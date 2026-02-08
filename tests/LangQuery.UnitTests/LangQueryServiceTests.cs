using System.Security.Cryptography;
using LangQuery.Core.Abstractions;
using LangQuery.Core.Models;
using LangQuery.Core.Services;

namespace LangQuery.UnitTests;

public sealed class LangQueryServiceTests
{
    [Fact]
    public async Task QueryAsync_InitializesStorageBeforeExecutingQuery()
    {
        var extractor = new FakeExtractor();
        var storage = new FakeStorageEngine
        {
            QueryResultToReturn = new QueryResult(["value"], [new Dictionary<string, object?> { ["value"] = 1 }], false, TimeSpan.Zero)
        };

        var service = new LangQueryService(extractor, storage);

        var result = await service.QueryAsync(new QueryOptions("db.sqlite", "SELECT 1"), CancellationToken.None);

        Assert.Equal(2, storage.CallLog.Count);
        Assert.Equal("InitializeReadOnly", storage.CallLog[0]);
        Assert.Equal("ExecuteReadOnlyQuery", storage.CallLog[1]);
        Assert.Single(result.Rows);
    }

    [Fact]
    public async Task GetSchemaAsync_InitializesStorageBeforeDescribingSchema()
    {
        var extractor = new FakeExtractor();
        var storage = new FakeStorageEngine
        {
            SchemaToReturn = new SchemaDescription(5, [new SchemaEntity("v1_files", "view", "", [])])
        };

        var service = new LangQueryService(extractor, storage);

        var result = await service.GetSchemaAsync(new SchemaOptions("db.sqlite"), CancellationToken.None);

        Assert.Equal(2, storage.CallLog.Count);
        Assert.Equal("InitializeReadOnly", storage.CallLog[0]);
        Assert.Equal("DescribeSchema", storage.CallLog[1]);
        Assert.Equal(5, result.SchemaVersion);
    }

