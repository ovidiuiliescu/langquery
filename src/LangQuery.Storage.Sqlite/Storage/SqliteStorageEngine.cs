
using System.Diagnostics;
using System.Globalization;
using LangQuery.Core.Abstractions;
using LangQuery.Core.Models;
using Microsoft.Data.Sqlite;

namespace LangQuery.Storage.Sqlite.Storage;

public sealed class SqliteStorageEngine(ISqlSafetyValidator safetyValidator) : IStorageEngine
{
    private const int CurrentSchemaVersion = 5;
    private const int BusyTimeoutMs = 250;
    private const int MaxBusyRetries = 3;
    private const string OwnershipCapabilityKey = "owner";
    private const string OwnershipCapabilityValue = "langquery";

    public async Task InitializeAsync(string databasePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        EnsureParentDirectory(databasePath);

        await ExecuteWithLockRetryAsync(async token =>
        {
            await using var connection = CreateConnection(databasePath, readOnly: false);
            await connection.OpenAsync(token).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, readOnly: false, token).ConfigureAwait(false);
            await EnsureDatabaseOwnershipAsync(connection, databasePath, token).ConfigureAwait(false);

            var version = await GetSchemaVersionInternalAsync(connection, token).ConfigureAwait(false);
            EnsureSchemaVersionIsSupported(version, databasePath);
            if (version < CurrentSchemaVersion)
            {
                await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                try
                {
                    await ApplyMigrationsAsync(connection, tx, token).ConfigureAwait(false);
                    await SetSchemaVersionAsync(connection, tx, CurrentSchemaVersion, token).ConfigureAwait(false);
                    await tx.CommitAsync(token).ConfigureAwait(false);
                }
                catch
                {
                    await tx.RollbackAsync(token).ConfigureAwait(false);
                    throw;
                }
            }

            await SeedCapabilitiesAsync(connection, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task InitializeReadOnlyAsync(string databasePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Database '{fullPath}' does not exist.", fullPath);
        }

        await ExecuteWithLockRetryAsync(async token =>
        {
            await using var connection = CreateConnection(databasePath, readOnly: true);
            await connection.OpenAsync(token).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, readOnly: true, token).ConfigureAwait(false);

            var version = await GetSchemaVersionInternalAsync(connection, token).ConfigureAwait(false);
            EnsureSchemaVersionIsSupported(version, databasePath);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetIndexedFileHashesAsync(string databasePath, CancellationToken cancellationToken)
    {
        return await ExecuteWithLockRetryAsync(async token =>
        {
            await using var connection = CreateConnection(databasePath, readOnly: true);
            await connection.OpenAsync(token).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, readOnly: true, token).ConfigureAwait(false);

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT path, hash FROM files;";

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                result[reader.GetString(0)] = reader.GetString(1);
            }

            return (IReadOnlyDictionary<string, string>)result;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task PersistFactsAsync(
        string databasePath,
        IReadOnlyList<FileFacts> facts,
        IReadOnlyCollection<string> removedPaths,
        bool fullRebuild,
        CancellationToken cancellationToken)
    {
        await ExecuteWithLockRetryAsync(async token =>
        {
            await using var connection = CreateConnection(databasePath, readOnly: false);
            await connection.OpenAsync(token).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, readOnly: false, token).ConfigureAwait(false);

            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);

            if (fullRebuild)
            {
                await ExecuteAsync(connection, tx, "DELETE FROM files;", token).ConfigureAwait(false);
            }
            else
            {
                foreach (var removedPath in removedPaths)
                {
                    await ExecuteAsync(connection, tx, "DELETE FROM files WHERE path = $path;", token, ("$path", removedPath)).ConfigureAwait(false);
                }
            }

            foreach (var fileFacts in facts)
            {
                token.ThrowIfCancellationRequested();
                var fileId = await UpsertFileAsync(connection, tx, fileFacts, token).ConfigureAwait(false);
                await DeleteFactsForFileAsync(connection, tx, fileId, token).ConfigureAwait(false);

                var typeIds = await InsertTypesAsync(connection, tx, fileId, fileFacts.Types, token).ConfigureAwait(false);
                await InsertTypeInheritancesAsync(connection, tx, fileFacts.TypeInheritances, typeIds, token).ConfigureAwait(false);
                var methodIds = await InsertMethodsAsync(connection, tx, fileId, fileFacts.Methods, typeIds, token).ConfigureAwait(false);
                var lineIds = await InsertLinesAsync(connection, tx, fileId, fileFacts.Lines, methodIds, token).ConfigureAwait(false);
                var variableIds = await InsertVariablesAsync(connection, tx, fileFacts.Variables, methodIds, token).ConfigureAwait(false);

                await InsertLineVariablesAsync(connection, tx, fileFacts.LineVariables, lineIds, variableIds, token).ConfigureAwait(false);
                await InsertInvocationsAsync(connection, tx, fileFacts.Invocations, methodIds, token).ConfigureAwait(false);
                await InsertSymbolRefsAsync(connection, tx, fileId, fileFacts.SymbolReferences, methodIds, token).ConfigureAwait(false);
            }

            await UpsertScanStateAsync(connection, tx, "last_scan_utc", DateTimeOffset.UtcNow.ToString("O"), token).ConfigureAwait(false);
            await UpsertScanStateAsync(connection, tx, "scanned_files", facts.Count.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
            await UpsertScanStateAsync(connection, tx, "removed_files", removedPaths.Count.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);

            await tx.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QueryResult> ExecuteReadOnlyQueryAsync(QueryOptions options, CancellationToken cancellationToken)
    {
        var validation = safetyValidator.Validate(options.Sql);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.ErrorMessage ?? "Invalid SQL.");
        }

        return await ExecuteWithLockRetryAsync(async token =>
        {
            var stopwatch = Stopwatch.StartNew();
            await using var connection = CreateConnection(options.DatabasePath, readOnly: true);
            await connection.OpenAsync(token).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, readOnly: true, token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = options.Sql;
            command.CommandTimeout = 0;

            var columns = new List<string>();
            var normalizedColumns = new List<string>();
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            var truncated = false;
            var maxRows = Math.Max(1, options.MaxRows);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, options.TimeoutMs)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            var executionToken = linkedCts.Token;

            try
            {
                await using var reader = await command.ExecuteReaderAsync(executionToken).ConfigureAwait(false);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    columns.Add(reader.GetName(i));
                }

                normalizedColumns.AddRange(BuildUniqueColumnNames(columns));

                while (await reader.ReadAsync(executionToken).ConfigureAwait(false))
                {
                    if (rows.Count >= maxRows)
                    {
                        truncated = true;
                        break;
                    }

                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        row[normalizedColumns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }

                    rows.Add(row);
                }
            }
            catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                throw new TimeoutException($"SQL query timed out after {Math.Max(1, options.TimeoutMs)} ms.", ex);
            }

            stopwatch.Stop();
            return new QueryResult(normalizedColumns, rows, truncated, stopwatch.Elapsed);
        }, cancellationToken).ConfigureAwait(false);
    }
    public async Task<SchemaDescription> DescribeSchemaAsync(string databasePath, CancellationToken cancellationToken)
    {
        return await ExecuteWithLockRetryAsync(async token =>
        {
            await using var connection = CreateConnection(databasePath, readOnly: true);
            await connection.OpenAsync(token).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, readOnly: true, token).ConfigureAwait(false);

            var version = await GetSchemaVersionInternalAsync(connection, token).ConfigureAwait(false);
            EnsureSchemaVersionIsSupported(version, databasePath);
            var entities = new List<SchemaEntity>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name, type, IFNULL(sql, '') FROM sqlite_master WHERE (name LIKE 'v1_%' OR name LIKE 'meta_%') AND type IN ('table', 'view') ORDER BY type, name;";

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var kind = reader.GetString(1);
                var sql = reader.GetString(2);
                var columns = await DescribeColumnsAsync(connection, name, token).ConfigureAwait(false);
                entities.Add(new SchemaEntity(name, kind, sql, columns));
            }

            return new SchemaDescription(version, entities);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> GetSchemaVersionAsync(string databasePath, CancellationToken cancellationToken)
    {
        return await ExecuteWithLockRetryAsync(async token =>
        {
            await using var connection = CreateConnection(databasePath, readOnly: true);
            await connection.OpenAsync(token).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, readOnly: true, token).ConfigureAwait(false);
            var version = await GetSchemaVersionInternalAsync(connection, token).ConfigureAwait(false);
            EnsureSchemaVersionIsSupported(version, databasePath);
            return version;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertScanStateAsync(SqliteConnection connection, SqliteTransaction tx, string key, string value, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            tx,
            "INSERT INTO meta_scan_state(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            cancellationToken,
            ("$key", key),
            ("$value", value)).ConfigureAwait(false);
    }

    private static async Task<long> UpsertFileAsync(SqliteConnection connection, SqliteTransaction tx, FileFacts facts, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            tx,
            "INSERT INTO files(path, hash, language, indexed_utc) VALUES ($path, $hash, $language, $indexedUtc) ON CONFLICT(path) DO UPDATE SET hash = excluded.hash, language = excluded.language, indexed_utc = excluded.indexed_utc;",
            cancellationToken,
            ("$path", facts.Path),
            ("$hash", facts.Hash),
            ("$language", facts.Language),
            ("$indexedUtc", DateTimeOffset.UtcNow.ToString("O"))).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT id FROM files WHERE path = $path;";
        command.Parameters.AddWithValue("$path", facts.Path);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task DeleteFactsForFileAsync(SqliteConnection connection, SqliteTransaction tx, long fileId, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, tx, "DELETE FROM types WHERE file_id = $fileId;", cancellationToken, ("$fileId", fileId)).ConfigureAwait(false);
        await ExecuteAsync(connection, tx, "DELETE FROM methods WHERE file_id = $fileId;", cancellationToken, ("$fileId", fileId)).ConfigureAwait(false);
        await ExecuteAsync(connection, tx, "DELETE FROM lines WHERE file_id = $fileId;", cancellationToken, ("$fileId", fileId)).ConfigureAwait(false);
        await ExecuteAsync(connection, tx, "DELETE FROM symbol_refs WHERE file_id = $fileId;", cancellationToken, ("$fileId", fileId)).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, long>> InsertTypesAsync(SqliteConnection connection, SqliteTransaction tx, long fileId, IReadOnlyList<TypeFact> items, CancellationToken cancellationToken)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO types(file_id, type_key, name, kind, access_modifier, modifiers, full_name, line, column) VALUES ($fileId, $typeKey, $name, $kind, $accessModifier, $modifiers, $fullName, $line, $column); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$fileId", fileId);
            command.Parameters.AddWithValue("$typeKey", item.TypeKey);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$kind", item.Kind);
            command.Parameters.AddWithValue("$accessModifier", item.AccessModifier);
            command.Parameters.AddWithValue("$modifiers", item.Modifiers);
            command.Parameters.AddWithValue("$fullName", item.FullName);
            command.Parameters.AddWithValue("$line", item.Line);
            command.Parameters.AddWithValue("$column", item.Column);
            var id = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            ids[item.TypeKey] = Convert.ToInt64(id, CultureInfo.InvariantCulture);
        }

        return ids;
    }

    private static async Task<Dictionary<string, long>> InsertMethodsAsync(SqliteConnection connection, SqliteTransaction tx, long fileId, IReadOnlyList<MethodFact> items, IReadOnlyDictionary<string, long> typeIds, CancellationToken cancellationToken)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO methods(file_id, type_id, method_key, name, return_type, parameters, parameter_count, access_modifier, modifiers, implementation_kind, parent_method_key, line_start, line_end, column_start, column_end) VALUES ($fileId, $typeId, $methodKey, $name, $returnType, $parameters, $parameterCount, $accessModifier, $modifiers, $implementationKind, $parentMethodKey, $lineStart, $lineEnd, $columnStart, $columnEnd); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$fileId", fileId);
            command.Parameters.AddWithValue("$typeId", item.TypeKey is not null && typeIds.TryGetValue(item.TypeKey, out var typeId) ? typeId : DBNull.Value);
            command.Parameters.AddWithValue("$methodKey", item.MethodKey);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$returnType", item.ReturnType);
            command.Parameters.AddWithValue("$parameters", item.Parameters);
            command.Parameters.AddWithValue("$parameterCount", item.ParameterCount);
            command.Parameters.AddWithValue("$accessModifier", item.AccessModifier);
            command.Parameters.AddWithValue("$modifiers", item.Modifiers);
            command.Parameters.AddWithValue("$implementationKind", item.ImplementationKind);
            command.Parameters.AddWithValue("$parentMethodKey", item.ParentMethodKey ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$lineStart", item.LineStart);
            command.Parameters.AddWithValue("$lineEnd", item.LineEnd);
            command.Parameters.AddWithValue("$columnStart", item.ColumnStart);
            command.Parameters.AddWithValue("$columnEnd", item.ColumnEnd);
            var id = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            ids[item.MethodKey] = Convert.ToInt64(id, CultureInfo.InvariantCulture);
        }

        return ids;
    }

    private static async Task InsertTypeInheritancesAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        IReadOnlyList<TypeInheritanceFact> items,
        IReadOnlyDictionary<string, long> typeIds,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            if (!typeIds.TryGetValue(item.TypeKey, out var typeId))
            {
                continue;
            }

            await ExecuteAsync(
                connection,
                tx,
                "INSERT INTO type_inheritances(type_id, base_type_name, relation_kind) VALUES ($typeId, $baseTypeName, $relationKind);",
                cancellationToken,
                ("$typeId", typeId),
                ("$baseTypeName", item.BaseTypeName),
                ("$relationKind", item.RelationKind)).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<int, long>> InsertLinesAsync(SqliteConnection connection, SqliteTransaction tx, long fileId, IReadOnlyList<LineFact> items, IReadOnlyDictionary<string, long> methodIds, CancellationToken cancellationToken)
    {
        var ids = new Dictionary<int, long>();
        foreach (var item in items)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO lines(file_id, method_id, line_number, text, block_depth_in_method, variable_count) VALUES ($fileId, $methodId, $lineNumber, $text, $depth, $variableCount); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$fileId", fileId);
            command.Parameters.AddWithValue("$methodId", item.MethodKey is not null && methodIds.TryGetValue(item.MethodKey, out var methodId) ? methodId : DBNull.Value);
            command.Parameters.AddWithValue("$lineNumber", item.LineNumber);
            command.Parameters.AddWithValue("$text", item.Text);
            command.Parameters.AddWithValue("$depth", item.BlockDepthInMethod);
            command.Parameters.AddWithValue("$variableCount", item.VariableCount);
            var id = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            ids[item.LineNumber] = Convert.ToInt64(id, CultureInfo.InvariantCulture);
        }

        return ids;
    }
    private static async Task<Dictionary<string, long>> InsertVariablesAsync(SqliteConnection connection, SqliteTransaction tx, IReadOnlyList<VariableFact> items, IReadOnlyDictionary<string, long> methodIds, CancellationToken cancellationToken)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!methodIds.TryGetValue(item.MethodKey, out var methodId))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO variables(method_id, variable_key, name, kind, type_name, declaration_line) VALUES ($methodId, $variableKey, $name, $kind, $typeName, $declarationLine); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$methodId", methodId);
            command.Parameters.AddWithValue("$variableKey", item.VariableKey);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$kind", item.Kind);
            command.Parameters.AddWithValue("$typeName", item.TypeName ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$declarationLine", item.DeclarationLine);
            var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            ids[item.VariableKey] = id;
            ids[$"{item.MethodKey}|{item.Name}"] = id;
        }

        return ids;
    }

    private static async Task InsertLineVariablesAsync(SqliteConnection connection, SqliteTransaction tx, IReadOnlyList<LineVariableFact> items, IReadOnlyDictionary<int, long> lineIds, IReadOnlyDictionary<string, long> variableIds, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            if (!lineIds.TryGetValue(item.LineNumber, out var lineId))
            {
                continue;
            }

            if (!TryResolveVariableId(item, variableIds, out var variableId))
            {
                continue;
            }

            await ExecuteAsync(connection, tx, "INSERT INTO line_variables(line_id, variable_id, variable_name) VALUES ($lineId, $variableId, $name);", cancellationToken, ("$lineId", lineId), ("$variableId", variableId), ("$name", item.VariableName)).ConfigureAwait(false);
        }
    }

    private static async Task InsertInvocationsAsync(SqliteConnection connection, SqliteTransaction tx, IReadOnlyList<InvocationFact> items, IReadOnlyDictionary<string, long> methodIds, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            if (!methodIds.TryGetValue(item.MethodKey, out var methodId))
            {
                continue;
            }

            await ExecuteAsync(connection, tx, "INSERT INTO invocations(method_id, line_number, expression, target_name) VALUES ($methodId, $line, $expression, $target);", cancellationToken, ("$methodId", methodId), ("$line", item.LineNumber), ("$expression", item.Expression), ("$target", item.TargetName)).ConfigureAwait(false);
        }
    }

    private static async Task InsertSymbolRefsAsync(SqliteConnection connection, SqliteTransaction tx, long fileId, IReadOnlyList<SymbolReferenceFact> items, IReadOnlyDictionary<string, long> methodIds, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            await ExecuteAsync(
                connection,
                tx,
                "INSERT INTO symbol_refs(file_id, method_id, line_number, symbol_name, symbol_kind, symbol_container_type_name, symbol_type_name) VALUES ($fileId, $methodId, $line, $name, $kind, $containerType, $symbolType);",
                cancellationToken,
                ("$fileId", fileId),
                ("$methodId", item.MethodKey is not null && methodIds.TryGetValue(item.MethodKey, out var methodId) ? methodId : DBNull.Value),
                ("$line", item.LineNumber),
                ("$name", item.SymbolName),
                ("$kind", item.SymbolKind),
                ("$containerType", item.SymbolContainerTypeName ?? (object)DBNull.Value),
                ("$symbolType", item.SymbolTypeName ?? (object)DBNull.Value)).ConfigureAwait(false);
        }
    }

    private static bool TryResolveVariableId(LineVariableFact item, IReadOnlyDictionary<string, long> ids, out long value)
    {
        if (item.VariableKey is not null && ids.TryGetValue(item.VariableKey, out value))
        {
            return true;
        }

        return ids.TryGetValue($"{item.MethodKey}|{item.VariableName}", out value);
    }

    private static async Task<IReadOnlyList<SchemaColumn>> DescribeColumnsAsync(SqliteConnection connection, string name, CancellationToken cancellationToken)
    {
        var columns = new List<SchemaColumn>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{name.Replace("'", "''", StringComparison.Ordinal)}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(new SchemaColumn(reader.GetString(reader.GetOrdinal("name")), reader.IsDBNull(reader.GetOrdinal("type")) ? string.Empty : reader.GetString(reader.GetOrdinal("type")), reader.GetInt64(reader.GetOrdinal("notnull")) == 1, reader.GetInt64(reader.GetOrdinal("pk")) == 1));
        }

        return columns;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? tx, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] args)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var arg in args)
        {
            command.Parameters.AddWithValue(arg.Name, arg.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationsAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        var setupStatements = new[]
        {
            "CREATE TABLE IF NOT EXISTS meta_schema_version(version INTEGER NOT NULL, applied_utc TEXT NOT NULL);",
            "CREATE TABLE IF NOT EXISTS meta_capabilities(key TEXT PRIMARY KEY, value TEXT NOT NULL);",
            "CREATE TABLE IF NOT EXISTS meta_scan_state(key TEXT PRIMARY KEY, value TEXT NOT NULL);",
            "CREATE TABLE IF NOT EXISTS files(id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT NOT NULL UNIQUE, hash TEXT NOT NULL, language TEXT NOT NULL, indexed_utc TEXT NOT NULL);",
            "CREATE TABLE IF NOT EXISTS types(id INTEGER PRIMARY KEY AUTOINCREMENT, file_id INTEGER NOT NULL, type_key TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, access_modifier TEXT NOT NULL DEFAULT 'Internal', modifiers TEXT NOT NULL DEFAULT '', full_name TEXT NOT NULL, line INTEGER NOT NULL, column INTEGER NOT NULL, FOREIGN KEY(file_id) REFERENCES files(id) ON DELETE CASCADE);",
            "CREATE TABLE IF NOT EXISTS methods(id INTEGER PRIMARY KEY AUTOINCREMENT, file_id INTEGER NOT NULL, type_id INTEGER NULL, method_key TEXT NOT NULL, name TEXT NOT NULL, return_type TEXT NOT NULL, parameters TEXT NOT NULL DEFAULT '', parameter_count INTEGER NOT NULL DEFAULT 0, access_modifier TEXT NOT NULL DEFAULT 'Private', modifiers TEXT NOT NULL DEFAULT '', implementation_kind TEXT NOT NULL DEFAULT 'Method', parent_method_key TEXT NULL, line_start INTEGER NOT NULL, line_end INTEGER NOT NULL, column_start INTEGER NOT NULL, column_end INTEGER NOT NULL, FOREIGN KEY(file_id) REFERENCES files(id) ON DELETE CASCADE, FOREIGN KEY(type_id) REFERENCES types(id) ON DELETE SET NULL);",
            "CREATE TABLE IF NOT EXISTS type_inheritances(id INTEGER PRIMARY KEY AUTOINCREMENT, type_id INTEGER NOT NULL, base_type_name TEXT NOT NULL, relation_kind TEXT NOT NULL, FOREIGN KEY(type_id) REFERENCES types(id) ON DELETE CASCADE);",
            "CREATE TABLE IF NOT EXISTS lines(id INTEGER PRIMARY KEY AUTOINCREMENT, file_id INTEGER NOT NULL, method_id INTEGER NULL, line_number INTEGER NOT NULL, text TEXT NOT NULL, block_depth_in_method INTEGER NOT NULL, variable_count INTEGER NOT NULL, FOREIGN KEY(file_id) REFERENCES files(id) ON DELETE CASCADE, FOREIGN KEY(method_id) REFERENCES methods(id) ON DELETE CASCADE, UNIQUE(file_id, line_number));",
            "CREATE TABLE IF NOT EXISTS variables(id INTEGER PRIMARY KEY AUTOINCREMENT, method_id INTEGER NOT NULL, variable_key TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, type_name TEXT NULL, declaration_line INTEGER NOT NULL, FOREIGN KEY(method_id) REFERENCES methods(id) ON DELETE CASCADE);",
            "CREATE TABLE IF NOT EXISTS line_variables(id INTEGER PRIMARY KEY AUTOINCREMENT, line_id INTEGER NOT NULL, variable_id INTEGER NOT NULL, variable_name TEXT NOT NULL, FOREIGN KEY(line_id) REFERENCES lines(id) ON DELETE CASCADE, FOREIGN KEY(variable_id) REFERENCES variables(id) ON DELETE CASCADE);",
            "CREATE TABLE IF NOT EXISTS invocations(id INTEGER PRIMARY KEY AUTOINCREMENT, method_id INTEGER NOT NULL, line_number INTEGER NOT NULL, expression TEXT NOT NULL, target_name TEXT NOT NULL, FOREIGN KEY(method_id) REFERENCES methods(id) ON DELETE CASCADE);",
            "CREATE TABLE IF NOT EXISTS symbol_refs(id INTEGER PRIMARY KEY AUTOINCREMENT, file_id INTEGER NOT NULL, method_id INTEGER NULL, line_number INTEGER NOT NULL, symbol_name TEXT NOT NULL, symbol_kind TEXT NOT NULL, symbol_container_type_name TEXT NULL, symbol_type_name TEXT NULL, FOREIGN KEY(file_id) REFERENCES files(id) ON DELETE CASCADE, FOREIGN KEY(method_id) REFERENCES methods(id) ON DELETE CASCADE);",
            "CREATE INDEX IF NOT EXISTS idx_lines_method_depth ON lines(method_id, block_depth_in_method, variable_count);",
            "CREATE INDEX IF NOT EXISTS idx_type_inheritances_type ON type_inheritances(type_id, base_type_name, relation_kind);",
            "CREATE INDEX IF NOT EXISTS idx_line_variables_line ON line_variables(line_id, variable_name);",
            "CREATE INDEX IF NOT EXISTS idx_variables_method_name ON variables(method_id, name);"
        };

        foreach (var sql in setupStatements)
        {
            await ExecuteAsync(connection, tx, sql, cancellationToken).ConfigureAwait(false);
        }

        await EnsureColumnExistsAsync(connection, tx, "types", "access_modifier", "TEXT NOT NULL DEFAULT 'Internal'", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, tx, "types", "modifiers", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, tx, "methods", "access_modifier", "TEXT NOT NULL DEFAULT 'Private'", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, tx, "methods", "modifiers", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, tx, "methods", "parameters", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, tx, "methods", "parameter_count", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, tx, "methods", "implementation_kind", "TEXT NOT NULL DEFAULT 'Method'", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, tx, "methods", "parent_method_key", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, tx, "symbol_refs", "symbol_container_type_name", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, tx, "symbol_refs", "symbol_type_name", "TEXT NULL", cancellationToken).ConfigureAwait(false);

        var viewStatements = new[]
        {
            "DROP VIEW IF EXISTS v1_files;",
            "DROP VIEW IF EXISTS v1_types;",
            "DROP VIEW IF EXISTS v1_methods;",
            "DROP VIEW IF EXISTS v1_type_inheritances;",
            "DROP VIEW IF EXISTS v1_lines;",
            "DROP VIEW IF EXISTS v1_variables;",
            "DROP VIEW IF EXISTS v1_line_variables;",
            "DROP VIEW IF EXISTS v1_invocations;",
            "DROP VIEW IF EXISTS v1_symbol_refs;",
            "CREATE VIEW v1_files AS SELECT id AS file_id, path AS file_path, hash, language, indexed_utc FROM files;",
            "CREATE VIEW v1_types AS SELECT t.id AS type_id, f.id AS file_id, f.path AS file_path, t.type_key, t.name, t.kind, t.access_modifier, t.modifiers, t.full_name, t.line, t.column FROM types t JOIN files f ON f.id=t.file_id;",
            "CREATE VIEW v1_methods AS SELECT m.id AS method_id, f.id AS file_id, f.path AS file_path, m.type_id, m.method_key, m.name, m.return_type, m.parameters, m.parameter_count, m.access_modifier, m.modifiers, m.implementation_kind, m.parent_method_key, m.line_start, m.line_end, m.column_start, m.column_end FROM methods m JOIN files f ON f.id=m.file_id;",
            "CREATE VIEW v1_type_inheritances AS SELECT ti.id AS type_inheritance_id, t.id AS type_id, f.id AS file_id, f.path AS file_path, t.type_key, t.name AS type_name, t.full_name AS type_full_name, ti.base_type_name, ti.relation_kind FROM type_inheritances ti JOIN types t ON t.id=ti.type_id JOIN files f ON f.id=t.file_id;",
            "CREATE VIEW v1_lines AS SELECT l.id AS line_id, f.id AS file_id, f.path AS file_path, l.method_id, m.method_key, m.name AS method_name, l.line_number, l.text, l.block_depth_in_method, l.variable_count FROM lines l JOIN files f ON f.id=l.file_id LEFT JOIN methods m ON m.id=l.method_id;",
            "CREATE VIEW v1_variables AS SELECT v.id AS variable_id, m.id AS method_id, f.id AS file_id, f.path AS file_path, m.method_key, m.name AS method_name, v.variable_key, v.name, v.kind, v.type_name, v.declaration_line FROM variables v JOIN methods m ON m.id=v.method_id JOIN files f ON f.id=m.file_id;",
            "CREATE VIEW v1_line_variables AS SELECT lv.id AS line_variable_id, l.id AS line_id, l.file_id, f.path AS file_path, l.method_id, m.method_key, l.line_number, lv.variable_name, lv.variable_id, v.variable_key FROM line_variables lv JOIN lines l ON l.id=lv.line_id JOIN files f ON f.id=l.file_id LEFT JOIN methods m ON m.id=l.method_id LEFT JOIN variables v ON v.id=lv.variable_id;",
            "CREATE VIEW v1_invocations AS SELECT i.id AS invocation_id, m.id AS method_id, f.id AS file_id, f.path AS file_path, m.method_key, i.line_number, i.expression, i.target_name FROM invocations i JOIN methods m ON m.id=i.method_id JOIN files f ON f.id=m.file_id;",
            "CREATE VIEW v1_symbol_refs AS SELECT sr.id AS symbol_ref_id, sr.file_id, f.path AS file_path, sr.method_id, m.method_key, sr.line_number, sr.symbol_name, sr.symbol_kind, sr.symbol_container_type_name, sr.symbol_type_name FROM symbol_refs sr JOIN files f ON f.id=sr.file_id LEFT JOIN methods m ON m.id=sr.method_id;"
        };

        foreach (var sql in viewStatements)
        {
            await ExecuteAsync(connection, tx, sql, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureColumnExistsAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"PRAGMA table_info('{tableName.Replace("'", "''", StringComparison.Ordinal)}');";

        var exists = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var current = reader.GetString(reader.GetOrdinal("name"));
            if (string.Equals(current, columnName, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }

        if (exists)
        {
            return;
        }

        await ExecuteAsync(
            connection,
            tx,
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};",
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> BuildUniqueColumnNames(IReadOnlyList<string> columns)
    {
        var uniqueNames = new List<string>(columns.Count);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenByBaseName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < columns.Count; i++)
        {
            var baseName = string.IsNullOrWhiteSpace(columns[i]) ? $"column_{i + 1}" : columns[i];
            if (!seenByBaseName.TryGetValue(baseName, out var seenCount))
            {
                seenByBaseName[baseName] = 1;
                uniqueNames.Add(baseName);
                usedNames.Add(baseName);
                continue;
            }

            var suffix = seenCount + 1;
            var candidate = $"{baseName}_{suffix}";
            while (!usedNames.Add(candidate))
            {
                suffix++;
                candidate = $"{baseName}_{suffix}";
            }

            seenByBaseName[baseName] = suffix;
            uniqueNames.Add(candidate);
        }

        return uniqueNames;
    }

    private static async Task SetSchemaVersionAsync(SqliteConnection connection, SqliteTransaction tx, int version, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, tx, "DELETE FROM meta_schema_version;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, tx, "INSERT INTO meta_schema_version(version, applied_utc) VALUES ($version, $appliedUtc);", cancellationToken, ("$version", version), ("$appliedUtc", DateTimeOffset.UtcNow.ToString("O"))).ConfigureAwait(false);
    }

    private static async Task EnsureDatabaseOwnershipAsync(SqliteConnection connection, string databasePath, CancellationToken cancellationToken)
    {
        if (await IsDatabaseEmptyAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (await HasOwnershipMarkerAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (await HasLegacyLangQuerySignatureAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        throw new InvalidOperationException($"Database '{Path.GetFullPath(databasePath)}' is not a LangQuery database. Refusing to modify an existing non-LangQuery SQLite file. Choose a new '--db' path for LangQuery.");
    }

    private static async Task<bool> IsDatabaseEmptyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND type IN ('table', 'view', 'index', 'trigger');";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return count == 0;
    }

    private static async Task<bool> HasOwnershipMarkerAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "meta_capabilities", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta_capabilities WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", OwnershipCapabilityKey);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null || value is DBNull)
        {
            return false;
        }

        return string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), OwnershipCapabilityValue, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> HasLegacyLangQuerySignatureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var requiredTables = new[] { "meta_schema_version", "meta_capabilities", "meta_scan_state", "files", "types", "methods", "lines", "variables", "line_variables", "invocations", "symbol_refs" };
        foreach (var table in requiredTables)
        {
            if (!await TableExistsAsync(connection, table, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return count == 1;
    }

    private static async Task<int> GetSchemaVersionInternalAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='meta_schema_version';";
        var count = Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (count == 0)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM meta_schema_version ORDER BY version DESC LIMIT 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null || value is DBNull)
        {
            return 0;
        }

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static SqliteConnection CreateConnection(string databasePath, bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 1,
            ForeignKeys = !readOnly
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, bool readOnly, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, null, $"PRAGMA busy_timeout = {BusyTimeoutMs};", cancellationToken).ConfigureAwait(false);
        if (readOnly)
        {
            return;
        }

        await ExecuteAsync(connection, null, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureSchemaVersionIsSupported(int version, string databasePath)
    {
        if (version <= CurrentSchemaVersion)
        {
            return;
        }

        throw new InvalidOperationException($"Database '{Path.GetFullPath(databasePath)}' schema version {version} is newer than the maximum supported version {CurrentSchemaVersion}. Upgrade LangQuery to continue.");
    }

    private static Task ExecuteWithLockRetryAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        return ExecuteWithLockRetryAsync(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private static async Task<T> ExecuteWithLockRetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsLockContention(ex) && attempt < MaxBusyRetries)
            {
                await Task.Delay(GetRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsLockContention(SqliteException exception)
    {
        return exception.SqliteErrorCode == 5 || exception.SqliteErrorCode == 6;
    }

    private static TimeSpan GetRetryDelay(int attempt)
    {
        var delayMs = (attempt + 1) * 100;
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private static void EnsureParentDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static async Task SeedCapabilitiesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, null, "INSERT OR REPLACE INTO meta_capabilities(key, value) VALUES ('sql_mode', 'read-only');", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "INSERT OR REPLACE INTO meta_capabilities(key, value) VALUES ('public_views', 'v1');", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "INSERT OR REPLACE INTO meta_capabilities(key, value) VALUES ('languages', 'csharp');", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, $"INSERT OR REPLACE INTO meta_capabilities(key, value) VALUES ('{OwnershipCapabilityKey}', '{OwnershipCapabilityValue}');", cancellationToken).ConfigureAwait(false);
    }
}
