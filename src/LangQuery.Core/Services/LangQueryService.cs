using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using LangQuery.Core.Abstractions;
using LangQuery.Core.Models;

namespace LangQuery.Core.Services;

public sealed class LangQueryService(ICodeFactsExtractor extractor, IStorageEngine storageEngine)
{
    private static readonly Regex SolutionProjectRegex = new(
        """^Project\("\{[^\"]+\}"\)\s*=\s*"[^"]+",\s*"(?<path>[^"]+)",\s*"\{[^\"]+\}"$""",
        RegexOptions.Compiled);

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "bin",
        "obj",
        ".vs",
        ".idea",
        ".vscode"
    };

    public async Task<ScanSummary> ScanAsync(ScanOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SolutionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);

        await storageEngine.InitializeAsync(options.DatabasePath, cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        var files = await DiscoverSourceFilesAsync(options.SolutionPath, cancellationToken).ConfigureAwait(false);
        var fingerprints = files
            .Select(path => new SourceFileFingerprint(path, ComputeSha256(path), "csharp"))
            .ToArray();

        var existing = await storageEngine.GetIndexedFileHashesAsync(options.DatabasePath, cancellationToken).ConfigureAwait(false);

        IReadOnlyCollection<string> removed;
        IReadOnlyCollection<SourceFileFingerprint> toScan;
        int unchangedCount;

        if (options.ChangedOnly)
        {
            var incomingLookup = fingerprints.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            removed = existing.Keys
                .Where(existingPath => !incomingLookup.ContainsKey(existingPath))
                .ToArray();

            var changed = new List<SourceFileFingerprint>(fingerprints.Length);
            var unchanged = 0;
            foreach (var fingerprint in fingerprints)
            {
                if (existing.TryGetValue(fingerprint.Path, out var hash) &&
                    string.Equals(hash, fingerprint.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    unchanged++;
                    continue;
                }

                changed.Add(fingerprint);
            }

            toScan = changed;
            unchangedCount = unchanged;
        }
        else
        {
            removed = Array.Empty<string>();
            toScan = fingerprints;
            unchangedCount = 0;
        }

        var facts = new List<FileFacts>(toScan.Count);
        foreach (var fingerprint in toScan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(fingerprint.Path, cancellationToken).ConfigureAwait(false);
            if (!extractor.CanHandle(fingerprint.Path))
            {
                continue;
            }

            facts.Add(extractor.Extract(fingerprint.Path, content, fingerprint.Hash));
        }

        await storageEngine.PersistFactsAsync(
            options.DatabasePath,
            facts,
            removed,
            fullRebuild: !options.ChangedOnly,
            cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();
        var indexedEntities = facts.Sum(GetEntityCount);

        return new ScanSummary(
            files.Length,
            facts.Count,
            unchangedCount,
            removed.Count,
            stopwatch.Elapsed,
            indexedEntities,
            options.DatabasePath);
    }

    public async Task<QueryResult> QueryAsync(QueryOptions options, CancellationToken cancellationToken = default)
    {
        await storageEngine.InitializeAsync(options.DatabasePath, cancellationToken).ConfigureAwait(false);
        return await storageEngine.ExecuteReadOnlyQueryAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SchemaDescription> GetSchemaAsync(SchemaOptions options, CancellationToken cancellationToken = default)
    {
        await storageEngine.InitializeAsync(options.DatabasePath, cancellationToken).ConfigureAwait(false);
        return await storageEngine.DescribeSchemaAsync(options.DatabasePath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string[]> DiscoverSourceFilesAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);

        if (File.Exists(fullPath))
        {
            if (string.Equals(Path.GetExtension(fullPath), ".sln", StringComparison.OrdinalIgnoreCase))
            {
                return await DiscoverSolutionSourceFilesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }

            var fileDirectory = Path.GetDirectoryName(fullPath)
                                ?? throw new DirectoryNotFoundException($"Could not derive root folder from '{path}'.");

            return DiscoverDirectorySourceFiles(fileDirectory).ToArray();
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Scan root '{fullPath}' does not exist.");
        }

        return DiscoverDirectorySourceFiles(fullPath).ToArray();
    }

    private static Task<string[]> DiscoverSolutionSourceFilesAsync(string solutionPath, CancellationToken cancellationToken)
    {
        var solutionDirectory = Path.GetDirectoryName(solutionPath)
                                ?? throw new DirectoryNotFoundException($"Could not derive root folder from '{solutionPath}'.");

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingProjects = new Queue<string>(EnumerateSolutionProjects(solutionPath, solutionDirectory));

        while (pendingProjects.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var projectPath = pendingProjects.Dequeue();
            if (!visitedProjects.Add(projectPath))
            {
                continue;
            }

            if (!File.Exists(projectPath))
            {
                continue;
            }

            var projectItems = EvaluateProjectItems(projectPath, solutionDirectory);

            foreach (var compilePath in projectItems.CompileFiles)
            {
                if (File.Exists(compilePath))
                {
                    files.Add(compilePath);
                }
            }

            foreach (var projectReference in projectItems.ProjectReferences)
            {
                if (!visitedProjects.Contains(projectReference))
                {
                    pendingProjects.Enqueue(projectReference);
                }
            }
        }

        return Task.FromResult(files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IEnumerable<string> DiscoverDirectorySourceFiles(string rootPath)
    {
        return Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsUnderIgnoredDirectory(path));
    }

    private static IEnumerable<string> EnumerateSolutionProjects(string solutionPath, string solutionDirectory)
    {
        foreach (var line in File.ReadLines(solutionPath))
        {
            var match = SolutionProjectRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var relativeProjectPath = match.Groups["path"].Value;
            if (!string.Equals(Path.GetExtension(relativeProjectPath), ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalizedRelativePath = NormalizePathSeparators(relativeProjectPath);
            var fullProjectPath = Path.GetFullPath(Path.Combine(solutionDirectory, normalizedRelativePath));
            EnsurePathWithinSolutionRoot(fullProjectPath, solutionDirectory, $"Project '{relativeProjectPath}' from solution '{solutionPath}'");
            yield return fullProjectPath;
        }
    }

    private static ProjectItems EvaluateProjectItems(string projectPath, string solutionDirectory)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)
                               ?? throw new DirectoryNotFoundException($"Could not derive project folder from '{projectPath}'.");

        var compileFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var useDefaultCompileItems = true;
        var document = LoadProjectDocument(projectPath);
        foreach (var property in document.Descendants().Where(static x => string.Equals(x.Name.LocalName, "EnableDefaultCompileItems", StringComparison.Ordinal)))
        {
            if (bool.TryParse(property.Value, out var parsedValue))
            {
                useDefaultCompileItems = parsedValue;
            }
        }

        if (useDefaultCompileItems)
        {
            foreach (var filePath in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
            {
                if (!IsUnderIgnoredDirectory(filePath))
                {
                    compileFiles.Add(Path.GetFullPath(filePath));
                }
            }
        }

        var explicitCompileIncludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitCompileRemoves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in document.Descendants().Where(static x => string.Equals(x.Name.LocalName, "Compile", StringComparison.Ordinal)))
        {
            AddMatchesFromAttribute(item, "Include", projectDirectory, ".cs", explicitCompileIncludes);
            AddMatchesFromAttribute(item, "Remove", projectDirectory, ".cs", explicitCompileRemoves);
        }

        compileFiles.UnionWith(explicitCompileIncludes);
        compileFiles.ExceptWith(explicitCompileRemoves);

        foreach (var item in document.Descendants().Where(static x => string.Equals(x.Name.LocalName, "ProjectReference", StringComparison.Ordinal)))
        {
            AddProjectReferenceFromAttribute(item, projectDirectory, solutionDirectory, projectReferences, projectPath);
        }

        return new ProjectItems(
            compileFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            projectReferences.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static XDocument LoadProjectDocument(string projectPath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };

        using var stream = File.OpenRead(projectPath);
        using var reader = XmlReader.Create(stream, settings, projectPath);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static void AddMatchesFromAttribute(
        XElement itemElement,
        string attributeName,
        string projectDirectory,
        string extension,
        ISet<string> result)
    {
        var attribute = itemElement.Attributes().FirstOrDefault(x => string.Equals(x.Name.LocalName, attributeName, StringComparison.Ordinal));
        if (attribute is null || string.IsNullOrWhiteSpace(attribute.Value))
        {
            return;
        }

        foreach (var rawPattern in attribute.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var filePath in ExpandProjectPattern(rawPattern, projectDirectory, extension))
            {
                if (!IsUnderIgnoredDirectory(filePath))
                {
                    result.Add(filePath);
                }
            }
        }
    }

    private static IReadOnlyList<string> ExpandProjectPattern(string pattern, string projectDirectory, string extension)
    {
        var normalizedPattern = NormalizePathSeparators(pattern);
        if (!normalizedPattern.Contains('*') && !normalizedPattern.Contains('?'))
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectDirectory, normalizedPattern));
            if (string.Equals(Path.GetExtension(fullPath), extension, StringComparison.OrdinalIgnoreCase))
            {
                return [fullPath];
            }

            return Array.Empty<string>();
        }

        if (normalizedPattern.StartsWith("**", StringComparison.Ordinal))
        {
            var suffix = normalizedPattern.TrimStart('*', Path.DirectorySeparatorChar);
            var filePattern = Path.GetFileName(suffix);
            if (string.IsNullOrWhiteSpace(filePattern))
            {
                filePattern = "*.cs";
            }

            return Directory
                .EnumerateFiles(projectDirectory, filePattern, SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Where(path => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var searchDirectoryPattern = Path.GetDirectoryName(normalizedPattern);
        var searchDirectory = string.IsNullOrWhiteSpace(searchDirectoryPattern)
            ? projectDirectory
            : Path.GetFullPath(Path.Combine(projectDirectory, searchDirectoryPattern));
        var filePatternInDirectory = Path.GetFileName(normalizedPattern);

        if (string.IsNullOrWhiteSpace(filePatternInDirectory) || !Directory.Exists(searchDirectory))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateFiles(searchDirectory, filePatternInDirectory, SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .Where(path => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static void AddProjectReferenceFromAttribute(
        XElement itemElement,
        string projectDirectory,
        string solutionDirectory,
        ISet<string> projectReferences,
        string sourceProjectPath)
    {
        var includeAttribute = itemElement.Attributes().FirstOrDefault(static x => string.Equals(x.Name.LocalName, "Include", StringComparison.Ordinal));
        if (includeAttribute is null || string.IsNullOrWhiteSpace(includeAttribute.Value))
        {
            return;
        }

        foreach (var rawPath in includeAttribute.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalizedRelativePath = NormalizePathSeparators(rawPath);
            var fullProjectPath = Path.GetFullPath(Path.Combine(projectDirectory, normalizedRelativePath));
            if (!string.Equals(Path.GetExtension(fullProjectPath), ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            EnsurePathWithinSolutionRoot(fullProjectPath, solutionDirectory, $"Project reference '{rawPath}' in '{sourceProjectPath}'");
            projectReferences.Add(fullProjectPath);
        }
    }

    private static void EnsurePathWithinSolutionRoot(string path, string solutionDirectory, string context)
    {
        if (IsPathWithinDirectory(path, solutionDirectory))
        {
            return;
        }

        throw new InvalidOperationException($"{context} resolves outside the solution root '{solutionDirectory}'.");
    }

    private static bool IsPathWithinDirectory(string path, string rootDirectory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(rootDirectory);

        if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathSeparators(string path)
    {
        return path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool IsUnderIgnoredDirectory(string path)
    {
        var segments = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => IgnoredDirectories.Contains(segment));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static int GetEntityCount(FileFacts fileFacts)
    {
        return fileFacts.Types.Count
             + fileFacts.Methods.Count
             + fileFacts.Lines.Count
             + fileFacts.Variables.Count
             + fileFacts.LineVariables.Count
             + fileFacts.Invocations.Count
             + fileFacts.SymbolReferences.Count;
    }

    private sealed record ProjectItems(IReadOnlyList<string> CompileFiles, IReadOnlyList<string> ProjectReferences);
}
