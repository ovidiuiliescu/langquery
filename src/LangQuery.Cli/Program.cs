using System.Text.Json;
using System.Text.Encodings.Web;
using System.Reflection;
using System.Runtime.InteropServices;
using LangQuery.Core.Models;
using LangQuery.Core.Services;
using LangQuery.Query.Validation;
using LangQuery.Roslyn.Extraction;
using LangQuery.Storage.Sqlite.Storage;
using Microsoft.Data.Sqlite;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args)
{
    var parsed = ParseArgs(args);
    if (parsed.Error is not null)
    {
        WriteOutput(new
        {
            command = parsed.Command,
            success = false,
            error = parsed.Error,
            help = BuildHelpPayload()
        }, parsed.Pretty);
        return 1;
    }

    if (string.Equals(parsed.Command, "help", StringComparison.OrdinalIgnoreCase))
    {
        WriteOutput(new
        {
            command = "help",
            success = true,
            data = BuildHelpPayload()
        }, parsed.Pretty);
        return 0;
    }

    if (string.Equals(parsed.Command, "info", StringComparison.OrdinalIgnoreCase))
    {
        WriteOutput(BuildInfoPayload(), parsed.Pretty);
        return 0;
    }

    if (string.Equals(parsed.Command, "examples", StringComparison.OrdinalIgnoreCase))
    {
        WriteOutput(new
        {
            command = "examples",
            success = true,
            data = BuildExamplesPayload()
        }, parsed.Pretty);
        return 0;
    }

    var service = new LangQueryService(
        new CSharpCodeFactsExtractor(),
        new SqliteStorageEngine(new ReadOnlySqlSafetyValidator()));

    try
    {
        return parsed.Command switch
        {
            "scan" => await HandleScanAsync(parsed, service).ConfigureAwait(false),
            "sql" => await HandleSqlAsync(parsed, service).ConfigureAwait(false),
            "schema" => await HandleSchemaAsync(parsed, service).ConfigureAwait(false),
            "simpleschema" => await HandleSimpleSchemaAsync(parsed, service).ConfigureAwait(false),
            "exportjson" => await HandleExportJsonAsync(parsed, service).ConfigureAwait(false),
            "installskill" => await HandleInstallSkillAsync(parsed).ConfigureAwait(false),
            "info" => 0,
            _ => WriteUnknownCommand(parsed)
        };
    }
    catch (Exception ex)
    {
        WriteOutput(new
        {
            command = parsed.Command,
            success = false,
            error = ex.Message
        }, parsed.Pretty);
        return 1;
    }
}

static int WriteUnknownCommand(ParsedArgs parsed)
{
    WriteOutput(new
    {
        command = parsed.Command,
        success = false,
        error = $"Unknown command '{parsed.Command}'.",
        help = BuildHelpPayload()
    }, parsed.Pretty);

    return 1;
}

static async Task<int> HandleScanAsync(ParsedArgs parsed, LangQueryService service)
{
    if (!TryResolveSolution(parsed, "scan", out var solution))
    {
        return 1;
    }

    if (!TryGetDatabasePath(parsed, solution, out var databasePath))
    {
        return 1;
    }

    var changedOnly = parsed.Flags.Contains("changed-only");
    var result = await service.ScanAsync(new ScanOptions(solution.FilePath, databasePath, changedOnly), CancellationToken.None).ConfigureAwait(false);

    WriteOutput(new
    {
        command = "scan",
        success = true,
        data = result
    }, parsed.Pretty);

    return 0;
}

static async Task<int> HandleSqlAsync(ParsedArgs parsed, LangQueryService service)
{
    if (!parsed.Options.TryGetValue("query", out var sql) || string.IsNullOrWhiteSpace(sql))
    {
        WriteOutput(new { command = "sql", success = false, error = "Missing required option '--query <sql>'." }, parsed.Pretty);
        return 1;
    }

    ResolvedSolution? solution = null;
    if (!TryGetDatabasePath(parsed, solution, out var databasePath))
    {
        if (!TryResolveSolution(parsed, "sql", out var resolvedSolution))
        {
            return 1;
        }

        solution = resolvedSolution;
        if (!TryGetDatabasePath(parsed, solution, out databasePath))
        {
            return 1;
        }
    }

    var maxRows = TryGetIntOption(parsed, "max-rows", 1000, 1);
    var timeoutMs = TryGetIntOption(parsed, "timeout-ms", 10000, 1);
    if (maxRows is null || timeoutMs is null)
    {
        return 1;
    }

    if (!File.Exists(Path.GetFullPath(databasePath)))
    {
        if (solution is null)
        {
            if (!TryResolveSolution(parsed, "sql", out var resolvedSolution))
            {
                return 1;
            }

            solution = resolvedSolution;
        }

        await EnsureDatabaseReadyForQueryAsync(service, solution, databasePath).ConfigureAwait(false);
    }

    var result = await service.QueryAsync(new QueryOptions(databasePath, sql, maxRows.Value, timeoutMs.Value), CancellationToken.None).ConfigureAwait(false);
    WriteOutput(new
    {
        command = "sql",
        success = true,
        data = result
    }, parsed.Pretty);

    return 0;
}

static async Task<int> HandleSchemaAsync(ParsedArgs parsed, LangQueryService service)
{
    if (!TryGetDatabasePath(parsed, solution: null, out var databasePath))
    {
        if (!TryResolveSolution(parsed, "schema", out var solution))
        {
            return 1;
        }

        if (!TryGetDatabasePath(parsed, solution, out databasePath))
        {
            return 1;
        }
    }

    var result = await service.GetSchemaAsync(new SchemaOptions(databasePath), CancellationToken.None).ConfigureAwait(false);
    WriteOutput(new
    {
        command = "schema",
        success = true,
        data = result
    }, parsed.Pretty);

    return 0;
}

