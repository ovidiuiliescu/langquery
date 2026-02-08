using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LangQuery.IntegrationTests;

public sealed class CliUsabilityTests
{
    [Fact]
    public async Task InfoCommand_ReturnsVersionAndRuntimeMetadata()
    {
        var result = await RunCliAsync(GetRepositoryRoot(), "info");
        var payload = ParseJson(result.StdOut);

        Assert.Equal(0, result.ExitCode);
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal("info", payload.GetProperty("command").GetString());

        var data = GetPropertyIgnoreCase(payload, "data");
        Assert.False(string.IsNullOrWhiteSpace(GetPropertyIgnoreCase(data, "version").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(GetPropertyIgnoreCase(data, "framework").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(GetPropertyIgnoreCase(data, "runtime_version").GetString()));
    }

    [Fact]
    public async Task ExamplesCommand_ReturnsSimpleAndAdvancedQueriesWithExplanations()
    {
        var result = await RunCliAsync(GetRepositoryRoot(), "examples");
        var payload = ParseJson(result.StdOut);

        Assert.Equal(0, result.ExitCode);
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal("examples", payload.GetProperty("command").GetString());

        var examples = GetPropertyIgnoreCase(payload, "data").EnumerateArray().ToArray();
        Assert.True(examples.Length >= 8);
        Assert.Contains(examples, example => GetPropertyIgnoreCase(example, "query").GetString()?.Contains("v1_files", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(examples, example => GetPropertyIgnoreCase(example, "query").GetString()?.Contains("sumOf%", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(examples, example => GetPropertyIgnoreCase(example, "query").GetString()?.Contains("implementation_kind", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(examples, example => GetPropertyIgnoreCase(example, "query").GetString()?.Contains("parameter_count", StringComparison.OrdinalIgnoreCase) == true);
        Assert.All(examples, example => Assert.False(string.IsNullOrWhiteSpace(GetPropertyIgnoreCase(example, "explanation").GetString())));
    }

    [Fact]
    public async Task InstallSkillCommand_CreatesAndOverwritesCodexSkillFileWithGeneratedSections()
    {
        var sampleRoot = CreateTemporarySampleSolutionCopy();
        var skillPath = Path.Combine(sampleRoot, ".codex", "skills", "langquery", "SKILL.md");
        var skillDirectory = Path.GetDirectoryName(skillPath)
                             ?? throw new DirectoryNotFoundException($"Could not derive skill directory from '{skillPath}'.");

        try
        {
            Directory.CreateDirectory(skillDirectory);
            await File.WriteAllTextAsync(skillPath, "outdated-skill-content");

            var result = await RunCliAsync(sampleRoot, "installskill", "codex");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.Equal("installskill", payload.GetProperty("command").GetString());
            Assert.True(File.Exists(skillPath));

            var skillContent = await File.ReadAllTextAsync(skillPath);
            Assert.StartsWith("---", skillContent, StringComparison.Ordinal);
            Assert.Contains("name: langquery", skillContent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("description:", skillContent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Quick summary", skillContent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"command\": \"help\"", skillContent, StringComparison.Ordinal);
            Assert.Contains("\"command\": \"examples\"", skillContent, StringComparison.Ordinal);
            Assert.Contains("\"command\": \"simpleschema\"", skillContent, StringComparison.Ordinal);
            Assert.Contains("Simple schema legend", skillContent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("text_fields", skillContent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Re-scan after every code change", skillContent, StringComparison.Ordinal);
            Assert.Contains("--changed-only", skillContent, StringComparison.Ordinal);
            Assert.Contains("experimental", skillContent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("outdated-skill-content", skillContent, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(sampleRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HelpCommand_MarksChangedOnlyAsExperimental()
    {
        var result = await RunCliAsync(GetRepositoryRoot(), "help");
        var payload = ParseJson(result.StdOut);

        Assert.Equal(0, result.ExitCode);
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal("help", payload.GetProperty("command").GetString());

        var notes = GetPropertyIgnoreCase(GetPropertyIgnoreCase(payload, "data"), "notes")
            .EnumerateArray()
            .Select(note => note.GetString())
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .ToArray();

        Assert.Contains(notes, note => note!.Contains("--changed-only", StringComparison.Ordinal));
        Assert.Contains(notes, note => note!.Contains("experimental", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notes, note => note!.Contains("full 'scan'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InstallSkillCommand_AllTarget_CreatesSkillFilesForAllSupportedAgents()
    {
        var sampleRoot = CreateTemporarySampleSolutionCopy();

        try
        {
            var result = await RunCliAsync(sampleRoot, "installskill", "all");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());

            var claudePath = Path.Combine(sampleRoot, ".claude", "skills", "langquery", "SKILL.md");
            var codexPath = Path.Combine(sampleRoot, ".codex", "skills", "langquery", "SKILL.md");
            var openCodePath = Path.Combine(sampleRoot, ".opencode", "skills", "langquery", "SKILL.md");

            Assert.True(File.Exists(claudePath));
            Assert.True(File.Exists(codexPath));
            Assert.True(File.Exists(openCodePath));
        }
        finally
        {
            Directory.Delete(sampleRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InstallSkillCommand_RejectsInvalidTarget()
    {
        var result = await RunCliAsync(GetRepositoryRoot(), "installskill", "invalid-agent");
        var payload = ParseJson(result.StdOut);

        Assert.Equal(1, result.ExitCode);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("must be one of", payload.GetProperty("error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallSkillCommand_WorksInFolderWithoutSolutionFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "langquery-installskill-empty", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await RunCliAsync(tempRoot, "installskill", "codex");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());

            var skillPath = Path.Combine(tempRoot, ".codex", "skills", "langquery", "SKILL.md");
            Assert.True(File.Exists(skillPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InstallSkillCommand_DoesNotCreateProjectDatabaseFile()
    {
        var sampleRoot = CreateTemporarySampleSolutionCopy();
        var projectDbPath = Path.Combine(sampleRoot, ".langquery.SampleSolution.db.sqlite");
        DeleteDatabaseFiles(projectDbPath);

        try
        {
            var result = await RunCliAsync(sampleRoot, "installskill", "codex");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.False(File.Exists(projectDbPath));
        }
        finally
        {
            DeleteDatabaseFiles(projectDbPath);
            Directory.Delete(sampleRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ScanWithoutSolution_UsesCurrentFolderAndNamedDefaultDatabase()
    {
        var sampleRoot = GetSampleSolutionRoot();
        var databasePath = Path.Combine(sampleRoot, ".langquery.SampleSolution.db.sqlite");
        DeleteDatabaseFiles(databasePath);

        try
        {
            var result = await RunCliAsync(sampleRoot, "scan");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.Equal("scan", payload.GetProperty("command").GetString());
            Assert.True(File.Exists(databasePath));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ShortFormQuery_UsesCurrentFolder_AutoScans_AndPrettyPrintsByDefault()
    {
        var sampleRoot = GetSampleSolutionRoot();
        var databasePath = Path.Combine(sampleRoot, ".langquery.SampleSolution.db.sqlite");
        DeleteDatabaseFiles(databasePath);

        try
        {
            var result = await RunCliAsync(sampleRoot, "SELECT COUNT(*) AS c FROM v1_files");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.Equal("sql", payload.GetProperty("command").GetString());
            Assert.Contains("\n  \"command\"", result.StdOut, StringComparison.Ordinal);
            Assert.True(File.Exists(databasePath));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task RelativeSolutionFolder_WithSingleSolution_IsResolved()
    {
        var repoRoot = GetRepositoryRoot();
        var sampleRoot = Path.Combine(repoRoot, "tests", "sample_solution");
        var databasePath = Path.Combine(sampleRoot, ".langquery.SampleSolution.db.sqlite");
        DeleteDatabaseFiles(databasePath);

        try
        {
            var result = await RunCliAsync(repoRoot, "sql", "--solution", "tests/sample_solution", "--query", "SELECT COUNT(*) AS c FROM v1_files");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.True(File.Exists(databasePath));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task AbsoluteSolutionFolder_WithSingleSolution_IsResolved()
    {
        var sampleRoot = GetSampleSolutionRoot();
        var databasePath = Path.Combine(sampleRoot, ".langquery.SampleSolution.db.sqlite");
        DeleteDatabaseFiles(databasePath);

        try
        {
            var result = await RunCliAsync(GetRepositoryRoot(), "sql", "--solution", sampleRoot, "--query", "SELECT COUNT(*) AS c FROM v1_files");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.True(File.Exists(databasePath));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ExplicitSolutionFile_IsUsedDirectly()
    {
        var sampleRoot = GetSampleSolutionRoot();
        var solutionFilePath = Path.Combine(sampleRoot, "SampleSolution.sln");
        var databasePath = Path.Combine(sampleRoot, ".langquery.SampleSolution.db.sqlite");
        DeleteDatabaseFiles(databasePath);

        try
        {
            var result = await RunCliAsync(GetRepositoryRoot(), "sql", "--solution", solutionFilePath, "--query", "SELECT COUNT(*) AS c FROM v1_files");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.True(File.Exists(databasePath));
            Assert.True(ReadSingleCount(payload) >= 1);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ExplicitSolutionsInSameFolder_IndexOnlyTheirOwnProjectFiles()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "langquery-solution-scope", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var firstProject = Path.Combine(tempDirectory, "src", "First", "First.csproj");
        var secondProject = Path.Combine(tempDirectory, "src", "Second", "Second.csproj");

        WriteProject(firstProject, "FirstClass");
        WriteProject(secondProject, "SecondClass");

        var firstSolution = Path.Combine(tempDirectory, "First.sln");
        var secondSolution = Path.Combine(tempDirectory, "Second.sln");
        WriteSolution(firstSolution, "First", "src/First/First.csproj", "11111111-1111-1111-1111-111111111111");
        WriteSolution(secondSolution, "Second", "src/Second/Second.csproj", "22222222-2222-2222-2222-222222222222");

        var firstDatabase = Path.Combine(tempDirectory, "first.db.sqlite");
        var secondDatabase = Path.Combine(tempDirectory, "second.db.sqlite");
        DeleteDatabaseFiles(firstDatabase);
        DeleteDatabaseFiles(secondDatabase);

        try
        {
            var firstResult = await RunCliAsync(
                GetRepositoryRoot(),
                "sql",
                "--solution",
                firstSolution,
                "--db",
                firstDatabase,
                "--query",
                "SELECT COUNT(*) AS c FROM v1_files");

            var firstPayload = ParseJson(firstResult.StdOut);
            Assert.Equal(0, firstResult.ExitCode);
            Assert.True(firstPayload.GetProperty("success").GetBoolean());
            Assert.Equal(1, ReadSingleCount(firstPayload));

            var secondResult = await RunCliAsync(
                GetRepositoryRoot(),
                "sql",
                "--solution",
                secondSolution,
                "--db",
                secondDatabase,
                "--query",
                "SELECT COUNT(*) AS c FROM v1_files");

            var secondPayload = ParseJson(secondResult.StdOut);
            Assert.Equal(0, secondResult.ExitCode);
            Assert.True(secondPayload.GetProperty("success").GetBoolean());
            Assert.Equal(1, ReadSingleCount(secondPayload));
        }
        finally
        {
            DeleteDatabaseFiles(firstDatabase);
            DeleteDatabaseFiles(secondDatabase);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MultipleSolutionsInFolder_ReturnsErrorAndListsCandidates()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "langquery-multi-sln", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var firstSolution = Path.Combine(tempDirectory, "First.sln");
        var secondSolution = Path.Combine(tempDirectory, "Second.sln");
        File.WriteAllText(firstSolution, "Microsoft Visual Studio Solution File, Format Version 12.00\nGlobal\nEndGlobal\n");
        File.WriteAllText(secondSolution, "Microsoft Visual Studio Solution File, Format Version 12.00\nGlobal\nEndGlobal\n");

        try
        {
            var result = await RunCliAsync(GetRepositoryRoot(), "sql", "--solution", tempDirectory, "--query", "SELECT 1 AS n");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(1, result.ExitCode);
            Assert.False(payload.GetProperty("success").GetBoolean());

            var solutions = payload
                .GetProperty("solutions")
                .EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => x is not null)
                .ToArray();

            Assert.Contains(firstSolution, solutions);
            Assert.Contains(secondSolution, solutions);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExportJsonCommandWithoutFileName_UsesDatabaseBasedDefaultAndPrettyWritesIndentedFile()
    {
        var sampleRoot = CreateTemporarySampleSolutionCopy();
        var databasePath = Path.Combine(sampleRoot, ".langquery.SampleSolution.db.sqlite");
        var exportPath = Path.ChangeExtension(databasePath, ".json");
        DeleteDatabaseFiles(databasePath);
        TryDelete(exportPath);

        try
        {
            var result = await RunCliAsync(sampleRoot, "exportjson", "--pretty");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.Equal("exportjson", payload.GetProperty("command").GetString());
            Assert.True(File.Exists(databasePath));
            Assert.True(File.Exists(exportPath));

            var exportContent = await File.ReadAllTextAsync(exportPath);
            Assert.Contains("\n  \"DatabasePath\"", exportContent, StringComparison.Ordinal);

            var exportPayload = ParseJson(exportContent);
            var entities = GetPropertyIgnoreCase(exportPayload, "entities").EnumerateArray().ToArray();
            Assert.NotEmpty(entities);
            Assert.Contains(entities, entity => string.Equals(GetPropertyIgnoreCase(entity, "name").GetString(), "v1_variables", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            TryDelete(exportPath);
            Directory.Delete(sampleRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExportJsonCommandWithExplicitFileName_WritesToRequestedLocation()
    {
        var sampleRoot = CreateTemporarySampleSolutionCopy();
        var databasePath = Path.Combine(sampleRoot, ".langquery.SampleSolution.db.sqlite");
        var exportPath = Path.Combine(sampleRoot, "custom-export.json");
        DeleteDatabaseFiles(databasePath);
        TryDelete(exportPath);

        try
        {
            var result = await RunCliAsync(sampleRoot, "exportjson", exportPath);
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.True(File.Exists(exportPath));

            var data = GetPropertyIgnoreCase(payload, "data");
            var reportedPath = GetPropertyIgnoreCase(data, "export_path").GetString();
            Assert.Equal(Path.GetFullPath(exportPath), reportedPath);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            TryDelete(exportPath);
            Directory.Delete(sampleRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExportJsonCommand_WithLargerDatabase_ProducesCompleteRows()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "langquery-export-large", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var projectPath = Path.Combine(tempDirectory, "src", "Large", "Large.csproj");
        var solutionPath = Path.Combine(tempDirectory, "Large.sln");
        var databasePath = Path.Combine(tempDirectory, "large.db.sqlite");
        var exportPath = Path.Combine(tempDirectory, "large-export.json");

        WriteProject(projectPath, "Anchor");
        var projectDirectory = Path.GetDirectoryName(projectPath)
                               ?? throw new DirectoryNotFoundException($"Could not derive project directory from '{projectPath}'.");

        for (var i = 0; i < 250; i++)
        {
            var sourcePath = Path.Combine(projectDirectory, $"Generated{i:D3}.cs");
            File.WriteAllText(sourcePath, $$"""
                namespace TempSolution;

                public sealed class Generated{{i:D3}}
                {
                    public int Value => {{i}};
                }
                """);
        }

        WriteSolution(solutionPath, "Large", "src/Large/Large.csproj", "33333333-3333-3333-3333-333333333333");
        DeleteDatabaseFiles(databasePath);
        TryDelete(exportPath);

        try
        {
            var result = await RunCliAsync(
                GetRepositoryRoot(),
                "exportjson",
                exportPath,
                "--solution",
                solutionPath,
                "--db",
                databasePath);
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.True(File.Exists(exportPath));

            var exportedJson = await File.ReadAllTextAsync(exportPath);
            var exportedPayload = ParseJson(exportedJson);
            var entities = GetPropertyIgnoreCase(exportedPayload, "entities").EnumerateArray().ToArray();
            var v1Files = entities.Single(entity => string.Equals(GetPropertyIgnoreCase(entity, "name").GetString(), "v1_files", StringComparison.OrdinalIgnoreCase));
            var v1FilesRows = GetPropertyIgnoreCase(v1Files, "rows").EnumerateArray().ToArray();

            Assert.True(v1FilesRows.Length >= 251);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            TryDelete(exportPath);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExportJsonCommand_WithExistingDb_DoesNotRequireSolution()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "langquery-export-existing-db", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var databasePath = Path.Combine(tempDirectory, "existing.db.sqlite");
        var exportPath = Path.Combine(tempDirectory, "existing-export.json");
        DeleteDatabaseFiles(databasePath);
        TryDelete(exportPath);

        try
        {
            var scanResult = await RunCliAsync(
                GetRepositoryRoot(),
                "scan",
                "--solution",
                GetSampleSolutionRoot(),
                "--db",
                databasePath);
            var scanPayload = ParseJson(scanResult.StdOut);
            Assert.Equal(0, scanResult.ExitCode);
            Assert.True(scanPayload.GetProperty("success").GetBoolean());

            var exportResult = await RunCliAsync(
                tempDirectory,
                "exportjson",
                exportPath,
                "--db",
                databasePath);
            var exportPayload = ParseJson(exportResult.StdOut);

            Assert.Equal(0, exportResult.ExitCode);
            Assert.True(exportPayload.GetProperty("success").GetBoolean());
            Assert.Equal("exportjson", exportPayload.GetProperty("command").GetString());
            Assert.True(File.Exists(exportPath));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            TryDelete(exportPath);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SimpleSchemaCommand_ReturnsQueryFocusedFieldsAndKnownConstants()
    {
        var sampleRoot = CreateTemporarySampleSolutionCopy();

        try
        {
            var result = await RunCliAsync(sampleRoot, "simpleschema", "--pretty");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.Equal("simpleschema", payload.GetProperty("command").GetString());

            var data = GetPropertyIgnoreCase(payload, "data");
            var entities = GetPropertyIgnoreCase(data, "entities");
            Assert.NotEmpty(entities.EnumerateObject());

            var v1Types = GetPropertyIgnoreCase(entities, "v1_types");
            var typeColumns = v1Types.EnumerateObject()
                .SelectMany(bucket => bucket.Value.EnumerateArray())
                .Select(column => column.GetString())
                .Where(column => !string.IsNullOrWhiteSpace(column))
                .ToArray();
            Assert.Contains(typeColumns, column => string.Equals(column, "kind", StringComparison.OrdinalIgnoreCase));

            var v1Methods = GetPropertyIgnoreCase(entities, "v1_methods");
            var methodColumns = v1Methods.EnumerateObject()
                .SelectMany(bucket => bucket.Value.EnumerateArray())
                .Select(column => column.GetString())
                .Where(column => !string.IsNullOrWhiteSpace(column))
                .ToArray();
            Assert.Contains(methodColumns, column => string.Equals(column, "parameters", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(methodColumns, column => string.Equals(column, "parameter_count", StringComparison.OrdinalIgnoreCase));

            var constants = GetPropertyIgnoreCase(data, "constants");
            Assert.True(constants.EnumerateObject().Count() >= 7);

            var knownTypeKinds = GetPropertyIgnoreCase(constants, "v1_types.kind")
                .EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();

            Assert.Contains("Class", knownTypeKinds);
            Assert.Contains("Record", knownTypeKinds);
            Assert.Contains("Enum", knownTypeKinds);

            var knownImplementationKinds = GetPropertyIgnoreCase(constants, "v1_methods.implementation_kind")
                .EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();

            Assert.Contains("Method", knownImplementationKinds);
            Assert.Contains("LocalFunction", knownImplementationKinds);
            Assert.Contains("Lambda", knownImplementationKinds);
            Assert.Contains("AnonymousMethod", knownImplementationKinds);

            var knownSymbolKinds = GetPropertyIgnoreCase(constants, "v1_symbol_refs.symbol_kind")
                .EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();

            Assert.Contains("Variable", knownSymbolKinds);
            Assert.Contains("Method", knownSymbolKinds);
            Assert.Contains("Property", knownSymbolKinds);
        }
        finally
        {
            Directory.Delete(sampleRoot, recursive: true);
        }
    }

    [Fact]
    public async Task NoArgsInvocation_ReturnsHelpPayload()
    {
        var result = await RunCliAsync(GetRepositoryRoot());
        var payload = ParseJson(result.StdOut);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("help", payload.GetProperty("command").GetString());
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.True(HasPropertyIgnoreCase(payload, "data"));
    }

    [Fact]
    public async Task BareWordToken_IsTreatedAsShortFormSqlAndFailsValidation()
    {
        var result = await RunCliAsync(GetRepositoryRoot(), "totally-unknown-command");
        var payload = ParseJson(result.StdOut);

        Assert.Equal(1, result.ExitCode);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("sql", payload.GetProperty("command").GetString());
        Assert.Contains("Only SELECT, WITH, or EXPLAIN", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlCommand_WithoutQueryOption_ReturnsError()
    {
        var result = await RunCliAsync(GetRepositoryRoot(), "sql");
        var payload = ParseJson(result.StdOut);

        Assert.Equal(1, result.ExitCode);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("Missing required option '--query <sql>'", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanCommand_UnknownOption_ReturnsError()
    {
        var result = await RunCliAsync(GetRepositoryRoot(), "scan", "--soluton", "tests/sample_solution");
        var payload = ParseJson(result.StdOut);

        Assert.Equal(1, result.ExitCode);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("Unknown option '--soluton'", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlCommand_MissingQueryValue_ReturnsExplicitError()
    {
        var result = await RunCliAsync(GetRepositoryRoot(), "sql", "--query", "--db", "temp.db.sqlite");
        var payload = ParseJson(result.StdOut);

        Assert.Equal(1, result.ExitCode);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("Option '--query' requires a value", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanCommand_MissingDbValue_ReturnsExplicitError()
    {
        var result = await RunCliAsync(GetRepositoryRoot(), "scan", "--db");
        var payload = ParseJson(result.StdOut);

        Assert.Equal(1, result.ExitCode);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("Option '--db' requires a value", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlCommand_InvalidMaxRowsValue_ReturnsError()
    {
        var sampleRoot = GetSampleSolutionRoot();
        var result = await RunCliAsync(sampleRoot, "sql", "--query", "SELECT 1 AS n", "--max-rows", "0");
        var payload = ParseJson(result.StdOut);

        Assert.Equal(1, result.ExitCode);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("--max-rows", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlCommand_InvalidTimeoutValue_ReturnsError()
    {
        var sampleRoot = GetSampleSolutionRoot();
        var result = await RunCliAsync(sampleRoot, "sql", "--query", "SELECT 1 AS n", "--timeout-ms", "-5");
        var payload = ParseJson(result.StdOut);

        Assert.Equal(1, result.ExitCode);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("--timeout-ms", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlCommand_OptionNames_AreCaseInsensitive()
    {
        var sampleRoot = GetSampleSolutionRoot();
        var databasePath = Path.Combine(Path.GetTempPath(), "langquery-case-options", Guid.NewGuid().ToString("N"), "case.db.sqlite");
        DeleteDatabaseFiles(databasePath);

        try
        {
            var result = await RunCliAsync(
                GetRepositoryRoot(),
                "sql",
                "--SoLuTiOn",
                sampleRoot,
                "--DB",
                databasePath,
                "--QuErY",
                "SELECT 1 AS c",
                "--MAX-ROWS",
                "1",
                "--TIMEOUT-MS",
                "1000",
                "--PrEtTy");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.Equal("sql", payload.GetProperty("command").GetString());
            Assert.Equal(1, ReadSingleCount(payload));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ScanCommand_WithNonexistentSolutionPath_ReturnsError()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "langquery-missing-sln", Guid.NewGuid().ToString("N"));
        var result = await RunCliAsync(GetRepositoryRoot(), "scan", "--solution", missingPath);
        var payload = ParseJson(result.StdOut);

        Assert.Equal(1, result.ExitCode);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("does not exist", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanCommand_WithNonSolutionFilePath_ReturnsError()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "langquery-not-sln", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var plainFile = Path.Combine(tempDirectory, "notes.txt");
        await File.WriteAllTextAsync(plainFile, "not a solution", CancellationToken.None);

        try
        {
            var result = await RunCliAsync(GetRepositoryRoot(), "scan", "--solution", plainFile);
            var payload = ParseJson(result.StdOut);

            Assert.Equal(1, result.ExitCode);
            Assert.False(payload.GetProperty("success").GetBoolean());
            Assert.Contains("not a .sln", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallSkillCommand_RejectsUnsupportedOption()
    {
        var result = await RunCliAsync(GetRepositoryRoot(), "installskill", "codex", "--db", "ignored.db");
        var payload = ParseJson(result.StdOut);

        Assert.Equal(1, result.ExitCode);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("Unknown option", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--db", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallSkillCommand_RejectsUnsupportedFlag()
    {
        var result = await RunCliAsync(GetRepositoryRoot(), "installskill", "codex", "--changed-only");
        var payload = ParseJson(result.StdOut);

        Assert.Equal(1, result.ExitCode);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("Unknown option", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--changed-only", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallSkillCommand_OpenCodeTarget_CreatesOnlyOpenCodeSkillFile()
    {
        var sampleRoot = CreateTemporarySampleSolutionCopy();

        try
        {
            var result = await RunCliAsync(sampleRoot, "installskill", "opencode");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());

            var openCodePath = Path.Combine(sampleRoot, ".opencode", "skills", "langquery", "SKILL.md");
            var codexPath = Path.Combine(sampleRoot, ".codex", "skills", "langquery", "SKILL.md");
            var claudePath = Path.Combine(sampleRoot, ".claude", "skills", "langquery", "SKILL.md");

            Assert.True(File.Exists(openCodePath));
            Assert.False(File.Exists(codexPath));
            Assert.False(File.Exists(claudePath));
        }
        finally
        {
            Directory.Delete(sampleRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SchemaCommand_WithExplicitDatabasePath_WorksWithoutSolutionOption()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "langquery-schema-db", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var dbPath = Path.Combine(tempDirectory, "schema.db.sqlite");
        DeleteDatabaseFiles(dbPath);

        try
        {
            var result = await RunCliAsync(GetRepositoryRoot(), "schema", "--db", dbPath);
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.Equal("schema", payload.GetProperty("command").GetString());

            var entities = GetPropertyIgnoreCase(GetPropertyIgnoreCase(payload, "data"), "entities")
                .EnumerateArray()
                .ToArray();
            Assert.NotEmpty(entities);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanCommand_WithExistingNonLangQueryDatabase_RefusesMutation()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "langquery-foreign-db", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var dbPath = Path.Combine(tempDirectory, "foreign.db.sqlite");

        try
        {
            await CreateForeignDatabaseAsync(dbPath);

            var result = await RunCliAsync(
                GetRepositoryRoot(),
                "scan",
                "--solution",
                GetSampleSolutionRoot(),
                "--db",
                dbPath);
            var payload = ParseJson(result.StdOut);

            Assert.Equal(1, result.ExitCode);
            Assert.False(payload.GetProperty("success").GetBoolean());
            Assert.Contains("not a LangQuery database", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            await using var verifyConnection = new SqliteConnection($"Data Source={dbPath}");
            await verifyConnection.OpenAsync(CancellationToken.None);
            await using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'notes';";
            var notesTableCount = Convert.ToInt32(await verifyCommand.ExecuteScalarAsync(CancellationToken.None));
            Assert.Equal(1, notesTableCount);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ShortFormQuery_WithTrailingOptions_ParsesQueryAndRunsSuccessfully()
    {
        var sampleRoot = GetSampleSolutionRoot();
        var databasePath = Path.Combine(sampleRoot, ".langquery.SampleSolution.db.sqlite");
        DeleteDatabaseFiles(databasePath);

        try
        {
            var result = await RunCliAsync(sampleRoot, "SELECT COUNT(*) AS c FROM v1_files", "--max-rows", "1");
            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.Equal("sql", payload.GetProperty("command").GetString());
            Assert.True(ReadSingleCount(payload) >= 1);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ExplicitEmptySolutionFile_IsSupportedAndIndexesZeroFiles()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "langquery-empty-sln", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var emptySolutionPath = Path.Combine(tempDirectory, "Empty.sln");
        var databasePath = Path.Combine(tempDirectory, "empty.db.sqlite");
        DeleteDatabaseFiles(databasePath);

        WriteEmptySolution(emptySolutionPath);

        try
        {
            var result = await RunCliAsync(
                GetRepositoryRoot(),
                "sql",
                "--solution",
                emptySolutionPath,
                "--db",
                databasePath,
                "--query",
                "SELECT COUNT(*) AS c FROM v1_files");

            var payload = ParseJson(result.StdOut);

            Assert.Equal(0, result.ExitCode);
            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.Equal(0, ReadSingleCount(payload));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SqlCommand_WithMutationQuery_ReturnsValidationError()
    {
        var sampleRoot = GetSampleSolutionRoot();

        var result = await RunCliAsync(sampleRoot, "sql", "--query", "DELETE FROM v1_files");
        var payload = ParseJson(result.StdOut);

        Assert.Equal(1, result.ExitCode);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("read-only", GetPropertyIgnoreCase(payload, "error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CliRunResult> RunCliAsync(string workingDirectory, params string[] args)
    {
        var cliDllPath = Path.Combine(GetRepositoryRoot(), "src", "LangQuery.Cli", "bin", "Debug", "net8.0", "LangQuery.Cli.dll");
        if (!File.Exists(cliDllPath))
        {
            throw new FileNotFoundException($"Could not find CLI assembly at '{cliDllPath}'.");
        }

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(cliDllPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return new CliRunResult(
            process.ExitCode,
            (await stdoutTask).Trim(),
            (await stderrTask).Trim());
    }

    private static JsonElement ParseJson(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        return document.RootElement.Clone();
    }

    private static int ReadSingleCount(JsonElement payload)
    {
        var data = GetPropertyIgnoreCase(payload, "data");
        var rows = GetPropertyIgnoreCase(data, "rows");
        var value = rows[0].GetProperty("c");

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt32(),
            JsonValueKind.String => int.Parse(value.GetString() ?? "0"),
            _ => throw new InvalidOperationException("Unexpected count value in CLI JSON payload.")
        };
    }

    private static JsonElement GetPropertyIgnoreCase(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new KeyNotFoundException($"Property '{propertyName}' was not found in CLI JSON payload.");
    }

    private static bool HasPropertyIgnoreCase(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "LangQuery.sln");
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing LangQuery.sln.");
    }

    private static string GetSampleSolutionRoot()
    {
        var sampleRoot = Path.Combine(GetRepositoryRoot(), "tests", "sample_solution");
        if (!Directory.Exists(sampleRoot))
        {
            throw new DirectoryNotFoundException($"Could not locate sample solution folder at '{sampleRoot}'.");
        }

        return sampleRoot;
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        TryDelete(databasePath);
        TryDelete(databasePath + "-wal");
        TryDelete(databasePath + "-shm");
        TryDelete(databasePath + "-journal");
    }

    private static string CreateTemporarySampleSolutionCopy()
    {
        var sourceRoot = GetSampleSolutionRoot();
        var destinationRoot = Path.Combine(Path.GetTempPath(), "langquery-cli-sample-copy", Guid.NewGuid().ToString("N"));
        CopyDirectory(sourceRoot, destinationRoot);
        return destinationRoot;
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var filePath in Directory.GetFiles(sourcePath))
        {
            var fileName = Path.GetFileName(filePath);
            var destinationFile = Path.Combine(destinationPath, fileName);
            File.Copy(filePath, destinationFile, overwrite: true);
        }

        foreach (var subDirectory in Directory.GetDirectories(sourcePath))
        {
            var directoryName = Path.GetFileName(subDirectory);
            var destinationSubDirectory = Path.Combine(destinationPath, directoryName);
            CopyDirectory(subDirectory, destinationSubDirectory);
        }
    }

    private static void WriteProject(string projectPath, string className)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)
                               ?? throw new DirectoryNotFoundException($"Could not derive project directory from '{projectPath}'.");

        Directory.CreateDirectory(projectDirectory);

        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var sourcePath = Path.Combine(projectDirectory, className + ".cs");
        File.WriteAllText(sourcePath, $$"""
            namespace TempSolution;

            public sealed class {{className}}
            {
            }
            """);
    }

    private static void WriteSolution(string solutionPath, string projectName, string relativeProjectPath, string projectGuid)
    {
        const string csharpProjectTypeGuid = "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";

        File.WriteAllText(solutionPath, $$"""
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.0.31903.59
            MinimumVisualStudioVersion = 10.0.40219.1
            Project("{{{csharpProjectTypeGuid}}}") = "{{projectName}}", "{{relativeProjectPath}}", "{{{projectGuid}}}"
            EndProject
            Global
            EndGlobal
            """);
    }

    private static void WriteEmptySolution(string solutionPath)
    {
        File.WriteAllText(solutionPath, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.0.31903.59
            MinimumVisualStudioVersion = 10.0.40219.1
            Global
            EndGlobal
            """);
    }

    private static void TryDelete(string path)
    {
        SqliteConnection.ClearAllPools();

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static async Task CreateForeignDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE notes(id INTEGER PRIMARY KEY, content TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private sealed record CliRunResult(int ExitCode, string StdOut, string StdErr);
}
