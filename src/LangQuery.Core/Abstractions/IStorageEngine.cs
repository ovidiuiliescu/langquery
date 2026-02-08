using LangQuery.Core.Models;

namespace LangQuery.Core.Abstractions;

public interface IStorageEngine
{
    Task InitializeAsync(string databasePath, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> GetIndexedFileHashesAsync(string databasePath, CancellationToken cancellationToken);

    Task PersistFactsAsync(
        string databasePath,
        IReadOnlyList<FileFacts> facts,
        IReadOnlyCollection<string> removedPaths,
        bool fullRebuild,
        CancellationToken cancellationToken);

    Task<QueryResult> ExecuteReadOnlyQueryAsync(QueryOptions options, CancellationToken cancellationToken);

    Task<SchemaDescription> DescribeSchemaAsync(string databasePath, CancellationToken cancellationToken);

    Task<int> GetSchemaVersionAsync(string databasePath, CancellationToken cancellationToken);
}