static async Task<int> HandleSimpleSchemaAsync(ParsedArgs parsed, LangQueryService service)
{
    if (!TryGetDatabasePath(parsed, solution: null, out var databasePath))
    {
        if (!TryResolveSolution(parsed, "simpleschema", out var solution))
        {
            return 1;
        }

        if (!TryGetDatabasePath(parsed, solution, out databasePath))
        {
            return 1;
        }
    }

    var schema = await service.GetSchemaAsync(new SchemaOptions(databasePath), CancellationToken.None).ConfigureAwait(false);
    var simpleSchema = BuildSimpleSchema(schema);

    WriteOutput(new
    {
        command = "simpleschema",
        success = true,
        data = simpleSchema
    }, parsed.Pretty);

    return 0;
}

static async Task<int> HandleExportJsonAsync(ParsedArgs parsed, LangQueryService service)
{
    ResolvedSolution? solution = null;
    var hasExplicitSolution = parsed.Options.TryGetValue("solution", out var solutionValue) && !string.IsNullOrWhiteSpace(solutionValue);

    string databasePath;
    if (TryGetDatabasePath(parsed, solution: null, out var explicitDatabasePath))
    {
        databasePath = explicitDatabasePath;
        var databaseExists = File.Exists(Path.GetFullPath(databasePath));
        var requiresRebuild = hasExplicitSolution || !databaseExists;

        if (requiresRebuild)
        {
            if (!TryResolveSolution(parsed, "exportjson", out var resolvedSolution))
            {
                return 1;
            }

            solution = resolvedSolution;
        }
    }
    else
    {
        if (!TryResolveSolution(parsed, "exportjson", out var resolvedSolution))
        {
            return 1;
        }

        solution = resolvedSolution;
        if (!TryGetDatabasePath(parsed, solution, out databasePath))
        {
            return 1;
        }
    }

    if (solution is not null)
    {
        await service.ScanAsync(new ScanOptions(solution.FilePath, databasePath, ChangedOnly: false), CancellationToken.None).ConfigureAwait(false);
    }

    var exportPath = ResolveExportJsonPath(parsed, databasePath);
    var fullExportPath = Path.GetFullPath(exportPath);
    var exportDirectory = Path.GetDirectoryName(fullExportPath);
    if (!string.IsNullOrWhiteSpace(exportDirectory))
    {
        Directory.CreateDirectory(exportDirectory);
    }

    var exportedEntityCount = await WriteDatabaseExportJsonAsync(databasePath, fullExportPath, parsed.Pretty, CancellationToken.None).ConfigureAwait(false);

    WriteOutput(new
    {
        command = "exportjson",
        success = true,
        data = new
        {
            database_path = Path.GetFullPath(databasePath),
            export_path = fullExportPath,
            entities = exportedEntityCount
        }
    }, parsed.Pretty);

    return 0;
}

static string ResolveExportJsonPath(ParsedArgs parsed, string databasePath)
{
    if (parsed.Options.TryGetValue("exportjson", out var explicitPath) && !string.IsNullOrWhiteSpace(explicitPath))
    {
        return explicitPath;
    }

    return Path.ChangeExtension(databasePath, ".json");
}

static async Task<int> HandleInstallSkillAsync(ParsedArgs parsed)
{
    if (!parsed.Options.TryGetValue("target", out var targetValue) || string.IsNullOrWhiteSpace(targetValue))
    {
        WriteOutput(new
        {
            command = "installskill",
            success = false,
            error = "Missing required argument '<claude|codex|opencode|all>'. Usage: langquery installskill <claude|codex|opencode|all> [--pretty]."
        }, parsed.Pretty);
        return 1;
    }

    if (!TryResolveInstallSkillTargets(targetValue, out var skillRoots))
    {
        WriteOutput(new
        {
            command = "installskill",
            success = false,
            error = "Argument for 'installskill' must be one of: claude, codex, opencode, all."
        }, parsed.Pretty);
        return 1;
    }

    var unsupportedOptions = parsed.Options.Keys
        .Where(key => !string.Equals(key, "target", StringComparison.OrdinalIgnoreCase))
        .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    var unsupportedFlags = parsed.Flags
        .Where(flag => !string.Equals(flag, "pretty", StringComparison.OrdinalIgnoreCase))
        .OrderBy(flag => flag, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (unsupportedOptions.Length > 0 || unsupportedFlags.Length > 0)
    {
        var unsupportedTokens = unsupportedOptions
            .Concat(unsupportedFlags)
            .Select(token => $"--{token}")
            .ToArray();

        WriteOutput(new
        {
            command = "installskill",
            success = false,
            error = $"Unsupported option(s) for 'installskill': {string.Join(", ", unsupportedTokens)}. Usage: langquery installskill <claude|codex|opencode|all> [--pretty]."
        }, parsed.Pretty);
        return 1;
    }

    var currentDirectory = Directory.GetCurrentDirectory();
    var tempDirectory = Path.Combine(Path.GetTempPath(), "langquery-installskill");
    Directory.CreateDirectory(tempDirectory);
    var tempDatabasePath = Path.Combine(tempDirectory, $"langquery.skill.{Guid.NewGuid():N}.db.sqlite");
    const string simpleSchemaSourceCommand = "langquery simpleschema --db <temp-db> --pretty";

    try
    {
        var examplesOutput = SerializePayloadForSkill(new
        {
            command = "examples",
            success = true,
            data = BuildExamplesPayload()
        });

        var service = new LangQueryService(
            new CSharpCodeFactsExtractor(),
            new SqliteStorageEngine(new ReadOnlySqlSafetyValidator()));

        string simpleSchemaOutput;
        try
        {
            var schema = await service.GetSchemaAsync(new SchemaOptions(tempDatabasePath), CancellationToken.None).ConfigureAwait(false);
            var simpleSchema = BuildSimpleSchema(schema);
            simpleSchemaOutput = SerializePayloadForSkill(new
            {
                command = "simpleschema",
                success = true,
                data = simpleSchema
            });
        }
        catch (Exception ex)
        {
            WriteOutput(new
            {
                command = "installskill",
                success = false,
                error = $"Failed to capture simpleschema output. {ex.Message}"
            }, parsed.Pretty);
            return 1;
        }

        var skillContent = BuildInstallSkillContent(examplesOutput, simpleSchemaOutput, simpleSchemaSourceCommand);
        var writtenFiles = new List<string>();

        foreach (var skillRoot in skillRoots)
        {
            var skillDirectory = Path.Combine(currentDirectory, skillRoot, "skills", "langquery");
            Directory.CreateDirectory(skillDirectory);

            var skillPath = Path.Combine(skillDirectory, "SKILL.md");
            await File.WriteAllTextAsync(skillPath, skillContent, CancellationToken.None).ConfigureAwait(false);
            writtenFiles.Add(Path.GetFullPath(skillPath));
        }

        WriteOutput(new
        {
            command = "installskill",
            success = true,
            data = new
            {
                target = targetValue,
                files = writtenFiles,
                generated_utc = DateTimeOffset.UtcNow,
                source = new
                {
                    help = "langquery help --pretty",
                    examples = "langquery examples --pretty",
                    simpleschema = simpleSchemaSourceCommand
                }
            }
        }, parsed.Pretty);

        return 0;
    }
    finally
    {
        DeleteTempDatabaseArtifacts(tempDatabasePath);
    }
}

static string SerializePayloadForSkill(object payload)
{
    return JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    });
}

