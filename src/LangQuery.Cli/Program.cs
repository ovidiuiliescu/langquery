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
    if (!TryResolveSolution(parsed, "exportjson", out var solution))
    {
        return 1;
    }

    if (!TryGetDatabasePath(parsed, solution, out var databasePath))
    {
        return 1;
    }

    await service.ScanAsync(new ScanOptions(solution.FilePath, databasePath, ChangedOnly: false), CancellationToken.None).ConfigureAwait(false);

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
        var helpOutput = SerializePayloadForSkill(new
        {
            command = "help",
            success = true,
            data = BuildHelpPayload()
        });

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

        var skillContent = BuildInstallSkillContent(helpOutput, examplesOutput, simpleSchemaOutput, simpleSchemaSourceCommand);
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

static string BuildInstallSkillContent(string helpOutput, string examplesOutput, string simpleSchemaOutput, string simpleSchemaSourceCommand)
{
    return $$"""
---
name: langquery
description: Query C# codebases with LangQuery using read-only SQL over `v1_*` views and `meta_*` metadata.
---

# LangQuery Skill

## Quick summary
LangQuery indexes a C# solution into a local SQLite database and exposes stable `v1_*` views plus `meta_*` metadata so AI agents can answer codebase questions with read-only SQL.

## Command line parameters (`langquery help --pretty`)
```json
{{helpOutput}}
```

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
        .Select(entity => new SimpleSchemaEntity(
            entity.Name,
            entity.Kind,
            entity.Columns
                .Select(column => new SimpleSchemaColumn(column.Name, column.Type))
                .ToArray()))
        .ToArray();

    var constants = BuildKnownQueryConstants(entities);
    return new SimpleSchemaPayload(schema.SchemaVersion, entities, constants);
}

static IReadOnlyList<SimpleSchemaConstant> BuildKnownQueryConstants(IReadOnlyList<SimpleSchemaEntity> entities)
{
    var candidates = new[]
    {
        new SimpleSchemaConstant(
            "v1_files.language",
            "Filter by source language.",
            new[]
            {
                new SimpleSchemaKnownValue("csharp", "C# source files indexed by the current extractor.")
            }),
        new SimpleSchemaConstant(
            "v1_types.kind",
            "Filter by declaration type category.",
            new[]
            {
                new SimpleSchemaKnownValue("Class", "class declarations."),
                new SimpleSchemaKnownValue("Struct", "struct declarations."),
                new SimpleSchemaKnownValue("Interface", "interface declarations."),
                new SimpleSchemaKnownValue("Record", "record declarations."),
                new SimpleSchemaKnownValue("Enum", "enum declarations.")
            }),
        new SimpleSchemaConstant(
            "v1_types.access_modifier",
            "Filter type declarations by effective access level.",
            new[]
            {
                new SimpleSchemaKnownValue("Public", "Publicly accessible type declaration."),
                new SimpleSchemaKnownValue("Internal", "Assembly-scoped type declaration."),
                new SimpleSchemaKnownValue("Private", "Nested private type declaration."),
                new SimpleSchemaKnownValue("Protected", "Nested protected type declaration."),
                new SimpleSchemaKnownValue("ProtectedInternal", "Nested protected internal type declaration."),
                new SimpleSchemaKnownValue("PrivateProtected", "Nested private protected type declaration."),
                new SimpleSchemaKnownValue("File", "File-local type declaration.")
            }),
        new SimpleSchemaConstant(
            "v1_types.modifiers",
            "Comma-separated non-access declaration modifiers for types.",
            new[]
            {
                new SimpleSchemaKnownValue("Abstract", "Type has the abstract modifier."),
                new SimpleSchemaKnownValue("Sealed", "Type has the sealed modifier."),
                new SimpleSchemaKnownValue("Static", "Type has the static modifier."),
                new SimpleSchemaKnownValue("Partial", "Type has the partial modifier."),
                new SimpleSchemaKnownValue("ReadOnly", "Type has the readonly modifier (for structs)."),
                new SimpleSchemaKnownValue("Ref", "Type has the ref modifier (for ref structs).")
            }),
        new SimpleSchemaConstant(
            "v1_type_inheritances.relation_kind",
            "Filter inheritance edges by relation kind.",
            new[]
            {
                new SimpleSchemaKnownValue("BaseType", "The inherited class/base type."),
                new SimpleSchemaKnownValue("Interface", "An implemented interface (or struct interface)."),
                new SimpleSchemaKnownValue("BaseInterface", "An interface inheriting another interface.")
            }),
        new SimpleSchemaConstant(
            "v1_variables.kind",
            "Filter by how the variable is introduced in a method.",
            new[]
            {
                new SimpleSchemaKnownValue("Parameter", "Method or constructor parameter."),
                new SimpleSchemaKnownValue("Local", "Local variable declaration."),
                new SimpleSchemaKnownValue("ForEach", "foreach loop variable."),
                new SimpleSchemaKnownValue("Catch", "catch exception variable.")
            }),
        new SimpleSchemaConstant(
            "v1_symbol_refs.symbol_kind",
            "Filter coarse symbol-reference categories.",
            new[]
            {
                new SimpleSchemaKnownValue("Variable", "Identifier resolved to a known variable in the method scope."),
                new SimpleSchemaKnownValue("Method", "Identifier used as an invoked method name."),
                new SimpleSchemaKnownValue("Property", "Identifier used as a member/property access (non-invocation)."),
                new SimpleSchemaKnownValue("Identifier", "Any other identifier usage.")
            }),
        new SimpleSchemaConstant(
            "v1_methods.return_type",
            "Constructor rows use a predefined marker.",
            new[]
            {
                new SimpleSchemaKnownValue("ctor", "Constructor methods (not regular methods).")
            }),
        new SimpleSchemaConstant(
            "v1_methods.access_modifier",
            "Filter method and nested implementation rows by effective access level.",
            new[]
            {
                new SimpleSchemaKnownValue("Public", "Public method/member declaration."),
                new SimpleSchemaKnownValue("Internal", "Internal method/member declaration."),
                new SimpleSchemaKnownValue("Private", "Private method/member declaration."),
                new SimpleSchemaKnownValue("Protected", "Protected method/member declaration."),
                new SimpleSchemaKnownValue("ProtectedInternal", "Protected internal method/member declaration."),
                new SimpleSchemaKnownValue("PrivateProtected", "Private protected method/member declaration."),
                new SimpleSchemaKnownValue("Local", "Local function, lambda, or anonymous method.")
            }),
        new SimpleSchemaConstant(
            "v1_methods.modifiers",
            "Comma-separated non-access declaration modifiers for method rows.",
            new[]
            {
                new SimpleSchemaKnownValue("Abstract", "Method has the abstract modifier."),
                new SimpleSchemaKnownValue("Virtual", "Method has the virtual modifier."),
                new SimpleSchemaKnownValue("Override", "Method has the override modifier."),
                new SimpleSchemaKnownValue("Sealed", "Method has the sealed modifier."),
                new SimpleSchemaKnownValue("Static", "Method has the static modifier."),
                new SimpleSchemaKnownValue("Async", "Method has the async modifier.")
            }),
        new SimpleSchemaConstant(
            "v1_methods.implementation_kind",
            "Distinguish top-level methods from nested implementation forms.",
            new[]
            {
                new SimpleSchemaKnownValue("Method", "Regular method declaration."),
                new SimpleSchemaKnownValue("Constructor", "Constructor declaration."),
                new SimpleSchemaKnownValue("LocalFunction", "Nested local function declaration."),
                new SimpleSchemaKnownValue("Lambda", "Lambda expression implementation."),
                new SimpleSchemaKnownValue("AnonymousMethod", "delegate(...) anonymous method implementation.")
            }),
        new SimpleSchemaConstant(
            "meta_capabilities.key",
            "Capability keys available for filtering metadata.",
            new[]
            {
                new SimpleSchemaKnownValue("sql_mode", "Read-only SQL mode metadata key."),
                new SimpleSchemaKnownValue("public_views", "Public schema version metadata key."),
                new SimpleSchemaKnownValue("languages", "Supported language metadata key.")
            }),
        new SimpleSchemaConstant(
            "meta_capabilities.value (key = 'sql_mode')",
            "Known values when key is 'sql_mode'.",
            new[]
            {
                new SimpleSchemaKnownValue("read-only", "Only read-oriented SQL statements are allowed.")
            }),
        new SimpleSchemaConstant(
            "meta_capabilities.value (key = 'public_views')",
            "Known values when key is 'public_views'.",
            new[]
            {
                new SimpleSchemaKnownValue("v1", "Public query surface version.")
            }),
        new SimpleSchemaConstant(
            "meta_capabilities.value (key = 'languages')",
            "Known values when key is 'languages'.",
            new[]
            {
                new SimpleSchemaKnownValue("csharp", "Current extractor language support.")
            })
    };

    return candidates
        .Where(candidate => IsConstantRelevantToSchema(candidate.Location, entities))
        .ToArray();
}

static bool IsConstantRelevantToSchema(string location, IReadOnlyList<SimpleSchemaEntity> entities)
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
            query = "SELECT file_path, name AS method_name, parameter_count, parameters FROM v1_methods WHERE implementation_kind IN ('Method', 'Constructor', 'LocalFunction') ORDER BY parameter_count DESC, file_path, method_name LIMIT 25",
            explanation = "Surfaces high-arity methods with parameter signatures so you can spot refactor candidates and verify call-shape assumptions."
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
            query = "SELECT file_path, line_number, variable_count, text FROM v1_lines WHERE variable_count >= 3 ORDER BY variable_count DESC, file_path, line_number LIMIT 50",
            explanation = "Lists lines with heavy variable usage, a useful signal for readability and complexity review."
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

        var name = token[2..].Trim();
        if (name.Length == 0)
        {
            return new ParsedArgs(command, options, flags, Pretty: flags.Contains("pretty"), Error: "Option name cannot be empty.");
        }

        if (commandSpec.FlagOptions.Contains(name))
        {
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return new ParsedArgs(command, options, flags, Pretty: flags.Contains("pretty"), Error: $"Option '--{name}' does not accept a value.");
            }

            flags.Add(name);
            continue;
        }

        if (commandSpec.ValueOptions.Contains(name))
        {
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return new ParsedArgs(command, options, flags, Pretty: flags.Contains("pretty"), Error: $"Option '--{name}' requires a value.");
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
            Error: $"Unknown option '--{name}' for command '{command}'.");
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
    IReadOnlyList<SimpleSchemaEntity> Entities,
    IReadOnlyList<SimpleSchemaConstant> Constants);

internal sealed record SimpleSchemaEntity(
    string Name,
    string Kind,
    IReadOnlyList<SimpleSchemaColumn> Columns);

internal sealed record SimpleSchemaColumn(
    string Name,
    string Type);

internal sealed record SimpleSchemaConstant(
    string Location,
    string Usage,
    IReadOnlyList<SimpleSchemaKnownValue> Values);

internal sealed record SimpleSchemaKnownValue(
    string Value,
    string Meaning);
