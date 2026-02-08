using LangQuery.Core.Models;
using LangQuery.Query.Validation;
using LangQuery.Storage.Sqlite.Storage;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Globalization;

namespace LangQuery.UnitTests;

public sealed class SqliteStorageEngineTests
{
    [Fact]
    public async Task InitializeAsync_CreatesSchemaAndPublicViews()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();

            await storage.InitializeAsync(databasePath, CancellationToken.None);
            var schemaVersion = await storage.GetSchemaVersionAsync(databasePath, CancellationToken.None);
            var schema = await storage.DescribeSchemaAsync(databasePath, CancellationToken.None);

            Assert.Equal(5, schemaVersion);
            Assert.Contains(schema.Entities, entity => entity.Name == "meta_schema_version" && entity.Kind == "table");
            Assert.Contains(schema.Entities, entity => entity.Name == "v1_files" && entity.Kind == "view");
            Assert.Contains(schema.Entities, entity => entity.Name == "v1_methods" && entity.Kind == "view");
            Assert.Contains(schema.Entities, entity => entity.Name == "v1_type_inheritances" && entity.Kind == "view");

            var typeView = Assert.Single(schema.Entities, entity => entity.Name == "v1_types");
            Assert.Contains(typeView.Columns, column => string.Equals(column.Name, "access_modifier", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(typeView.Columns, column => string.Equals(column.Name, "modifiers", StringComparison.OrdinalIgnoreCase));

            var methodsView = Assert.Single(schema.Entities, entity => entity.Name == "v1_methods");
            Assert.Contains(methodsView.Columns, column => string.Equals(column.Name, "access_modifier", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(methodsView.Columns, column => string.Equals(column.Name, "modifiers", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(methodsView.Columns, column => string.Equals(column.Name, "parameters", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(methodsView.Columns, column => string.Equals(column.Name, "parameter_count", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(methodsView.Columns, column => string.Equals(column.Name, "implementation_kind", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(methodsView.Columns, column => string.Equals(column.Name, "parent_method_key", StringComparison.OrdinalIgnoreCase));

            var symbolRefsView = Assert.Single(schema.Entities, entity => entity.Name == "v1_symbol_refs");
            Assert.Contains(symbolRefsView.Columns, column => string.Equals(column.Name, "symbol_container_type_name", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(symbolRefsView.Columns, column => string.Equals(column.Name, "symbol_type_name", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteReadOnlyQueryAsync_AllowsSelectAndRejectsMutation()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var readResult = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "SELECT key, value FROM meta_capabilities ORDER BY key", MaxRows: 10),
                CancellationToken.None);

            Assert.Contains("key", readResult.Columns, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("value", readResult.Columns, StringComparer.OrdinalIgnoreCase);
            Assert.NotEmpty(readResult.Rows);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                storage.ExecuteReadOnlyQueryAsync(new QueryOptions(databasePath, "DELETE FROM meta_capabilities"), CancellationToken.None));

            Assert.Contains("read-only", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteReadOnlyQueryAsync_RespectsMaxRowsAndSetsTruncatedFlag()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var result = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "WITH RECURSIVE nums(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM nums WHERE value < 3) SELECT value FROM nums", MaxRows: 1),
                CancellationToken.None);

            Assert.True(result.Truncated);
            Assert.Single(result.Rows);
            Assert.Contains("value", result.Columns, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_CreatesParentDirectoryForDatabasePath()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var directory = Path.GetDirectoryName(databasePath);
            Assert.NotNull(directory);
            Assert.False(Directory.Exists(directory));

            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            Assert.True(Directory.Exists(directory));
            Assert.True(File.Exists(databasePath));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotentAndCapabilitiesRemainStable()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();

            await storage.InitializeAsync(databasePath, CancellationToken.None);
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var result = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "SELECT key, value FROM meta_capabilities ORDER BY key", MaxRows: 10),
                CancellationToken.None);

            Assert.Equal(4, result.Rows.Count);
            Assert.Contains(result.Rows, row => Equals(row["key"], "owner") && Equals(row["value"], "langquery"));
            Assert.Contains(result.Rows, row => Equals(row["key"], "languages") && Equals(row["value"], "csharp"));
            Assert.Contains(result.Rows, row => Equals(row["key"], "public_views") && Equals(row["value"], "v1"));
            Assert.Contains(result.Rows, row => Equals(row["key"], "sql_mode") && Equals(row["value"], "read-only"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetIndexedFileHashesAsync_ReturnsEmptyAfterInitialization()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var hashes = await storage.GetIndexedFileHashesAsync(databasePath, CancellationToken.None);

            Assert.Empty(hashes);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PersistFactsAsync_FullRebuildReplacesPreviouslyIndexedFiles()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            await storage.PersistFactsAsync(
                databasePath,
                [CreateSimpleFacts("First.cs", "HASH1")],
                removedPaths: Array.Empty<string>(),
                fullRebuild: true,
                CancellationToken.None);

            await storage.PersistFactsAsync(
                databasePath,
                [CreateSimpleFacts("Second.cs", "HASH2")],
                removedPaths: Array.Empty<string>(),
                fullRebuild: true,
                CancellationToken.None);

            var query = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "SELECT file_path FROM v1_files ORDER BY file_path", MaxRows: 10),
                CancellationToken.None);

            Assert.Single(query.Rows);
            Assert.Equal(Path.GetFullPath("Second.cs"), Convert.ToString(query.Rows[0]["file_path"], CultureInfo.InvariantCulture));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PersistFactsAsync_ChangedOnlyRemovesDeletedPaths()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var firstPath = Path.GetFullPath("First.cs");
            var secondPath = Path.GetFullPath("Second.cs");

            await storage.PersistFactsAsync(
                databasePath,
                [CreateSimpleFacts(firstPath, "HASH1"), CreateSimpleFacts(secondPath, "HASH2")],
                removedPaths: Array.Empty<string>(),
                fullRebuild: true,
                CancellationToken.None);

            await storage.PersistFactsAsync(
                databasePath,
                facts: Array.Empty<FileFacts>(),
                removedPaths: [firstPath],
                fullRebuild: false,
                CancellationToken.None);

            var query = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "SELECT file_path FROM v1_files ORDER BY file_path", MaxRows: 10),
                CancellationToken.None);

            Assert.Single(query.Rows);
            Assert.Equal(secondPath, Convert.ToString(query.Rows[0]["file_path"], CultureInfo.InvariantCulture));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PersistFactsAsync_UpsertsExistingFileHash()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var path = Path.GetFullPath("Tracked.cs");

            await storage.PersistFactsAsync(
                databasePath,
                [CreateSimpleFacts(path, "OLD_HASH")],
                removedPaths: Array.Empty<string>(),
                fullRebuild: true,
                CancellationToken.None);

            await storage.PersistFactsAsync(
                databasePath,
                [CreateSimpleFacts(path, "NEW_HASH")],
                removedPaths: Array.Empty<string>(),
                fullRebuild: false,
                CancellationToken.None);

            var hashes = await storage.GetIndexedFileHashesAsync(databasePath, CancellationToken.None);

            Assert.Equal("NEW_HASH", hashes[path]);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PersistFactsAsync_StoresScanStateValues()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            await storage.PersistFactsAsync(
                databasePath,
                [CreateSimpleFacts("Tracked.cs", "HASH")],
                removedPaths: [Path.GetFullPath("Removed.cs")],
                fullRebuild: false,
                CancellationToken.None);

            var scanState = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "SELECT key, value FROM meta_scan_state ORDER BY key", MaxRows: 10),
                CancellationToken.None);

            Assert.Contains(scanState.Rows, row => Equals(row["key"], "scanned_files") && Equals(row["value"], "1"));
            Assert.Contains(scanState.Rows, row => Equals(row["key"], "removed_files") && Equals(row["value"], "1"));
            Assert.Contains(scanState.Rows, row => Equals(row["key"], "last_scan_utc"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PersistFactsAsync_PersistsTypeInheritanceRows()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var facts = CreateSimpleFacts("Inheritance.cs", "HASH", includeInheritance: true);
            await storage.PersistFactsAsync(databasePath, [facts], Array.Empty<string>(), fullRebuild: true, CancellationToken.None);

            var query = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "SELECT type_name, base_type_name, relation_kind FROM v1_type_inheritances", MaxRows: 10),
                CancellationToken.None);

            Assert.Contains(query.Rows, row =>
                Equals(row["type_name"], "Widget")
                && Equals(row["base_type_name"], "BaseWidget")
                && Equals(row["relation_kind"], "BaseType"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PersistFactsAsync_PersistsNestedMethodsAndParentLinks()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var facts = CreateFactsWithNestedMethod("Nested.cs", "HASH");
            await storage.PersistFactsAsync(databasePath, [facts], Array.Empty<string>(), fullRebuild: true, CancellationToken.None);

            var query = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "SELECT implementation_kind, parent_method_key, access_modifier FROM v1_methods ORDER BY implementation_kind", MaxRows: 10),
                CancellationToken.None);

            Assert.Contains(query.Rows, row => Equals(row["implementation_kind"], "Method") && row["parent_method_key"] is null);
            Assert.Contains(query.Rows, row => Equals(row["implementation_kind"], "LocalFunction") && row["parent_method_key"] is not null && Equals(row["access_modifier"], "Local"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PersistFactsAsync_PersistsSymbolReferenceTypeMetadata()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            await storage.PersistFactsAsync(
                databasePath,
                [CreateSimpleFacts("Symbols.cs", "HASH", includeSymbolMetadata: true)],
                Array.Empty<string>(),
                fullRebuild: true,
                CancellationToken.None);

            var query = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "SELECT symbol_name, symbol_kind, symbol_container_type_name, symbol_type_name FROM v1_symbol_refs WHERE symbol_name = 'Parameters'", MaxRows: 10),
                CancellationToken.None);

            Assert.Single(query.Rows);
            Assert.Equal("Property", Convert.ToString(query.Rows[0]["symbol_kind"], CultureInfo.InvariantCulture));
            Assert.Equal("SqliteCommand", Convert.ToString(query.Rows[0]["symbol_container_type_name"], CultureInfo.InvariantCulture));
            Assert.Equal("SqliteParameterCollection", Convert.ToString(query.Rows[0]["symbol_type_name"], CultureInfo.InvariantCulture));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteReadOnlyQueryAsync_ReturnsNullForNullValues()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var result = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "SELECT NULL AS maybe_null", MaxRows: 10),
                CancellationToken.None);

            Assert.Single(result.Rows);
            Assert.Null(result.Rows[0]["maybe_null"]);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteReadOnlyQueryAsync_UsesCaseInsensitiveRowLookup()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var result = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "SELECT 123 AS MixedCaseColumn", MaxRows: 10),
                CancellationToken.None);

            Assert.Equal(123L, Convert.ToInt64(result.Rows[0]["mixedcasecolumn"], CultureInfo.InvariantCulture));
            Assert.Equal(123L, Convert.ToInt64(result.Rows[0]["MIXEDCASECOLUMN"], CultureInfo.InvariantCulture));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteReadOnlyQueryAsync_WhenColumnsShareName_PreservesAllValuesInRow()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var result = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "SELECT 1 AS duplicate, 2 AS duplicate, 3 AS DUPLICATE", MaxRows: 10),
                CancellationToken.None);

            Assert.Equal(3, result.Columns.Count);
            Assert.Equal("duplicate", result.Columns[0]);
            Assert.Equal("duplicate_2", result.Columns[1]);
            Assert.Equal("DUPLICATE_3", result.Columns[2]);

            var row = Assert.Single(result.Rows);
            Assert.Equal(1L, Convert.ToInt64(row["duplicate"], CultureInfo.InvariantCulture));
            Assert.Equal(2L, Convert.ToInt64(row["duplicate_2"], CultureInfo.InvariantCulture));
            Assert.Equal(3L, Convert.ToInt64(row[result.Columns[2]], CultureInfo.InvariantCulture));
            Assert.All(result.Columns, column => Assert.True(row.ContainsKey(column)));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteReadOnlyQueryAsync_TimeoutMs_UsesMillisecondPrecision()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var stopwatch = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                storage.ExecuteReadOnlyQueryAsync(
                    new QueryOptions(
                        databasePath,
                        "WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 1000000000) SELECT x FROM cnt",
                        MaxRows: int.MaxValue,
                        TimeoutMs: 10),
                    CancellationToken.None));
            stopwatch.Stop();

            Assert.Contains("10 ms", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(900), $"Expected timeout near 10ms but elapsed {stopwatch.Elapsed.TotalMilliseconds:F0}ms.");
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteReadOnlyQueryAsync_WhenMaxRowsIsZero_StillReturnsSingleRowAndMarksTruncated()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var result = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "WITH RECURSIVE nums(v) AS (SELECT 1 UNION ALL SELECT v + 1 FROM nums WHERE v < 3) SELECT v FROM nums", MaxRows: 0),
                CancellationToken.None);

            Assert.Single(result.Rows);
            Assert.True(result.Truncated);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task DescribeSchemaAsync_IncludesMetaScanStateTable()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var schema = await storage.DescribeSchemaAsync(databasePath, CancellationToken.None);

            Assert.Contains(schema.Entities, entity => entity.Name == "meta_scan_state" && entity.Kind == "table");
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InitializeReadOnlyAsync_AllowsQueryAndSchemaOnReadOnlyDatabaseFile()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            File.SetAttributes(databasePath, File.GetAttributes(databasePath) | FileAttributes.ReadOnly);

            await storage.InitializeReadOnlyAsync(databasePath, CancellationToken.None);

            var query = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "SELECT key FROM meta_capabilities WHERE key = 'owner'", MaxRows: 10),
                CancellationToken.None);
            var schema = await storage.DescribeSchemaAsync(databasePath, CancellationToken.None);

            Assert.Single(query.Rows);
            Assert.Equal("owner", Convert.ToString(query.Rows[0]["key"], CultureInfo.InvariantCulture));
            Assert.Equal(5, schema.SchemaVersion);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                var attributes = File.GetAttributes(databasePath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(databasePath, attributes & ~FileAttributes.ReadOnly);
                }
            }

            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_WhenSchemaVersionIsNewerThanSupported_ThrowsFast()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await CreateDatabaseWithSchemaVersionAsync(databasePath, 999);
            var storage = CreateStorage();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.InitializeAsync(databasePath, CancellationToken.None));

            Assert.Contains("schema version", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("999", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InitializeReadOnlyAsync_WhenSchemaVersionIsNewerThanSupported_ThrowsFast()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await CreateDatabaseWithSchemaVersionAsync(databasePath, 999);
            var storage = CreateStorage();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.InitializeReadOnlyAsync(databasePath, CancellationToken.None));

            Assert.Contains("schema version", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("999", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PersistFactsAsync_WhenLockIsTransient_RetriesAndSucceeds()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            await using var lockConnection = new SqliteConnection($"Data Source={databasePath}");
            await lockConnection.OpenAsync(CancellationToken.None);
            await using var lockCommand = lockConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN EXCLUSIVE;";
            await lockCommand.ExecuteNonQueryAsync(CancellationToken.None);

            var releaseTask = Task.Run(async () =>
            {
                await Task.Delay(700).ConfigureAwait(false);
                await using var releaseCommand = lockConnection.CreateCommand();
                releaseCommand.CommandText = "ROLLBACK;";
                await releaseCommand.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            });

            await storage.PersistFactsAsync(
                databasePath,
                [CreateSimpleFacts("Locked.cs", "HASH")],
                Array.Empty<string>(),
                fullRebuild: false,
                CancellationToken.None);

            await releaseTask;
            var hashes = await storage.GetIndexedFileHashesAsync(databasePath, CancellationToken.None);
            Assert.Contains(Path.GetFullPath("Locked.cs"), hashes.Keys, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PersistFactsAsync_WhenLockPersists_FailsAfterBoundedRetries()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            await using var lockConnection = new SqliteConnection($"Data Source={databasePath}");
            await lockConnection.OpenAsync(CancellationToken.None);
            await using var lockCommand = lockConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN EXCLUSIVE;";
            await lockCommand.ExecuteNonQueryAsync(CancellationToken.None);

            var stopwatch = Stopwatch.StartNew();
            await Assert.ThrowsAsync<SqliteException>(() => storage.PersistFactsAsync(
                databasePath,
                [CreateSimpleFacts("LockedForever.cs", "HASH")],
                Array.Empty<string>(),
                fullRebuild: false,
                CancellationToken.None));
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(12), $"Expected bounded retries, elapsed {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");

            await using var releaseCommand = lockConnection.CreateCommand();
            releaseCommand.CommandText = "ROLLBACK;";
            await releaseCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_RejectsExistingNonLangQueryDatabase()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var parentDirectory = Path.GetDirectoryName(databasePath)
                                  ?? throw new DirectoryNotFoundException($"Could not derive parent directory from '{databasePath}'.");
            Directory.CreateDirectory(parentDirectory);

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync(CancellationToken.None);
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE notes(id INTEGER PRIMARY KEY, content TEXT NOT NULL);";
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }

            var storage = CreateStorage();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.InitializeAsync(databasePath, CancellationToken.None));

            Assert.Contains("not a LangQuery database", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--db", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_WhenMigrationFails_RollsBackPartialSchemaChanges()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var parentDirectory = Path.GetDirectoryName(databasePath)
                                  ?? throw new DirectoryNotFoundException($"Could not derive parent directory from '{databasePath}'.");
            Directory.CreateDirectory(parentDirectory);

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync(CancellationToken.None);
                await using var setup = connection.CreateCommand();
                setup.CommandText = "CREATE TABLE meta_schema_version(version INTEGER NOT NULL, applied_utc TEXT NOT NULL);"
                                  + "INSERT INTO meta_schema_version(version, applied_utc) VALUES (0, '1970-01-01T00:00:00.0000000+00:00');"
                                  + "CREATE TABLE meta_capabilities(key TEXT PRIMARY KEY, value TEXT NOT NULL);"
                                  + "INSERT INTO meta_capabilities(key, value) VALUES ('owner', 'langquery');"
                                  + "CREATE TABLE lines(id INTEGER PRIMARY KEY AUTOINCREMENT, file_id INTEGER NOT NULL, line_number INTEGER NOT NULL, text TEXT NOT NULL);";
                await setup.ExecuteNonQueryAsync(CancellationToken.None);
            }

            var storage = CreateStorage();
            await Assert.ThrowsAnyAsync<SqliteException>(() => storage.InitializeAsync(databasePath, CancellationToken.None));

            await using var verifyConnection = new SqliteConnection($"Data Source={databasePath}");
            await verifyConnection.OpenAsync(CancellationToken.None);

            await using var metaScanStateCommand = verifyConnection.CreateCommand();
            metaScanStateCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta_scan_state';";
            var metaScanStateCount = Convert.ToInt32(await metaScanStateCommand.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);

            await using var methodsCommand = verifyConnection.CreateCommand();
            methodsCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'methods';";
            var methodsCount = Convert.ToInt32(await methodsCommand.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);

            await using var schemaVersionCommand = verifyConnection.CreateCommand();
            schemaVersionCommand.CommandText = "SELECT version FROM meta_schema_version ORDER BY version DESC LIMIT 1;";
            var schemaVersion = Convert.ToInt32(await schemaVersionCommand.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);

            Assert.Equal(0, metaScanStateCount);
            Assert.Equal(0, methodsCount);
            Assert.Equal(0, schemaVersion);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static SqliteStorageEngine CreateStorage()
    {
        return new SqliteStorageEngine(new ReadOnlySqlSafetyValidator());
    }

    private static FileFacts CreateSimpleFacts(string relativePath, string hash, bool includeInheritance = false, bool includeSymbolMetadata = false)
    {
        var fullPath = Path.GetFullPath(relativePath);
        const string typeKey = "Sample.Widget@1";
        const string methodKey = "Sample.Widget@1:Method:Run@3:5";
        const string variableKey = "Sample.Widget@1:Method:Run@3:5:Parameter:value@3";

        var types = new[]
        {
            new TypeFact(typeKey, "Widget", "Class", "Public", "", "Sample.Widget", 1, 1)
        };

        var typeInheritances = includeInheritance
            ? new[] { new TypeInheritanceFact(typeKey, "BaseWidget", "BaseType") }
            : Array.Empty<TypeInheritanceFact>();

        var methods = new[]
        {
            new MethodFact(methodKey, "Run", "int", "int value", 1, "Public", "", "Method", null, typeKey, 3, 6, 5, 9)
        };

        var lines = new[]
        {
            new LineFact(4, "return value;", methodKey, 0, 1)
        };

        var variables = new[]
        {
            new VariableFact(variableKey, methodKey, "value", "Parameter", "int", 3)
        };

        var lineVariables = new[]
        {
            new LineVariableFact(4, methodKey, "value", variableKey)
        };

        var invocations = new[]
        {
            new InvocationFact(methodKey, 4, "System.Console.WriteLine(value)", "WriteLine")
        };

        var symbolReferences = includeSymbolMetadata
            ? new[]
            {
                new SymbolReferenceFact(4, methodKey, "value", "Variable", null, "Int32"),
                new SymbolReferenceFact(4, methodKey, "Parameters", "Property", "SqliteCommand", "SqliteParameterCollection")
            }
            : new[]
            {
                new SymbolReferenceFact(4, methodKey, "value", "Variable", null, "Int32")
            };

        return new FileFacts(
            fullPath,
            hash,
            "csharp",
            types,
            typeInheritances,
            methods,
            lines,
            variables,
            lineVariables,
            invocations,
            symbolReferences);
    }

    private static FileFacts CreateFactsWithNestedMethod(string relativePath, string hash)
    {
        var fullPath = Path.GetFullPath(relativePath);
        const string typeKey = "Sample.NestedWidget@1";
        const string rootMethod = "Sample.NestedWidget@1:Method:Run@3:5";
        const string nestedMethod = "Sample.NestedWidget@1:Method:Run@3:5:LocalFunction:LocalAdjust@4:9";

        return new FileFacts(
            fullPath,
            hash,
            "csharp",
            [new TypeFact(typeKey, "NestedWidget", "Class", "Public", "", "Sample.NestedWidget", 1, 1)],
            Array.Empty<TypeInheritanceFact>(),
            [
                new MethodFact(rootMethod, "Run", "int", "", 0, "Public", "", "Method", null, typeKey, 3, 9, 5, 9),
                new MethodFact(nestedMethod, "LocalAdjust", "int", "int value", 1, "Local", "", "LocalFunction", rootMethod, typeKey, 4, 7, 9, 9)
            ],
            [
                new LineFact(4, "int LocalAdjust(int value)", rootMethod, 0, 0),
                new LineFact(6, "return value;", nestedMethod, 1, 1)
            ],
            [new VariableFact("nested-value", nestedMethod, "value", "Parameter", "int", 4)],
            [new LineVariableFact(6, nestedMethod, "value", "nested-value")],
            Array.Empty<InvocationFact>(),
            [new SymbolReferenceFact(6, nestedMethod, "value", "Variable", null, "Int32")]);
    }

    private static string CreateTempDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "langquery-tests", Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "facts.db");
    }

    private static async Task CreateDatabaseWithSchemaVersionAsync(string databasePath, int version)
    {
        var parentDirectory = Path.GetDirectoryName(databasePath)
                              ?? throw new DirectoryNotFoundException($"Could not derive parent directory from '{databasePath}'.");
        Directory.CreateDirectory(parentDirectory);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE meta_schema_version(version INTEGER NOT NULL, applied_utc TEXT NOT NULL);"
            + "INSERT INTO meta_schema_version(version, applied_utc) VALUES ($version, '1970-01-01T00:00:00.0000000+00:00');"
            + "CREATE TABLE meta_capabilities(key TEXT PRIMARY KEY, value TEXT NOT NULL);"
            + "INSERT INTO meta_capabilities(key, value) VALUES ('owner', 'langquery');";
        command.Parameters.AddWithValue("$version", version);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static void DeleteTempDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