static void DeleteTempDatabaseArtifacts(string databasePath)
{
    TryDeleteFile(databasePath);
    TryDeleteFile(databasePath + "-wal");
    TryDeleteFile(databasePath + "-shm");
    TryDeleteFile(databasePath + "-journal");
}

static void TryDeleteFile(string filePath)
{
    try
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
    catch
    {
        // Best effort cleanup of temp artifacts.
    }
}

static bool TryResolveInstallSkillTargets(string targetValue, out IReadOnlyList<string> roots)
{
    if (string.Equals(targetValue, "claude", StringComparison.OrdinalIgnoreCase))
    {
        roots = [".claude"];
        return true;
    }

    if (string.Equals(targetValue, "codex", StringComparison.OrdinalIgnoreCase))
    {
        roots = [".codex"];
        return true;
    }

    if (string.Equals(targetValue, "opencode", StringComparison.OrdinalIgnoreCase))
    {
        roots = [".opencode"];
        return true;
    }

    if (string.Equals(targetValue, "all", StringComparison.OrdinalIgnoreCase))
    {
        roots = [".claude", ".codex", ".opencode"];
        return true;
    }

    roots = Array.Empty<string>();
    return false;
}

static string BuildInstallSkillContent(string examplesOutput, string simpleSchemaOutput, string simpleSchemaSourceCommand)
{
    return $$"""
---
name: langquery
description: Analyze C# codebases with structured SQL facts instead of grep and other token-heavy text search.
---

# LangQuery Skill

## Quick summary
LangQuery scans a C# solution into a local SQLite database, then lets you answer code questions using read-only SQL over stable `v1_*` views and `meta_*` metadata.

## When to use this skill
- You need fast, repo-wide facts (for example: where symbols are declared/used, method/type inventories, hotspots, counts, relationships).
- You want deterministic, auditable answers backed by SQL rows instead of heuristic text search.
- You need to explore large codebases efficiently before deciding which files to open in detail.

## When not to use this skill
- You already know the exact file/line to inspect; read the file directly instead.
- You need to modify code, run tests, or perform non-SQL tasks; this skill is for analysis/querying only.
- You need runtime behavior that static indexing cannot prove (for example live config values, production-only state).

Run `langquery help` for a full command list, but in the common case you should not need it. If there is exactly one `.sln` in the current project, the best workflow is: run `langquery scan` after initial setup or code changes, then run `langquery "<sql>"` for queries.

## Usage examples (`langquery examples --pretty`)
```json
{{examplesOutput}}
```

## Best practices
- Re-scan after every code change (add/edit/delete/rename): prefer a full `langquery scan` (safer and preferred). Use `langquery scan --solution <folder-or-.sln> --db <path> --changed-only` only as an experimental faster path.
- Prefer querying `v1_*` views and `meta_*` entities from the public contract instead of private/internal SQLite tables.
- Keep SQL read-only (`SELECT`, `WITH`, `EXPLAIN`) and set `--max-rows`/`--timeout-ms` for predictable results.
- Start with broad discovery queries (`COUNT`, grouped summaries, `LIMIT`) before deep joins.
- Use `langquery simpleschema --solution <folder-or-.sln> --db <path> --pretty` to refresh field names and known constants.

## Simple schema legend
- `SchemaVersion`: public schema contract version.
- `Entities`: map of `<table_or_view_name>` to grouped field arrays.
- `text_fields`: use for `LIKE`, equality, and grouping filters.
- `numeric_fields`: use for range filters, sorting, and aggregations.
- `boolean_fields`: boolean-like fields (`0/1` or true/false semantics).
- `blob_fields`: binary payload fields; avoid unless explicitly needed.
- `other_fields`: fields that do not match the common SQLite affinities above.
- `Constants`: map of `<table.column>` to known literal values for safe predicates.

## Current simple schema description (`{{simpleSchemaSourceCommand}}`)
```json
{{simpleSchemaOutput}}
```
""";
}

