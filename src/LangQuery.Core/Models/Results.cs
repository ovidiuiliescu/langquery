namespace LangQuery.Core.Models;

public sealed record ScanSummary(
    int FilesDiscovered,
    int FilesScanned,
    int FilesUnchanged,
    int FilesRemoved,
    TimeSpan Duration,
    long IndexedEntities,
    string DatabasePath);

public sealed record QueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool Truncated,
    TimeSpan Duration);

public sealed record SchemaDescription(
    int SchemaVersion,
    IReadOnlyList<SchemaEntity> Entities);

public sealed record SchemaEntity(
    string Name,
    string Kind,
    string Sql,
    IReadOnlyList<SchemaColumn> Columns);

public sealed record SchemaColumn(
    string Name,
    string Type,
    bool NotNull,
    bool PrimaryKey);

public sealed record SqlValidationResult(bool IsValid, string? ErrorMessage)
{
    public static SqlValidationResult Valid() => new(true, null);

    public static SqlValidationResult Invalid(string errorMessage) => new(false, errorMessage);
}
