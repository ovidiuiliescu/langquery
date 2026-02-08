using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using LangQuery.Core.Abstractions;
using LangQuery.Core.Models;

namespace LangQuery.Core.Services;

public sealed class LangQueryService(ICodeFactsExtractor extractor, IStorageEngine storageEngine)
{
    private static readonly Regex SolutionProjectRegex = new(
        """^Project\("\{[^\"]+\}"\)\s*=\s*"[^"]+",\s*"(?<path>[^"]+)",\s*"\{[^\"]+\}"$""",
        RegexOptions.Compiled);

    private static readonly HashSet<string> IgnoredDirectories =
    [
        ".git",
        "bin",
        "obj",
        ".vs",
        ".idea",
        ".vscode"
    ];

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

    private static async Task<string[]> DiscoverSolutionSourceFilesAsync(string solutionPath, CancellationToken cancellationToken)
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
                throw new FileNotFoundException($"Project '{projectPath}' referenced by solution '{solutionPath}' does not exist.", projectPath);
            }

            var projectItems = await EvaluateProjectItemsAsync(projectPath, cancellationToken).ConfigureAwait(false);

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

        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
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
            yield return Path.GetFullPath(Path.Combine(solutionDirectory, normalizedRelativePath));
        }
    }

    private static async Task<ProjectItems> EvaluateProjectItemsAsync(string projectPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-verbosity:quiet");
        startInfo.ArgumentList.Add("-getItem:Compile");
        startInfo.ArgumentList.Add("-getItem:ProjectReference");

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var errorText = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"Failed to evaluate project '{projectPath}'. {errorText.Trim()}");
        }

        try
        {
            using var document = JsonDocument.Parse(stdout);
            if (!document.RootElement.TryGetProperty("Items", out var itemsElement) ||
                itemsElement.ValueKind != JsonValueKind.Object)
            {
                return new ProjectItems(Array.Empty<string>(), Array.Empty<string>());
            }

            var compileFiles = ExtractItemPaths(itemsElement, "Compile", projectPath, ".cs");
            var projectReferences = ExtractItemPaths(itemsElement, "ProjectReference", projectPath, ".csproj");
            return new ProjectItems(compileFiles, projectReferences);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Unexpected MSBuild output while evaluating '{projectPath}'.", ex);
        }
    }

    private static string[] ExtractItemPaths(JsonElement itemsElement, string itemName, string projectPath, string extension)
    {
        if (!itemsElement.TryGetProperty(itemName, out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var projectDirectory = Path.GetDirectoryName(projectPath)
                               ?? throw new DirectoryNotFoundException($"Could not derive project folder from '{projectPath}'.");

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var itemPath = ResolveItemPath(entry, projectDirectory);
            if (string.IsNullOrWhiteSpace(itemPath) ||
                !string.Equals(Path.GetExtension(itemPath), extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(itemPath);
        }

        return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? ResolveItemPath(JsonElement itemElement, string projectDirectory)
    {
        if (!itemElement.TryGetProperty("FullPath", out var fullPathElement) ||
            fullPathElement.ValueKind != JsonValueKind.String)
        {
            if (!itemElement.TryGetProperty("Identity", out var identityElement) ||
                identityElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var identityValue = identityElement.GetString();
            if (string.IsNullOrWhiteSpace(identityValue))
            {
                return null;
            }

            return Path.GetFullPath(Path.Combine(projectDirectory, NormalizePathSeparators(identityValue)));
        }

        var fullPathValue = fullPathElement.GetString();
        if (string.IsNullOrWhiteSpace(fullPathValue))
        {
            return null;
        }

        return Path.GetFullPath(fullPathValue);
    }

    private static string NormalizePathSeparators(string path)
    {
        return path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    }

    private static void TryKill(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
    }

    private static bool IsUnderIgnoredDirectory(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