static async Task<int> WriteDatabaseExportJsonAsync(string databasePath, string exportPath, bool pretty, CancellationToken cancellationToken)
{
    const string entityDiscoverySql = "SELECT name, type, IFNULL(sql, '') AS sql FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY type, name";
    var fullDatabasePath = Path.GetFullPath(databasePath);

    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = fullDatabasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = true
    }.ConnectionString);
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

    var entities = new List<(string Name, string Type, string Sql)>();
    await using (var entityCommand = connection.CreateCommand())
    {
        entityCommand.CommandText = entityDiscoverySql;
        await using var entityReader = await entityCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await entityReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entities.Add((
                Name: entityReader.GetString(0),
                Type: entityReader.GetString(1),
                Sql: entityReader.GetString(2)));
        }
    }

    await using var stream = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 128 * 1024, useAsync: true);
    await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = pretty });

    writer.WriteStartObject();
    writer.WriteString(nameof(DatabaseExportPayload.DatabasePath), fullDatabasePath);
    writer.WriteString(nameof(DatabaseExportPayload.ExportedUtc), DateTimeOffset.UtcNow);
    writer.WritePropertyName(nameof(DatabaseExportPayload.Entities));
    writer.WriteStartArray();

    foreach (var entity in entities)
    {
        writer.WriteStartObject();
        writer.WriteString(nameof(DatabaseExportEntity.Name), entity.Name);
        writer.WriteString(nameof(DatabaseExportEntity.Type), entity.Type);
        writer.WriteString(nameof(DatabaseExportEntity.Sql), entity.Sql);
        writer.WritePropertyName(nameof(DatabaseExportEntity.Columns));

        var escapedEntityName = EscapeSqliteIdentifier(entity.Name);
        await using var dataCommand = connection.CreateCommand();
        dataCommand.CommandText = $"SELECT * FROM \"{escapedEntityName}\"";
        await using var dataReader = await dataCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        writer.WriteStartArray();
        for (var i = 0; i < dataReader.FieldCount; i++)
        {
            writer.WriteStringValue(dataReader.GetName(i));
        }
        writer.WriteEndArray();

        writer.WritePropertyName(nameof(DatabaseExportEntity.Rows));
        writer.WriteStartArray();
        while (await dataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            writer.WriteStartObject();
            for (var i = 0; i < dataReader.FieldCount; i++)
            {
                writer.WritePropertyName(dataReader.GetName(i));
                if (dataReader.IsDBNull(i))
                {
                    writer.WriteNullValue();
                }
                else
                {
                    JsonSerializer.Serialize(writer, dataReader.GetValue(i));
                }
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

    return entities.Count;
}

static string EscapeSqliteIdentifier(string value)
{
    return value.Replace("\"", "\"\"", StringComparison.Ordinal);
}

static SimpleSchemaPayload BuildSimpleSchema(SchemaDescription schema)
{
    var entities = schema.Entities
        .OrderBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            entity => entity.Name,
            entity => BuildSimpleSchemaFieldBuckets(entity.Columns),
            StringComparer.OrdinalIgnoreCase);

    var constants = BuildKnownQueryConstants(schema.Entities)
        .OrderBy(constant => constant.Location, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            constant => constant.Location,
            constant => (IReadOnlyList<string>)constant.Values,
            StringComparer.OrdinalIgnoreCase);

    return new SimpleSchemaPayload(schema.SchemaVersion, entities, constants);
}

static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildSimpleSchemaFieldBuckets(IReadOnlyList<SchemaColumn> columns)
{
    var buckets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    foreach (var column in columns)
    {
        var bucketName = ClassifySimpleSchemaBucket(column.Type);
        if (!buckets.TryGetValue(bucketName, out var names))
        {
            names = new List<string>();
            buckets[bucketName] = names;
        }

        names.Add(column.Name);
    }

    var orderedBuckets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    foreach (var bucketName in new[] { "text_fields", "numeric_fields", "boolean_fields", "blob_fields", "other_fields" })
    {
        if (buckets.TryGetValue(bucketName, out var names) && names.Count > 0)
        {
            orderedBuckets[bucketName] = names;
        }
    }

    return orderedBuckets;
}

static string ClassifySimpleSchemaBucket(string sqliteType)
{
    var normalized = sqliteType.Trim().ToUpperInvariant();
    var parenthesisStart = normalized.IndexOf('(', StringComparison.Ordinal);
    if (parenthesisStart > 0)
    {
        normalized = normalized[..parenthesisStart];
    }

    if (normalized.Contains("CHAR", StringComparison.Ordinal)
        || normalized.Contains("CLOB", StringComparison.Ordinal)
        || normalized.Contains("TEXT", StringComparison.Ordinal))
    {
        return "text_fields";
    }

    if (normalized.Contains("BOOL", StringComparison.Ordinal))
    {
        return "boolean_fields";
    }

    if (normalized.Contains("INT", StringComparison.Ordinal)
        || normalized.Contains("REAL", StringComparison.Ordinal)
        || normalized.Contains("FLOA", StringComparison.Ordinal)
        || normalized.Contains("DOUB", StringComparison.Ordinal)
        || normalized.Contains("NUMERIC", StringComparison.Ordinal)
        || normalized.Contains("DECIMAL", StringComparison.Ordinal))
    {
        return "numeric_fields";
    }

    if (normalized.Contains("BLOB", StringComparison.Ordinal))
    {
        return "blob_fields";
    }

    return "other_fields";
}

static IReadOnlyList<SimpleSchemaConstant> BuildKnownQueryConstants(IReadOnlyList<SchemaEntity> entities)
{
    var candidates = new[]
    {
        new SimpleSchemaConstant("v1_files.language", ["csharp"]),
        new SimpleSchemaConstant("v1_types.kind", ["Class", "Struct", "Interface", "Record", "Enum"]),
        new SimpleSchemaConstant("v1_types.access_modifier", ["Public", "Internal", "Private", "Protected", "ProtectedInternal", "PrivateProtected", "File"]),
        new SimpleSchemaConstant("v1_types.modifiers", ["Abstract", "Sealed", "Static", "Partial", "ReadOnly", "Ref"]),
        new SimpleSchemaConstant("v1_type_inheritances.relation_kind", ["BaseType", "Interface", "BaseInterface"]),
        new SimpleSchemaConstant("v1_variables.kind", ["Parameter", "Local", "ForEach", "Catch"]),
        new SimpleSchemaConstant("v1_symbol_refs.symbol_kind", ["Variable", "Method", "Property", "Identifier"]),
        new SimpleSchemaConstant("v1_methods.return_type", ["ctor"]),
        new SimpleSchemaConstant("v1_methods.access_modifier", ["Public", "Internal", "Private", "Protected", "ProtectedInternal", "PrivateProtected", "Local"]),
        new SimpleSchemaConstant("v1_methods.modifiers", ["Abstract", "Virtual", "Override", "Sealed", "Static", "Async"]),
        new SimpleSchemaConstant("v1_methods.implementation_kind", ["Method", "Constructor", "LocalFunction", "Lambda", "AnonymousMethod"]),
        new SimpleSchemaConstant("meta_capabilities.key", ["sql_mode", "public_views", "languages"]),
        new SimpleSchemaConstant("meta_capabilities.value (key = 'sql_mode')", ["read-only"]),
        new SimpleSchemaConstant("meta_capabilities.value (key = 'public_views')", ["v1"]),
        new SimpleSchemaConstant("meta_capabilities.value (key = 'languages')", ["csharp"])
    };

    return candidates
        .Where(candidate => IsConstantRelevantToSchema(candidate.Location, entities))
        .ToArray();
}

static bool IsConstantRelevantToSchema(string location, IReadOnlyList<SchemaEntity> entities)
{
    var baseLocation = location;
    var conditionStart = location.IndexOf(' ', StringComparison.Ordinal);
    if (conditionStart >= 0)
    {
        baseLocation = location[..conditionStart];
    }

    var separator = baseLocation.IndexOf('.', StringComparison.Ordinal);
    if (separator <= 0 || separator >= baseLocation.Length - 1)
    {
        return false;
    }

    var entityName = baseLocation[..separator];
    var columnName = baseLocation[(separator + 1)..];

    return entities.Any(entity =>
        string.Equals(entity.Name, entityName, StringComparison.OrdinalIgnoreCase)
        && entity.Columns.Any(column => string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase)));
}