    [Fact]
    public async Task ScanAsync_FullRebuildPersistsWithFullRebuildFlag()
    {
        var workspace = CreateWorkspace([("A.cs", "namespace Demo; class A {}")]);
        var databasePath = Path.Combine(workspace, "facts.db");

        try
        {
            var extractor = new FakeExtractor();
            var storage = new FakeStorageEngine();
            var service = new LangQueryService(extractor, storage);

            var summary = await service.ScanAsync(new ScanOptions(workspace, databasePath, ChangedOnly: false), CancellationToken.None);

            Assert.Equal(1, summary.FilesDiscovered);
            Assert.Equal(1, summary.FilesScanned);
            Assert.True(storage.LastPersistFullRebuild);
            Assert.Single(storage.LastPersistFacts);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_ChangedOnlyPersistsWithIncrementalFlag()
    {
        var workspace = CreateWorkspace([("A.cs", "namespace Demo; class A {}")]);
        var databasePath = Path.Combine(workspace, "facts.db");
        var aPath = Path.GetFullPath(Path.Combine(workspace, "A.cs"));

        try
        {
            var extractor = new FakeExtractor();
            var storage = new FakeStorageEngine
            {
                IndexedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [aPath] = "OLD_HASH"
                }
            };

            var service = new LangQueryService(extractor, storage);

            var summary = await service.ScanAsync(new ScanOptions(workspace, databasePath, ChangedOnly: true), CancellationToken.None);

            Assert.Equal(1, summary.FilesDiscovered);
            Assert.Equal(1, summary.FilesScanned);
            Assert.False(storage.LastPersistFullRebuild);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_ChangedOnlyScansOnlyChangedFilesAndCountsUnchanged()
    {
        var workspace = CreateWorkspace(
            [("A.cs", "namespace Demo; class A {}"), ("B.cs", "namespace Demo; class B {}")]);

        var databasePath = Path.Combine(workspace, "facts.db");
        var aPath = Path.GetFullPath(Path.Combine(workspace, "A.cs"));
        var bPath = Path.GetFullPath(Path.Combine(workspace, "B.cs"));

        try
        {
            var extractor = new FakeExtractor();
            var storage = new FakeStorageEngine
            {
                IndexedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [aPath] = ComputeSha256(aPath),
                    [bPath] = "STALE_HASH"
                }
            };

            var service = new LangQueryService(extractor, storage);

            var summary = await service.ScanAsync(new ScanOptions(workspace, databasePath, ChangedOnly: true), CancellationToken.None);

            Assert.Equal(2, summary.FilesDiscovered);
            Assert.Equal(1, summary.FilesScanned);
            Assert.Equal(1, summary.FilesUnchanged);
            Assert.Single(storage.LastPersistFacts);
            Assert.Equal(bPath, storage.LastPersistFacts[0].Path);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_ChangedOnlyTracksRemovedFiles()
    {
        var workspace = CreateWorkspace([("A.cs", "namespace Demo; class A {}")]);

        var databasePath = Path.Combine(workspace, "facts.db");
        var aPath = Path.GetFullPath(Path.Combine(workspace, "A.cs"));
        var removedPath = Path.GetFullPath(Path.Combine(workspace, "Removed.cs"));

        try
        {
            var extractor = new FakeExtractor();
            var storage = new FakeStorageEngine
            {
                IndexedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [aPath] = ComputeSha256(aPath),
                    [removedPath] = "ANY_HASH"
                }
            };

            var service = new LangQueryService(extractor, storage);

            var summary = await service.ScanAsync(new ScanOptions(workspace, databasePath, ChangedOnly: true), CancellationToken.None);

            Assert.Equal(1, summary.FilesRemoved);
            Assert.Contains(removedPath, storage.LastRemovedPaths, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_DirectoryScanIgnoresBinDirectory()
    {
        var workspace = CreateWorkspace(
            [("A.cs", "namespace Demo; class A {}"), (Path.Combine("bin", "Ignored.cs"), "namespace Demo; class Ignored {}")]);

        var databasePath = Path.Combine(workspace, "facts.db");

        try
        {
            var service = new LangQueryService(new FakeExtractor(), new FakeStorageEngine());

            var summary = await service.ScanAsync(new ScanOptions(workspace, databasePath), CancellationToken.None);

            Assert.Equal(1, summary.FilesDiscovered);
            Assert.Equal(1, summary.FilesScanned);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_DirectoryScanIgnoresObjDirectory()
    {
        var workspace = CreateWorkspace(
            [("A.cs", "namespace Demo; class A {}"), (Path.Combine("obj", "Ignored.cs"), "namespace Demo; class Ignored {}")]);

        var databasePath = Path.Combine(workspace, "facts.db");

        try
        {
            var service = new LangQueryService(new FakeExtractor(), new FakeStorageEngine());

            var summary = await service.ScanAsync(new ScanOptions(workspace, databasePath), CancellationToken.None);

            Assert.Equal(1, summary.FilesDiscovered);
            Assert.Equal(1, summary.FilesScanned);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_DirectoryScanIgnoresGitDirectory()
    {
        var workspace = CreateWorkspace(
            [("A.cs", "namespace Demo; class A {}"), (Path.Combine(".git", "Ignored.cs"), "namespace Demo; class Ignored {}")]);

        var databasePath = Path.Combine(workspace, "facts.db");

        try
        {
            var service = new LangQueryService(new FakeExtractor(), new FakeStorageEngine());

            var summary = await service.ScanAsync(new ScanOptions(workspace, databasePath), CancellationToken.None);

            Assert.Equal(1, summary.FilesDiscovered);
            Assert.Equal(1, summary.FilesScanned);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_DirectoryScanIgnoresMixedCaseIgnoredDirectories()
    {
        var workspace = CreateWorkspace(
            [
                ("A.cs", "namespace Demo; class A {}"),
                (Path.Combine("Bin", "Ignored.cs"), "namespace Demo; class IgnoredBin {}"),
                (Path.Combine("OBJ", "Ignored.cs"), "namespace Demo; class IgnoredObj {}"),
                (Path.Combine(".VS", "Ignored.cs"), "namespace Demo; class IgnoredVs {}")
            ]);

        var databasePath = Path.Combine(workspace, "facts.db");

        try
        {
            var service = new LangQueryService(new FakeExtractor(), new FakeStorageEngine());

            var summary = await service.ScanAsync(new ScanOptions(workspace, databasePath), CancellationToken.None);

            Assert.Equal(1, summary.FilesDiscovered);
            Assert.Equal(1, summary.FilesScanned);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_WhenScanRootIsAFile_UsesContainingFolder()
    {
        var workspace = CreateWorkspace(
            [("A.cs", "namespace Demo; class A {}"), ("B.cs", "namespace Demo; class B {}")]);

        var databasePath = Path.Combine(workspace, "facts.db");
        var filePath = Path.Combine(workspace, "A.cs");

        try
        {
            var service = new LangQueryService(new FakeExtractor(), new FakeStorageEngine());

            var summary = await service.ScanAsync(new ScanOptions(filePath, databasePath), CancellationToken.None);

            Assert.Equal(2, summary.FilesDiscovered);
            Assert.Equal(2, summary.FilesScanned);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_SolutionScanIgnoresMixedCaseIgnoredDirectories()
    {
        var workspace = CreateWorkspace(
            [
                (Path.Combine("src", "App", "A.cs"), "namespace Demo; class A {}"),
                (Path.Combine("src", "App", "Bin", "Ignored.cs"), "namespace Demo; class IgnoredBin {}"),
                (Path.Combine("src", "App", "OBJ", "Ignored.cs"), "namespace Demo; class IgnoredObj {}"),
                (Path.Combine("src", "App", ".VS", "Ignored.cs"), "namespace Demo; class IgnoredVs {}"),
                (Path.Combine("src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>")
            ]);

        var solutionPath = Path.Combine(workspace, "Demo.sln");
        var databasePath = Path.Combine(workspace, "facts.db");
        WriteSolutionFile(solutionPath, ["src\\App\\App.csproj"]);

        try
        {
            var service = new LangQueryService(new FakeExtractor(), new FakeStorageEngine());

            var summary = await service.ScanAsync(new ScanOptions(solutionPath, databasePath), CancellationToken.None);

            Assert.Equal(1, summary.FilesDiscovered);
            Assert.Equal(1, summary.FilesScanned);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_SolutionScanSkipsMissingProjects()
    {
        var workspace = CreateWorkspace(
            [
                (Path.Combine("src", "App", "A.cs"), "namespace Demo; class A {}"),
                (Path.Combine("src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>")
            ]);

        var solutionPath = Path.Combine(workspace, "Demo.sln");
        var databasePath = Path.Combine(workspace, "facts.db");
        WriteSolutionFile(solutionPath, ["src\\App\\App.csproj", "src\\Missing\\Missing.csproj"]);

        try
        {
            var service = new LangQueryService(new FakeExtractor(), new FakeStorageEngine());

            var summary = await service.ScanAsync(new ScanOptions(solutionPath, databasePath), CancellationToken.None);

            Assert.Equal(1, summary.FilesDiscovered);
            Assert.Equal(1, summary.FilesScanned);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_SolutionScanRejectsProjectReferencesOutsideSolutionRoot()
    {
        var workspace = CreateWorkspace(
            [
                (Path.Combine("src", "App", "A.cs"), "namespace Demo; class A {}")
            ]);

        var outsideRoot = Path.Combine(Path.GetTempPath(), "langquery-service-tests-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(outsideRoot, "Escape"));
        File.WriteAllText(Path.Combine(outsideRoot, "Escape", "Escape.cs"), "namespace Demo; class Escape {}");
        File.WriteAllText(Path.Combine(outsideRoot, "Escape", "Escape.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        var appProjectPath = Path.Combine(workspace, "src", "App", "App.csproj");
        var relativeEscapeProjectPath = Path.GetRelativePath(Path.GetDirectoryName(appProjectPath)!, Path.Combine(outsideRoot, "Escape", "Escape.csproj"));
        var appProjectText = $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"{relativeEscapeProjectPath.Replace('\\', '/')}\" /></ItemGroup></Project>";
        File.WriteAllText(appProjectPath, appProjectText);

        var solutionPath = Path.Combine(workspace, "Demo.sln");
        var databasePath = Path.Combine(workspace, "facts.db");
        WriteSolutionFile(solutionPath, ["src\\App\\App.csproj"]);

        try
        {
            var service = new LangQueryService(new FakeExtractor(), new FakeStorageEngine());
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ScanAsync(new ScanOptions(solutionPath, databasePath), CancellationToken.None));

            Assert.Contains("outside the solution root", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_SolutionScanRejectsSolutionProjectsOutsideSolutionRoot()
    {
        var workspace = CreateWorkspace([]);

        var outsideRoot = Path.Combine(Path.GetTempPath(), "langquery-service-tests-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(outsideRoot, "Escape"));
        File.WriteAllText(Path.Combine(outsideRoot, "Escape", "Escape.cs"), "namespace Demo; class Escape {}");
        File.WriteAllText(Path.Combine(outsideRoot, "Escape", "Escape.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        var solutionPath = Path.Combine(workspace, "Demo.sln");
        var databasePath = Path.Combine(workspace, "facts.db");
        var relativeEscapeProjectPath = Path.GetRelativePath(workspace, Path.Combine(outsideRoot, "Escape", "Escape.csproj")).Replace('/', '\\');
        WriteSolutionFile(solutionPath, [relativeEscapeProjectPath]);

        try
        {
            var service = new LangQueryService(new FakeExtractor(), new FakeStorageEngine());
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ScanAsync(new ScanOptions(solutionPath, databasePath), CancellationToken.None));

            Assert.Contains("outside the solution root", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_SolutionScanRejectsCompileIncludesOutsideSolutionRoot()
    {
        var workspace = CreateWorkspace(
            [
                (Path.Combine("src", "App", "A.cs"), "namespace Demo; class A {}")
            ]);

        var outsideRoot = Path.Combine(Path.GetTempPath(), "langquery-service-tests-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(outsideRoot, "Escape"));
        var outsideFilePath = Path.Combine(outsideRoot, "Escape", "Outside.cs");
        File.WriteAllText(outsideFilePath, "namespace Demo; class Outside {}");

        var appProjectPath = Path.Combine(workspace, "src", "App", "App.csproj");
        var relativeOutsidePath = Path.GetRelativePath(Path.GetDirectoryName(appProjectPath)!, outsideFilePath).Replace('\\', '/');
        var appProjectText = $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"{relativeOutsidePath}\" /></ItemGroup></Project>";
        File.WriteAllText(appProjectPath, appProjectText);

        var solutionPath = Path.Combine(workspace, "Demo.sln");
        var databasePath = Path.Combine(workspace, "facts.db");
        WriteSolutionFile(solutionPath, ["src\\App\\App.csproj"]);

        try
        {
            var service = new LangQueryService(new FakeExtractor(), new FakeStorageEngine());
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ScanAsync(new ScanOptions(solutionPath, databasePath), CancellationToken.None));

            Assert.Contains("Compile Include", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("outside the solution root", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_SolutionScanSupportsPrefixedRecursiveCompileGlob()
    {
        var workspace = CreateWorkspace(
            [
                (Path.Combine("src", "Top.cs"), "namespace Demo; class Top {}"),
                (Path.Combine("src", "Nested", "Inner.cs"), "namespace Demo; class Inner {}"),
                ("Other.cs", "namespace Demo; class Other {}"),
                ("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"src/**/*.cs\" /></ItemGroup></Project>")
            ]);

        var solutionPath = Path.Combine(workspace, "Demo.sln");
        var databasePath = Path.Combine(workspace, "facts.db");
        WriteSolutionFile(solutionPath, ["App.csproj"]);

        try
        {
            var storage = new FakeStorageEngine();
            var service = new LangQueryService(new FakeExtractor(), storage);

            var summary = await service.ScanAsync(new ScanOptions(solutionPath, databasePath), CancellationToken.None);

            Assert.Equal(2, summary.FilesDiscovered);
            Assert.Equal(2, summary.FilesScanned);
            Assert.Contains(storage.LastPersistFacts, x => string.Equals(x.Path, Path.GetFullPath(Path.Combine(workspace, "src", "Top.cs")), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(storage.LastPersistFacts, x => string.Equals(x.Path, Path.GetFullPath(Path.Combine(workspace, "src", "Nested", "Inner.cs")), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(storage.LastPersistFacts, x => string.Equals(x.Path, Path.GetFullPath(Path.Combine(workspace, "Other.cs")), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_SolutionScanHonorsExplicitCompileIncludesInIgnoredFolders()
    {
        var workspace = CreateWorkspace(
            [
                ("A.cs", "namespace Demo; class A {}"),
                (Path.Combine("obj", "Explicit.cs"), "namespace Demo; class Explicit {}"),
                (Path.Combine("bin", "DefaultIgnored.cs"), "namespace Demo; class DefaultIgnored {}"),
                ("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"obj/Explicit.cs\" /></ItemGroup></Project>")
            ]);

        var solutionPath = Path.Combine(workspace, "Demo.sln");
        var databasePath = Path.Combine(workspace, "facts.db");
        WriteSolutionFile(solutionPath, ["App.csproj"]);

        try
        {
            var storage = new FakeStorageEngine();
            var service = new LangQueryService(new FakeExtractor(), storage);

            var summary = await service.ScanAsync(new ScanOptions(solutionPath, databasePath), CancellationToken.None);

            Assert.Equal(2, summary.FilesDiscovered);
            Assert.Equal(2, summary.FilesScanned);
            Assert.Contains(storage.LastPersistFacts, x => string.Equals(x.Path, Path.GetFullPath(Path.Combine(workspace, "obj", "Explicit.cs")), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(storage.LastPersistFacts, x => string.Equals(x.Path, Path.GetFullPath(Path.Combine(workspace, "bin", "DefaultIgnored.cs")), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_SolutionScanUsesStaticParsingWithoutExecutingProjectTargets()
    {
        var workspace = CreateWorkspace(
            [
                (Path.Combine("src", "App", "A.cs"), "namespace Demo; class A {}")
            ]);

        var markerPath = Path.Combine(workspace, "project-target-marker.txt");
        var appProjectPath = Path.Combine(workspace, "src", "App", "App.csproj");
        var escapedMarkerPath = markerPath.Replace("\\", "\\\\");
        var projectText = $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><Target Name=\"Probe\" BeforeTargets=\"Build\"><Exec Command=\"dotnet --version &gt; &quot;{escapedMarkerPath}&quot;\" /></Target></Project>";
        File.WriteAllText(appProjectPath, projectText);

        var solutionPath = Path.Combine(workspace, "Demo.sln");
        var databasePath = Path.Combine(workspace, "facts.db");
        WriteSolutionFile(solutionPath, ["src\\App\\App.csproj"]);

        try
        {
            var service = new LangQueryService(new FakeExtractor(), new FakeStorageEngine());

            var summary = await service.ScanAsync(new ScanOptions(solutionPath, databasePath), CancellationToken.None);

            Assert.Equal(1, summary.FilesDiscovered);
            Assert.Equal(1, summary.FilesScanned);
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_SkipsExtractorIneligibleFilesWhileKeepingDiscoveryCount()
    {
        var workspace = CreateWorkspace(
            [("A.cs", "namespace Demo; class A {}"), ("B.cs", "namespace Demo; class B {}")]);

        var databasePath = Path.Combine(workspace, "facts.db");
        var aPath = Path.GetFullPath(Path.Combine(workspace, "A.cs"));

        try
        {
            var extractor = new FakeExtractor
            {
                CanHandlePredicate = path => string.Equals(Path.GetFullPath(path), aPath, StringComparison.OrdinalIgnoreCase)
            };

            var service = new LangQueryService(extractor, new FakeStorageEngine());

            var summary = await service.ScanAsync(new ScanOptions(workspace, databasePath), CancellationToken.None);

            Assert.Equal(2, summary.FilesDiscovered);
            Assert.Equal(1, summary.FilesScanned);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_ThrowsWhenScanRootDoesNotExist()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), "langquery-nope", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(Path.GetTempPath(), "langquery-tests", Guid.NewGuid().ToString("N"), "facts.db");
        var service = new LangQueryService(new FakeExtractor(), new FakeStorageEngine());

        var error = await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            service.ScanAsync(new ScanOptions(nonexistent, databasePath), CancellationToken.None));

        Assert.Contains("does not exist", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateWorkspace(IReadOnlyList<(string RelativePath, string Content)> files)
    {
        var root = Path.Combine(Path.GetTempPath(), "langquery-service-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        foreach (var (relativePath, content) in files)
        {
            var fullPath = Path.Combine(root, relativePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, content);
        }

        return root;
    }

    private static void WriteSolutionFile(string solutionPath, IReadOnlyList<string> relativeProjectPaths)
    {
        var lines = new List<string>
        {
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "# Visual Studio Version 17"
        };

        for (var index = 0; index < relativeProjectPaths.Count; index++)
        {
            var projectPath = relativeProjectPaths[index];
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var projectGuid = Guid.NewGuid().ToString("D").ToUpperInvariant();
            lines.Add($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{projectName}\", \"{projectPath}\", \"{{{projectGuid}}}\"");
            lines.Add("EndProject");
        }

        lines.Add("Global");
        lines.Add("EndGlobal");

        File.WriteAllLines(solutionPath, lines);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private sealed class FakeExtractor : ICodeFactsExtractor
    {
        public Func<string, bool>? CanHandlePredicate { get; init; }

        public bool CanHandle(string filePath)
        {
            return CanHandlePredicate?.Invoke(filePath) ?? true;
        }

        public FileFacts Extract(string filePath, string content, string hash)
        {
            const string typeKey = "Fake.Type@1";
            const string methodKey = "Fake.Type@1:Method:Run@1:1";
            const string variableKey = "Fake.Type@1:Method:Run@1:1:Local:local@1";

            return new FileFacts(
                Path.GetFullPath(filePath),
                hash,
                "csharp",
                [new TypeFact(typeKey, "Type", "Class", "Public", "", "Fake.Type", 1, 1)],
                Array.Empty<TypeInheritanceFact>(),
                [new MethodFact(methodKey, "Run", "void", "", 0, "Public", "", "Method", null, typeKey, 1, 1, 1, 1)],
                [new LineFact(1, content, methodKey, 0, 1)],
                [new VariableFact(variableKey, methodKey, "local", "Local", "int", 1)],
                [new LineVariableFact(1, methodKey, "local", variableKey)],
                Array.Empty<InvocationFact>(),
                [new SymbolReferenceFact(1, methodKey, "local", "Variable", null, "Int32")]);
        }
    }

    private sealed class FakeStorageEngine : IStorageEngine
    {
        public List<string> CallLog { get; } = [];

        public IReadOnlyDictionary<string, string> IndexedHashes { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public QueryResult QueryResultToReturn { get; init; } = new QueryResult([], [], false, TimeSpan.Zero);

        public SchemaDescription SchemaToReturn { get; init; } = new SchemaDescription(5, []);

        public IReadOnlyList<FileFacts> LastPersistFacts { get; private set; } = [];

        public IReadOnlyCollection<string> LastRemovedPaths { get; private set; } = Array.Empty<string>();

        public bool LastPersistFullRebuild { get; private set; }

        public Task InitializeAsync(string databasePath, CancellationToken cancellationToken)
        {
            CallLog.Add("Initialize");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetIndexedFileHashesAsync(string databasePath, CancellationToken cancellationToken)
        {
            CallLog.Add("GetIndexedFileHashes");
            return Task.FromResult(IndexedHashes);
        }

        public Task InitializeReadOnlyAsync(string databasePath, CancellationToken cancellationToken)
        {
            CallLog.Add("InitializeReadOnly");
            return Task.CompletedTask;
        }

        public Task PersistFactsAsync(string databasePath, IReadOnlyList<FileFacts> facts, IReadOnlyCollection<string> removedPaths, bool fullRebuild, CancellationToken cancellationToken)
        {
            CallLog.Add("PersistFacts");
            LastPersistFacts = facts;
            LastRemovedPaths = removedPaths;
            LastPersistFullRebuild = fullRebuild;
            return Task.CompletedTask;
        }

        public Task<QueryResult> ExecuteReadOnlyQueryAsync(QueryOptions options, CancellationToken cancellationToken)
        {
            CallLog.Add("ExecuteReadOnlyQuery");
            return Task.FromResult(QueryResultToReturn);
        }

        public Task<SchemaDescription> DescribeSchemaAsync(string databasePath, CancellationToken cancellationToken)
        {
            CallLog.Add("DescribeSchema");
            return Task.FromResult(SchemaToReturn);
        }

        public Task<int> GetSchemaVersionAsync(string databasePath, CancellationToken cancellationToken)
        {
            CallLog.Add("GetSchemaVersion");
            return Task.FromResult(5);
        }
    }
}
