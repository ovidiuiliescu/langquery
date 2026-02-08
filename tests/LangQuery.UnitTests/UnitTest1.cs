using LangQuery.Core.Models;
using LangQuery.Query.Validation;
using LangQuery.Storage.Sqlite.Storage;
using Microsoft.Data.Sqlite;

namespace LangQuery.UnitTests;

public sealed class SqliteStorageEngineTests
{
    [Fact]
    public async Task InitializeAsync_CreatesSchemaAndPublicViews()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();

            await storage.InitializeAsync(databasePath, CancellationToken.None);
            var schemaVersion = await storage.GetSchemaVersionAsync(databasePath, CancellationToken.None);
            var schema = await storage.DescribeSchemaAsync(databasePath, CancellationToken.None);

            Assert.Equal(5, schemaVersion);
            Assert.Contains(schema.Entities, entity => entity.Name == "meta_schema_version" && entity.Kind == "table");
            Assert.Contains(schema.Entities, entity => entity.Name == "v1_files" && entity.Kind == "view");
            Assert.Contains(schema.Entities, entity => entity.Name == "v1_methods" && entity.Kind == "view");
            Assert.Contains(schema.Entities, entity => entity.Name == "v1_type_inheritances" && entity.Kind == "view");

            var typeView = Assert.Single(schema.Entities, entity => entity.Name == "v1_types");
            Assert.Contains(typeView.Columns, column => string.Equals(column.Name, "access_modifier", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(typeView.Columns, column => string.Equals(column.Name, "modifiers", StringComparison.OrdinalIgnoreCase));

            var methodsView = Assert.Single(schema.Entities, entity => entity.Name == "v1_methods");
            Assert.Contains(methodsView.Columns, column => string.Equals(column.Name, "access_modifier", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(methodsView.Columns, column => string.Equals(column.Name, "modifiers", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(methodsView.Columns, column => string.Equals(column.Name, "parameters", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(methodsView.Columns, column => string.Equals(column.Name, "parameter_count", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(methodsView.Columns, column => string.Equals(column.Name, "implementation_kind", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(methodsView.Columns, column => string.Equals(column.Name, "parent_method_key", StringComparison.OrdinalIgnoreCase));

            var symbolRefsView = Assert.Single(schema.Entities, entity => entity.Name == "v1_symbol_refs");
            Assert.Contains(symbolRefsView.Columns, column => string.Equals(column.Name, "symbol_container_type_name", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(symbolRefsView.Columns, column => string.Equals(column.Name, "symbol_type_name", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteReadOnlyQueryAsync_AllowsSelectAndRejectsMutation()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var readResult = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "SELECT key, value FROM meta_capabilities ORDER BY key", MaxRows: 10),
                CancellationToken.None);

            Assert.Contains("key", readResult.Columns, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("value", readResult.Columns, StringComparer.OrdinalIgnoreCase);
            Assert.NotEmpty(readResult.Rows);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                storage.ExecuteReadOnlyQueryAsync(new QueryOptions(databasePath, "DELETE FROM meta_capabilities"), CancellationToken.None));

            Assert.Contains("read-only", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteReadOnlyQueryAsync_RespectsMaxRowsAndSetsTruncatedFlag()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var storage = CreateStorage();
            await storage.InitializeAsync(databasePath, CancellationToken.None);

            var result = await storage.ExecuteReadOnlyQueryAsync(
                new QueryOptions(databasePath, "WITH RECURSIVE nums(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM nums WHERE value < 3) SELECT value FROM nums", MaxRows: 1),
                CancellationToken.None);

            Assert.True(result.Truncated);
            Assert.Single(result.Rows);
            Assert.Contains("value", result.Columns, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static SqliteStorageEngine CreateStorage()
    {
        return new SqliteStorageEngine(new ReadOnlySqlSafetyValidator());
    }

    private static string CreateTempDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "langquery-tests", Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "facts.db");
    }

    private static void DeleteTempDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