static bool TryGetDatabasePath(ParsedArgs parsed, ResolvedSolution? solution, out string databasePath)
{
    if (parsed.Options.TryGetValue("db", out var dbValue) && !string.IsNullOrWhiteSpace(dbValue))
    {
        databasePath = dbValue;
        return true;
    }

    if (solution is null)
    {
        databasePath = string.Empty;
        return false;
    }

    databasePath = Path.Combine(solution.DirectoryPath, $".langquery.{solution.Name}.db.sqlite");
    return true;
}

static bool TryResolveSolution(ParsedArgs parsed, string command, out ResolvedSolution solution)
{
    var requestedPath = Directory.GetCurrentDirectory();
    if (parsed.Options.TryGetValue("solution", out var solutionPath) && !string.IsNullOrWhiteSpace(solutionPath))
    {
        requestedPath = solutionPath;
    }

    return TryResolveSolutionPath(requestedPath, parsed, command, out solution);
}

static bool TryResolveSolutionPath(string solutionPath, ParsedArgs parsed, string command, out ResolvedSolution solution)
{
    var fullPath = Path.GetFullPath(solutionPath);

    if (File.Exists(fullPath))
    {
        if (!string.Equals(Path.GetExtension(fullPath), ".sln", StringComparison.OrdinalIgnoreCase))
        {
            WriteOutput(new
            {
                command,
                success = false,
                error = $"Solution path '{solutionPath}' points to a file that is not a .sln file."
            }, parsed.Pretty);

            solution = default!;
            return false;
        }

        var directoryPath = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            WriteOutput(new
            {
                command,
                success = false,
                error = $"Could not derive the solution folder from '{solutionPath}'."
            }, parsed.Pretty);

            solution = default!;
            return false;
        }

        solution = new ResolvedSolution(fullPath, directoryPath, Path.GetFileNameWithoutExtension(fullPath));
        return true;
    }

    if (Directory.Exists(fullPath))
    {
        var slnFiles = Directory
            .EnumerateFiles(fullPath, "*.sln", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (slnFiles.Length == 1)
        {
            var slnPath = slnFiles[0];
            solution = new ResolvedSolution(slnPath, fullPath, Path.GetFileNameWithoutExtension(slnPath));
            return true;
        }

        if (slnFiles.Length == 0)
        {
            WriteOutput(new
            {
                command,
                success = false,
                error = $"No .sln files were found in '{fullPath}'. Specify '--solution <path-to-sln>' explicitly."
            }, parsed.Pretty);

            solution = default!;
            return false;
        }

        WriteOutput(new
        {
            command,
            success = false,
            error = $"Multiple .sln files were found in '{fullPath}'. LangQuery cannot decide which one to use.",
            solutions = slnFiles
        }, parsed.Pretty);

        solution = default!;
        return false;
    }

    WriteOutput(new
    {
        command,
        success = false,
        error = $"Solution path '{solutionPath}' does not exist."
    }, parsed.Pretty);

    solution = default!;
    return false;
}

