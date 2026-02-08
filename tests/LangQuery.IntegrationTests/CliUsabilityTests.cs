using System.Diagnostics;
using System.Text.Json;
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
            Assert.Contains("Re-scan after every code change", skillContent, StringComparison.Ordinal);
            Assert.Contains("--changed-only", skillContent, StringComparison.Ordinal);
            Assert.DoesNotContain("outdated-skill-content", skillContent, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(sampleRoot, recursive: true);
        }
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
            Assert.Equal(0, ReadSingleCount(payload));
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
            var entities = GetPropertyIgnoreCase(data, "entities").EnumerateArray().ToArray();
            Assert.NotEmpty(entities);

            var v1Types = entities.Single(entity => string.Equals(GetPropertyIgnoreCase(entity, "name").GetString(), "v1_types", StringComparison.OrdinalIgnoreCase));
            var typeColumns = GetPropertyIgnoreCase(v1Types, "columns").EnumerateArray().ToArray();
            Assert.Contains(typeColumns, column => string.Equals(GetPropertyIgnoreCase(column, "name").GetString(), "kind", StringComparison.OrdinalIgnoreCase));
            Assert.All(typeColumns, column =>
            {
                Assert.True(HasPropertyIgnoreCase(column, "name"));
                Assert.True(HasPropertyIgnoreCase(column, "type"));
                Assert.False(HasPropertyIgnoreCase(column, "notnull"));
                Assert.False(HasPropertyIgnoreCase(column, "primarykey"));
            });

            var v1Methods = entities.Single(entity => string.Equals(GetPropertyIgnoreCase(entity, "name").GetString(), "v1_methods", StringComparison.OrdinalIgnoreCase));
            var methodColumns = GetPropertyIgnoreCase(v1Methods, "columns").EnumerateArray().ToArray();
            Assert.Contains(methodColumns, column => string.Equals(GetPropertyIgnoreCase(column, "name").GetString(), "parameter_count", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(methodColumns, column => string.Equals(GetPropertyIgnoreCase(column, "name").GetString(), "parameters", StringComparison.OrdinalIgnoreCase));

            var constants = GetPropertyIgnoreCase(data, "constants").EnumerateArray().ToArray();
            Assert.True(constants.Length >= 7);

            var typeKindConstants = constants.Single(constant => string.Equals(GetPropertyIgnoreCase(constant, "location").GetString(), "v1_types.kind", StringComparison.OrdinalIgnoreCase));
            var knownTypeKinds = GetPropertyIgnoreCase(typeKindConstants, "values")
                .EnumerateArray()
                .Select(item => GetPropertyIgnoreCase(item, "value").GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();

            Assert.Contains("Class", knownTypeKinds);
            Assert.Contains("Record", knownTypeKinds);
            Assert.Contains("Enum", knownTypeKinds);

            var implementationKindConstants = constants.Single(constant => string.Equals(GetPropertyIgnoreCase(constant, "location").GetString(), "v1_methods.implementation_kind", StringComparison.OrdinalIgnoreCase));
            var knownImplementationKinds = GetPropertyIgnoreCase(implementationKindConstants, "values")
                .EnumerateArray()
                .Select(item => GetPropertyIgnoreCase(item, "value").GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();

            Assert.Contains("Method", knownImplementationKinds);
            Assert.Contains("LocalFunction", knownImplementationKinds);
            Assert.Contains("Lambda", knownImplementationKinds);
            Assert.Contains("AnonymousMethod", knownImplementationKinds);

            var symbolKindConstants = constants.Single(constant => string.Equals(GetPropertyIgnoreCase(constant, "location").GetString(), "v1_symbol_refs.symbol_kind", StringComparison.OrdinalIgnoreCase));
            var knownSymbolKinds = GetPropertyIgnoreCase(symbolKindConstants, "values")
                .EnumerateArray()
                .Select(item => GetPropertyIgnoreCase(item, "value").GetString())
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

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record CliRunResult(int ExitCode, string StdOut, string StdErr);
}
