namespace LangQuery.Core.Models;

public sealed record ScanOptions(string SolutionPath, string DatabasePath, bool ChangedOnly = false);

public sealed record QueryOptions(string DatabasePath, string Sql, int MaxRows = 1000, int TimeoutMs = 10_000);

public sealed record SchemaOptions(string DatabasePath);