static async Task EnsureDatabaseReadyForQueryAsync(LangQueryService service, ResolvedSolution solution, string databasePath)
{
    if (File.Exists(Path.GetFullPath(databasePath)))
    {
        return;
    }

    await service.ScanAsync(new ScanOptions(solution.FilePath, databasePath), CancellationToken.None).ConfigureAwait(false);
}

static int? TryGetIntOption(ParsedArgs parsed, string optionName, int fallback, int minValue)
{
    if (!parsed.Options.TryGetValue(optionName, out var value) || string.IsNullOrWhiteSpace(value))
    {
        return fallback;
    }

    if (!int.TryParse(value, out var parsedInt) || parsedInt < minValue)
    {
        WriteOutput(new
        {
            command = parsed.Command,
            success = false,
            error = $"Option '--{optionName}' must be an integer >= {minValue}."
        }, parsed.Pretty);
        return null;
    }

    return parsedInt;
}

static object BuildHelpPayload()
{
    return new
    {
        description = "LangQuery CLI",
        default_usage = "langquery <sql>",
        notes = new[]
        {
            "If '--db' is omitted, the CLI uses '<solution-folder>/.langquery.<solution-name>.db.sqlite'.",
            "If '--solution' is omitted, the current folder is used and exactly one .sln file must be present.",
            "'--changed-only' is experimental for partial/incremental updates; a full 'scan' is safer and preferred.",
            "For SQL queries, if the DB file does not exist, the CLI runs a scan first and then executes the query.",
            "Use 'examples' to print sample SQL queries from simple to advanced scenarios.",
            "Use 'exportjson [file-name]' to rebuild and export the full SQLite database as one JSON file.",
            "Use 'info' to print tool/runtime metadata including the installed version.",
            "Use 'installskill <claude|codex|opencode|all>' to generate reusable AI skill files in the current project.",
            "Short query form ('langquery <sql>') enables pretty JSON output by default."
        },
        global_options = new[]
        {
            new
            {
                name = "--pretty",
                description = "Pretty-print JSON payloads."
            }
        },
        commands = new[]
        {
            new
            {
                name = "scan",
                usage = "langquery scan [--solution <folder-or-.sln>] [--db <path>] [--changed-only] [--pretty]",
                description = "Scan C# files and persist extracted facts into SQLite ('--changed-only' is experimental)"
            },
            new
            {
                name = "sql",
                usage = "langquery sql --query <sql> [--solution <folder-or-.sln>] [--db <path>] [--max-rows <n>] [--timeout-ms <n>] [--pretty]",
                description = "Execute read-only SQL against LangQuery views"
            },
            new
            {
                name = "schema",
                usage = "langquery schema [--solution <folder-or-.sln>] [--db <path>] [--pretty]",
                description = "Describe available `v1_*` and `meta_*` entities"
            },
            new
            {
                name = "simpleschema",
                usage = "langquery simpleschema [--solution <folder-or-.sln>] [--db <path>] [--pretty]",
                description = "Describe query-focused schema fields and known constants"
            },
            new
            {
                name = "help",
                usage = "langquery help [--pretty]",
                description = "Show command help"
            },
            new
            {
                name = "info",
                usage = "langquery info [--pretty]",
                description = "Show installed version and environment metadata"
            },
            new
            {
                name = "installskill",
                usage = "langquery installskill <claude|codex|opencode|all> [--pretty]",
                description = "Generate LangQuery skill files for AI coding agents"
            },
            new
            {
                name = "examples",
                usage = "langquery examples [--pretty]",
                description = "Show example SQL queries with explanations"
            },
            new
            {
                name = "exportjson",
                usage = "langquery exportjson [file-name] [--solution <folder-or-.sln>] [--db <path>] [--pretty]",
                description = "Rebuild and export the entire LangQuery database as JSON"
            }
        }
    };
}

