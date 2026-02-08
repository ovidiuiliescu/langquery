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
        Assert.Equal("Initialize", storage.CallLog[0]);
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
        Assert.Equal("Initialize", storage.CallLog[0]);
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