static object BuildExamplesPayload()
{
    return new[]
    {
        new
        {
            title = "List all indexed files",
            query = "SELECT file_path, language, indexed_utc FROM v1_files ORDER BY file_path",
            explanation = "Returns one row per indexed source file with language and indexing timestamp."
        },
        new
        {
            title = "List all variables",
            query = "SELECT file_path, method_name, name AS variable_name, kind, type_name, declaration_line FROM v1_variables ORDER BY file_path, method_name, declaration_line",
            explanation = "Shows every variable declaration with owning file/method and inferred type information."
        },
        new
        {
            title = "Integer sumOf* variables used in at least two places",
            query = "SELECT v.file_path, v.method_name, v.name AS variable_name, LOWER(COALESCE(v.type_name, '')) AS normalized_type, COUNT(DISTINCT lv.line_number) AS usage_line_count FROM v1_variables v JOIN v1_line_variables lv ON lv.variable_id = v.variable_id WHERE v.name LIKE 'sumOf%' AND LOWER(COALESCE(v.type_name, '')) IN ('int', 'int32', 'system.int32') GROUP BY v.variable_id, v.file_path, v.method_name, v.name, normalized_type HAVING COUNT(DISTINCT lv.line_number) >= 2 ORDER BY usage_line_count DESC, v.file_path, v.method_name, v.name",
            explanation = "Finds variables named like 'sumOf*' whose type is integer and that are referenced on at least two distinct lines."
        },
        new
        {
            title = "Type count by kind",
            query = "SELECT kind AS type_kind, COUNT(*) AS type_count FROM v1_types GROUP BY kind ORDER BY type_count DESC, type_kind",
            explanation = "Summarizes how many classes, interfaces, records, and other type kinds are indexed."
        },
        new
        {
            title = "Methods with highest parameter arity",
            query = "SELECT m.file_path, m.name AS method_name, COUNT(v.variable_id) AS parameter_count, m.parameters FROM v1_methods m LEFT JOIN v1_variables v ON v.method_id = m.method_id AND v.kind = 'Parameter' WHERE m.implementation_kind IN ('Method', 'Constructor', 'LocalFunction') GROUP BY m.method_id, m.file_path, m.name, m.parameters ORDER BY parameter_count DESC, m.file_path, method_name LIMIT 25",
            explanation = "Surfaces high-arity methods by computing parameter counts from variable facts, then pairing them with parameter signatures."
        },
        new
        {
            title = "Abstract and sealed type declarations",
            query = "SELECT file_path, name AS type_name, access_modifier, modifiers FROM v1_types WHERE modifiers LIKE '%Abstract%' OR modifiers LIKE '%Sealed%' ORDER BY file_path, type_name",
            explanation = "Finds type declarations carrying key modifiers like abstract or sealed and shows their effective accessibility."
        },
        new
        {
            title = "Nested implementations (local/lambda/anonymous)",
            query = "SELECT file_path, name AS implementation_name, implementation_kind, access_modifier, parent_method_key FROM v1_methods WHERE implementation_kind IN ('LocalFunction', 'Lambda', 'AnonymousMethod') ORDER BY file_path, implementation_kind, implementation_name",
            explanation = "Lists nested implementation forms and their parent method keys so you can trace local behavior boundaries."
        },
        new
        {
            title = "Longest methods by span",
            query = "SELECT file_path, name AS method_name, (line_end - line_start + 1) AS line_span FROM v1_methods ORDER BY line_span DESC, file_path, method_name LIMIT 25",
            explanation = "Highlights large methods by counting source lines from declaration start to end."
        },
        new
        {
            title = "Most reused variables",
            query = "SELECT v.file_path, v.method_name, v.name AS variable_name, COUNT(DISTINCT lv.line_number) AS usage_line_count FROM v1_variables v JOIN v1_line_variables lv ON lv.variable_id = v.variable_id GROUP BY v.variable_id, v.file_path, v.method_name, v.name ORDER BY usage_line_count DESC, v.file_path, v.method_name, v.name LIMIT 25",
            explanation = "Shows variables that appear on the most distinct lines, useful for spotting high-churn state."
        },
        new
        {
            title = "Invocation hotspots",
            query = "SELECT file_path, target_name, COUNT(*) AS call_count FROM v1_invocations GROUP BY file_path, target_name ORDER BY call_count DESC, file_path, target_name LIMIT 25",
            explanation = "Finds the most frequently called targets per file to reveal call concentration points."
        },
        new
        {
            title = "Property/member access hotspots",
            query = "SELECT file_path, symbol_name AS member_name, symbol_kind, COUNT(*) AS reference_count FROM v1_symbol_refs WHERE symbol_kind IN ('Property', 'Method') GROUP BY file_path, symbol_name, symbol_kind ORDER BY reference_count DESC, file_path, member_name LIMIT 50",
            explanation = "Highlights frequently referenced member names and distinguishes property access from method invocations (for example, `Parameters` vs `AddWithValue`)."
        },
        new
        {
            title = "Variable-dense lines",
            query = "SELECT l.file_path, l.line_number, COUNT(DISTINCT lv.variable_id) AS variable_count, l.text FROM v1_lines l JOIN v1_line_variables lv ON lv.line_id = l.line_id GROUP BY l.line_id, l.file_path, l.line_number, l.text HAVING COUNT(DISTINCT lv.variable_id) >= 3 ORDER BY variable_count DESC, l.file_path, l.line_number LIMIT 50",
            explanation = "Lists lines with heavy variable usage by deriving line-level counts from line-variable links."
        }
    };
}

static object BuildInfoPayload()
{
    var entryAssembly = Assembly.GetEntryAssembly() ?? typeof(LangQueryService).Assembly;
    var informationalVersion = entryAssembly
                                   .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                                   ?.InformationalVersion
                               ?? entryAssembly.GetName().Version?.ToString()
                               ?? "unknown";

    var fileVersion = entryAssembly
                          .GetCustomAttribute<AssemblyFileVersionAttribute>()
                          ?.Version
                      ?? "unknown";

    return new
    {
        command = "info",
        success = true,
        data = new
        {
            tool = entryAssembly.GetName().Name,
            version = informationalVersion,
            file_version = fileVersion,
            framework = RuntimeInformation.FrameworkDescription,
            runtime_version = Environment.Version.ToString(),
            os = RuntimeInformation.OSDescription,
            os_architecture = RuntimeInformation.OSArchitecture.ToString(),
            process_architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            executable_path = Environment.ProcessPath,
            current_directory = Directory.GetCurrentDirectory(),
            utc_now = DateTimeOffset.UtcNow
        }
    };
}

static ParsedArgs ParseArgs(string[] args)
{
    if (args.Length == 0)
    {
        return new ParsedArgs("help", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), Pretty: true, Error: null);
    }

    var firstToken = args[0].Trim();
    if (firstToken.StartsWith("--", StringComparison.Ordinal))
    {
        return ParseOptions("sql", args, 0);
    }

    if (IsKnownCommand(firstToken))
    {
        return ParseOptions(firstToken.ToLowerInvariant(), args, 1);
    }

    var optionStart = Array.FindIndex(args, token => token.StartsWith("--", StringComparison.Ordinal));
    if (optionStart < 0)
    {
        var queryOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["query"] = string.Join(" ", args)
        };

        return new ParsedArgs("sql", queryOptions, new HashSet<string>(StringComparer.OrdinalIgnoreCase), Pretty: true, Error: null);
    }

    var parsed = ParseOptions("sql", args, optionStart);
    if (parsed.Error is not null)
    {
        return parsed;
    }

    if (!parsed.Options.ContainsKey("query"))
    {
        var queryTokens = args[..optionStart];
        parsed.Options["query"] = string.Join(" ", queryTokens);
    }

    return parsed with { Pretty = true };
}

static ParsedArgs ParseOptions(string command, string[] args, int startIndex)
{
    var commandSpec = GetCommandSpec(command);
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    for (var i = startIndex; i < args.Length; i++)
    {
        var token = args[i];
        if (!token.StartsWith("--", StringComparison.Ordinal))
        {
            if (string.Equals(command, "exportjson", StringComparison.OrdinalIgnoreCase) &&
                !options.ContainsKey("exportjson"))
            {
                options["exportjson"] = token;
                continue;
            }

            if (string.Equals(command, "installskill", StringComparison.OrdinalIgnoreCase) &&
                !options.ContainsKey("target"))
            {
                options["target"] = token;
                continue;
            }

            return new ParsedArgs(command, options, flags, Pretty: flags.Contains("pretty"), Error: $"Unexpected token '{token}'. Options must use '--name value' format.");
        }

        var rawName = token[2..].Trim();
        if (rawName.Length == 0)
        {
            return new ParsedArgs(command, options, flags, Pretty: flags.Contains("pretty"), Error: "Option name cannot be empty.");
        }

        var name = rawName.ToLowerInvariant();

        if (commandSpec.FlagOptions.Contains(name))
        {
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return new ParsedArgs(command, options, flags, Pretty: flags.Contains("pretty"), Error: $"Option '--{rawName}' does not accept a value.");
            }

            flags.Add(name);
            continue;
        }

        if (commandSpec.ValueOptions.Contains(name))
        {
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return new ParsedArgs(command, options, flags, Pretty: flags.Contains("pretty"), Error: $"Option '--{rawName}' requires a value.");
            }

            options[name] = args[i + 1];
            i++;
            continue;
        }

        return new ParsedArgs(
            command,
            options,
            flags,
            Pretty: flags.Contains("pretty"),
            Error: $"Unknown option '--{rawName}' for command '{command}'.");
    }

    return new ParsedArgs(command, options, flags, Pretty: flags.Contains("pretty"), Error: null);
}

static CommandSpec GetCommandSpec(string command)
{
    var normalized = command.ToLowerInvariant();
    return normalized switch
    {
        "scan" => new CommandSpec(
            ValueOptions: ["solution", "db"],
            FlagOptions: ["changed-only", "pretty"]),
        "sql" => new CommandSpec(
            ValueOptions: ["query", "solution", "db", "max-rows", "timeout-ms"],
            FlagOptions: ["pretty"]),
        "schema" => new CommandSpec(
            ValueOptions: ["solution", "db"],
            FlagOptions: ["pretty"]),
        "simpleschema" => new CommandSpec(
            ValueOptions: ["solution", "db"],
            FlagOptions: ["pretty"]),
        "exportjson" => new CommandSpec(
            ValueOptions: ["solution", "db"],
            FlagOptions: ["pretty"]),
        "installskill" => new CommandSpec(
            ValueOptions: Array.Empty<string>(),
            FlagOptions: ["pretty"]),
        "help" or "examples" or "info" => new CommandSpec(
            ValueOptions: Array.Empty<string>(),
            FlagOptions: ["pretty"]),
        _ => new CommandSpec(
            ValueOptions: Array.Empty<string>(),
            FlagOptions: ["pretty"])
    };
}

static bool IsKnownCommand(string command)
{
    return string.Equals(command, "scan", StringComparison.OrdinalIgnoreCase)
        || string.Equals(command, "sql", StringComparison.OrdinalIgnoreCase)
        || string.Equals(command, "schema", StringComparison.OrdinalIgnoreCase)
        || string.Equals(command, "simpleschema", StringComparison.OrdinalIgnoreCase)
        || string.Equals(command, "examples", StringComparison.OrdinalIgnoreCase)
        || string.Equals(command, "exportjson", StringComparison.OrdinalIgnoreCase)
        || string.Equals(command, "installskill", StringComparison.OrdinalIgnoreCase)
        || string.Equals(command, "info", StringComparison.OrdinalIgnoreCase)
        || string.Equals(command, "help", StringComparison.OrdinalIgnoreCase);
}

static void WriteOutput(object payload, bool pretty)
{
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = pretty
    });

    Console.WriteLine(json);
}

internal sealed record ParsedArgs(
    string Command,
    Dictionary<string, string> Options,
    HashSet<string> Flags,
    bool Pretty,
    string? Error);

internal sealed record CommandSpec(
    IReadOnlyCollection<string> ValueOptions,
    IReadOnlyCollection<string> FlagOptions);

internal sealed record ResolvedSolution(string FilePath, string DirectoryPath, string Name);

internal sealed record DatabaseExportPayload(
    string DatabasePath,
    DateTimeOffset ExportedUtc,
    IReadOnlyList<DatabaseExportEntity> Entities);

internal sealed record DatabaseExportEntity(
    string Name,
    string Type,
    string Sql,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

internal sealed record SimpleSchemaPayload(
    int SchemaVersion,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Entities,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Constants);

internal sealed record SimpleSchemaConstant(
    string Location,
    IReadOnlyList<string> Values);
