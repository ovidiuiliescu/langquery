using System.Globalization;
using LangQuery.Core.Models;
using LangQuery.Core.Services;
using LangQuery.Query.Validation;
using LangQuery.Roslyn.Extraction;
using LangQuery.Storage.Sqlite.Storage;
using Microsoft.Data.Sqlite;

namespace LangQuery.IntegrationTests;

public sealed class EndToEndTests
{
    [Fact]
    public async Task ScanQueryAndSchema_WorkOnSampleSolution()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            var sampleRoot = GetSampleSolutionRoot();
            var expectedFileCount = CountCSharpFiles(sampleRoot);

            var scan = await service.ScanAsync(new ScanOptions(sampleRoot, databasePath), CancellationToken.None);
            Assert.Equal(expectedFileCount, scan.FilesDiscovered);
            Assert.Equal(expectedFileCount, scan.FilesScanned);

            var query = await service.QueryAsync(
                new QueryOptions(databasePath, "SELECT COUNT(*) AS method_count FROM v1_methods", MaxRows: 10),
                CancellationToken.None);

            var methodCount = Convert.ToInt32(query.Rows[0]["method_count"]!, CultureInfo.InvariantCulture);
            Assert.True(methodCount >= 12);

            var schema = await service.GetSchemaAsync(new SchemaOptions(databasePath), CancellationToken.None);
            Assert.Equal(6, schema.SchemaVersion);
            Assert.Contains(schema.Entities, entity => entity.Name == "v1_methods" && entity.Kind == "view");
            Assert.Contains(schema.Entities, entity => entity.Name == "v1_type_inheritances" && entity.Kind == "view");
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ScanChangedOnly_SkipsUnchangedFilesOnSecondRun()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            var sampleRoot = GetSampleSolutionRoot();
            var expectedFileCount = CountCSharpFiles(sampleRoot);

            await service.ScanAsync(new ScanOptions(sampleRoot, databasePath, ChangedOnly: false), CancellationToken.None);
            var second = await service.ScanAsync(new ScanOptions(sampleRoot, databasePath, ChangedOnly: true), CancellationToken.None);

            Assert.Equal(expectedFileCount, second.FilesDiscovered);
            Assert.Equal(0, second.FilesScanned);
            Assert.Equal(expectedFileCount, second.FilesUnchanged);
            Assert.Equal(0, second.FilesRemoved);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ScanChangedOnly_TracksRemovedFiles()
    {
        var databasePath = CreateTempDatabasePath();
        var workspaceRoot = CreateTempWorkspacePath();

        try
        {
            var service = CreateService();
            CopyDirectory(GetSampleSolutionRoot(), workspaceRoot);

            await service.ScanAsync(new ScanOptions(workspaceRoot, databasePath, ChangedOnly: false), CancellationToken.None);

            var removedFilePath = Path.Combine(workspaceRoot, "src", "Sample.App", "Greeter.cs");
            File.Delete(removedFilePath);

            var expectedFileCount = CountCSharpFiles(workspaceRoot);
            var second = await service.ScanAsync(new ScanOptions(workspaceRoot, databasePath, ChangedOnly: true), CancellationToken.None);

            Assert.Equal(expectedFileCount, second.FilesDiscovered);
            Assert.Equal(0, second.FilesScanned);
            Assert.Equal(expectedFileCount, second.FilesUnchanged);
            Assert.Equal(1, second.FilesRemoved);

            var query = await service.QueryAsync(
                new QueryOptions(databasePath, "SELECT COUNT(*) AS file_count FROM v1_files", MaxRows: 10),
                CancellationToken.None);

            var fileCount = Convert.ToInt32(query.Rows[0]["file_count"]!, CultureInfo.InvariantCulture);
            Assert.Equal(expectedFileCount, fileCount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
            DeleteDirectory(workspaceRoot);
        }
    }

    [Fact]
    public async Task Query_LinesWithThreeVariablesAndTypedSumOfInDerivedClasses_ReturnsMatches()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            var sampleRoot = GetSampleSolutionRoot();
            await service.ScanAsync(new ScanOptions(sampleRoot, databasePath), CancellationToken.None);

            const string sql = """
                SELECT
                    l.file_path,
                    l.line_number,
                    l.text,
                    t.full_name AS class_name,
                    COUNT(DISTINCT v.name) AS variable_usage_count,
                    SUM(
                        CASE
                            WHEN v.name LIKE 'sumOf%'
                                AND LOWER(COALESCE(v.type_name, '')) IN ('int', 'int32', 'system.int32')
                            THEN 1
                            ELSE 0
                        END
                    ) AS sum_of_int_usage
                FROM v1_lines l
                JOIN v1_methods m ON m.method_id = l.method_id
                JOIN v1_types t ON t.type_id = m.type_id
                JOIN v1_type_inheritances ti ON ti.type_id = t.type_id
                JOIN v1_line_variables lv ON lv.line_id = l.line_id
                JOIN v1_variables v ON v.variable_id = lv.variable_id
                WHERE ti.base_type_name IN ('ComputationBase', 'RevenueCalculator')
                GROUP BY l.line_id, l.file_path, l.line_number, l.text, t.full_name
                HAVING COUNT(DISTINCT v.name) >= 3
                    AND SUM(
                        CASE
                            WHEN v.name LIKE 'sumOf%'
                                AND LOWER(COALESCE(v.type_name, '')) IN ('int', 'int32', 'system.int32')
                            THEN 1
                            ELSE 0
                        END
                    ) >= 1
                ORDER BY l.file_path, l.line_number;
                """;

            var result = await service.QueryAsync(new QueryOptions(databasePath, sql, MaxRows: 100), CancellationToken.None);

            Assert.NotEmpty(result.Rows);
            Assert.Contains(result.Rows, row =>
                Convert.ToString(row["class_name"], CultureInfo.InvariantCulture)?.EndsWith("RevenueCalculator", StringComparison.Ordinal) == true ||
                Convert.ToString(row["class_name"], CultureInfo.InvariantCulture)?.EndsWith("RegionalRevenueCalculator", StringComparison.Ordinal) == true ||
                Convert.ToString(row["class_name"], CultureInfo.InvariantCulture)?.EndsWith("ExpenseCalculator", StringComparison.Ordinal) == true);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Query_InheritanceGraphIncludesExpectedTypeRelationships()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            var sampleRoot = GetSampleSolutionRoot();
            await service.ScanAsync(new ScanOptions(sampleRoot, databasePath), CancellationToken.None);

            const string sql = """
                SELECT type_name, base_type_name, relation_kind
                FROM v1_type_inheritances
                ORDER BY type_name, base_type_name;
                """;

            var result = await service.QueryAsync(new QueryOptions(databasePath, sql, MaxRows: 100), CancellationToken.None);

            Assert.Contains(result.Rows, row =>
                Equals(row["type_name"], "RevenueCalculator") &&
                Equals(row["base_type_name"], "ComputationBase") &&
                Equals(row["relation_kind"], "BaseType"));

            Assert.Contains(result.Rows, row =>
                Equals(row["type_name"], "RegionalRevenueCalculator") &&
                Equals(row["base_type_name"], "RevenueCalculator") &&
                Equals(row["relation_kind"], "BaseType"));

            Assert.Contains(result.Rows, row =>
                Equals(row["type_name"], "ExpenseCalculator") &&
                Equals(row["base_type_name"], "ComputationBase") &&
                Equals(row["relation_kind"], "BaseType"));

            Assert.Contains(result.Rows, row =>
                Equals(row["type_name"], "ExpenseCalculator") &&
                Equals(row["base_type_name"], "ITraceableComputation") &&
                Equals(row["relation_kind"], "Interface"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Query_InvocationsInDerivedTypesExposeCrossMethodCalls()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            var sampleRoot = GetSampleSolutionRoot();
            await service.ScanAsync(new ScanOptions(sampleRoot, databasePath), CancellationToken.None);

            const string sql = """
                SELECT t.name AS type_name, i.target_name
                FROM v1_invocations i
                JOIN v1_methods m ON m.method_id = i.method_id
                JOIN v1_types t ON t.type_id = m.type_id
                JOIN v1_type_inheritances ti ON ti.type_id = t.type_id
                WHERE ti.base_type_name IN ('ComputationBase', 'RevenueCalculator')
                ORDER BY t.name, i.target_name;
                """;

            var result = await service.QueryAsync(new QueryOptions(databasePath, sql, MaxRows: 200), CancellationToken.None);

            Assert.NotEmpty(result.Rows);
            Assert.Contains(result.Rows, row =>
                Equals(row["type_name"], "RevenueCalculator") &&
                Equals(row["target_name"], "NormalizeToHundreds"));
            Assert.Contains(result.Rows, row =>
                Equals(row["type_name"], "RegionalRevenueCalculator") &&
                Equals(row["target_name"], "CalculateQuarterRevenue"));
            Assert.Contains(result.Rows, row =>
                Equals(row["type_name"], "ExpenseCalculator") &&
                Equals(row["target_name"], "ComputeStabilityDelta"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Query_SumOfVariableReferencesCanBeFilteredByIntegerType()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            var sampleRoot = GetSampleSolutionRoot();
            await service.ScanAsync(new ScanOptions(sampleRoot, databasePath), CancellationToken.None);

            const string sql = """
                SELECT
                    l.file_path,
                    l.line_number,
                    v.name AS variable_name,
                    v.type_name AS variable_type,
                    t.name AS class_name
                FROM v1_line_variables lv
                JOIN v1_variables v ON v.variable_id = lv.variable_id
                JOIN v1_lines l ON l.line_id = lv.line_id
                JOIN v1_methods m ON m.method_id = l.method_id
                JOIN v1_types t ON t.type_id = m.type_id
                JOIN v1_type_inheritances ti ON ti.type_id = t.type_id
                WHERE v.name LIKE 'sumOf%'
                    AND LOWER(COALESCE(v.type_name, '')) IN ('int', 'int32', 'system.int32')
                    AND ti.base_type_name IN ('ComputationBase', 'RevenueCalculator')
                ORDER BY l.file_path, l.line_number;
                """;

            var result = await service.QueryAsync(new QueryOptions(databasePath, sql, MaxRows: 200), CancellationToken.None);

            Assert.NotEmpty(result.Rows);
            Assert.All(result.Rows, row =>
            {
                var variableName = Convert.ToString(row["variable_name"], CultureInfo.InvariantCulture) ?? string.Empty;
                var variableType = Convert.ToString(row["variable_type"], CultureInfo.InvariantCulture) ?? string.Empty;

                Assert.StartsWith("sumOf", variableName, StringComparison.Ordinal);
                Assert.True(
                    string.Equals(variableType, "int", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(variableType, "int32", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(variableType, "system.int32", StringComparison.OrdinalIgnoreCase));
            });
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Query_AccessModifiersAndNestedImplementationKinds_AreExposed()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            var sampleRoot = GetSampleSolutionRoot();
            await service.ScanAsync(new ScanOptions(sampleRoot, databasePath), CancellationToken.None);

            const string typeSql = "SELECT name, access_modifier, modifiers FROM v1_types WHERE name = 'ComputationBase';";
            var typeResult = await service.QueryAsync(new QueryOptions(databasePath, typeSql, MaxRows: 10), CancellationToken.None);

            Assert.Contains(typeResult.Rows, row =>
                Equals(row["name"], "ComputationBase")
                && Equals(row["access_modifier"], "Public")
                && Convert.ToString(row["modifiers"], CultureInfo.InvariantCulture)?.Contains("Abstract", StringComparison.Ordinal) == true);

            const string methodSql = "SELECT m.name, m.modifiers, m.implementation_kind, m.access_modifier, (SELECT COUNT(*) FROM v1_variables v WHERE v.method_id = m.method_id AND v.kind = 'Parameter') AS parameter_count, m.parameters FROM v1_methods m WHERE m.name = 'CalculateQuarterRevenue';";
            var methodResult = await service.QueryAsync(new QueryOptions(databasePath, methodSql, MaxRows: 10), CancellationToken.None);

            Assert.Contains(methodResult.Rows, row =>
                Equals(row["name"], "CalculateQuarterRevenue")
                && Equals(row["implementation_kind"], "Method")
                && Equals(row["access_modifier"], "Public")
                && Convert.ToString(row["modifiers"], CultureInfo.InvariantCulture)?.Contains("Virtual", StringComparison.Ordinal) == true
                && Convert.ToInt32(row["parameter_count"], CultureInfo.InvariantCulture) == 4
                && Convert.ToString(row["parameters"], CultureInfo.InvariantCulture)?.Contains("int january", StringComparison.Ordinal) == true);

            const string nestedSql = "SELECT implementation_kind, parent_method_key, access_modifier FROM v1_methods WHERE implementation_kind IN ('LocalFunction', 'Lambda', 'AnonymousMethod') ORDER BY implementation_kind;";
            var nestedResult = await service.QueryAsync(new QueryOptions(databasePath, nestedSql, MaxRows: 100), CancellationToken.None);

            Assert.NotEmpty(nestedResult.Rows);
            Assert.Contains(nestedResult.Rows, row => Equals(row["implementation_kind"], "LocalFunction"));
            Assert.Contains(nestedResult.Rows, row => Equals(row["implementation_kind"], "Lambda"));
            Assert.Contains(nestedResult.Rows, row => Equals(row["implementation_kind"], "AnonymousMethod"));
            Assert.All(nestedResult.Rows, row =>
            {
                Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(row["parent_method_key"], CultureInfo.InvariantCulture)));
                Assert.Equal("Local", Convert.ToString(row["access_modifier"], CultureInfo.InvariantCulture));
            });

            const string symbolKindSql = "SELECT symbol_kind, COUNT(*) AS c FROM v1_symbol_refs WHERE symbol_kind IN ('Property', 'Method') GROUP BY symbol_kind ORDER BY symbol_kind;";
            var symbolKindResult = await service.QueryAsync(new QueryOptions(databasePath, symbolKindSql, MaxRows: 10), CancellationToken.None);

            Assert.Contains(symbolKindResult.Rows, row => Equals(row["symbol_kind"], "Property") && Convert.ToInt32(row["c"], CultureInfo.InvariantCulture) > 0);
            Assert.Contains(symbolKindResult.Rows, row => Equals(row["symbol_kind"], "Method") && Convert.ToInt32(row["c"], CultureInfo.InvariantCulture) > 0);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ScanUsingExplicitSolutionFile_IndexesProjectCompileItems()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            var sampleRoot = GetSampleSolutionRoot();
            var solutionPath = Path.Combine(sampleRoot, "SampleSolution.sln");
            var expectedProjectFileCount = CountCSharpFiles(Path.Combine(sampleRoot, "src", "Sample.App"));

            var scan = await service.ScanAsync(new ScanOptions(solutionPath, databasePath), CancellationToken.None);

            Assert.Equal(expectedProjectFileCount, scan.FilesDiscovered);
            Assert.Equal(expectedProjectFileCount, scan.FilesScanned);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Query_ConstructorsFromFixture_AreIndexedAsCtorRows()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            await service.ScanAsync(new ScanOptions(Path.Combine(GetSampleSolutionRoot(), "SampleSolution.sln"), databasePath), CancellationToken.None);

            var result = await service.QueryAsync(
                new QueryOptions(databasePath, "SELECT name, return_type, implementation_kind FROM v1_methods WHERE implementation_kind = 'Constructor' ORDER BY name", MaxRows: 50),
                CancellationToken.None);

            Assert.NotEmpty(result.Rows);
            Assert.All(result.Rows, row =>
            {
                Assert.Equal("Constructor", Convert.ToString(row["implementation_kind"], CultureInfo.InvariantCulture));
                Assert.Equal("ctor", Convert.ToString(row["return_type"], CultureInfo.InvariantCulture));
            });
            Assert.Contains(result.Rows, row => Equals(row["name"], "AdvancedForecastEngine"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Query_RecordAndEnumTypeKinds_AreExposedFromFixture()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            await service.ScanAsync(new ScanOptions(Path.Combine(GetSampleSolutionRoot(), "SampleSolution.sln"), databasePath), CancellationToken.None);

            var result = await service.QueryAsync(
                new QueryOptions(databasePath, "SELECT name, kind FROM v1_types WHERE name IN ('ForecastSlice', 'ForecastSensitivity') ORDER BY name", MaxRows: 10),
                CancellationToken.None);

            Assert.Contains(result.Rows, row => Equals(row["name"], "ForecastSlice") && Equals(row["kind"], "Record"));
            Assert.Contains(result.Rows, row => Equals(row["name"], "ForecastSensitivity") && Equals(row["kind"], "Enum"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Query_FileScopedTypeAccessModifier_IsIndexedAsFile()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            await service.ScanAsync(new ScanOptions(Path.Combine(GetSampleSolutionRoot(), "SampleSolution.sln"), databasePath), CancellationToken.None);

            var result = await service.QueryAsync(
                new QueryOptions(databasePath, "SELECT name, access_modifier FROM v1_types WHERE name = 'FileScopedSignal'", MaxRows: 10),
                CancellationToken.None);

            Assert.Contains(result.Rows, row => Equals(row["name"], "FileScopedSignal") && Equals(row["access_modifier"], "File"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Query_InterfaceInheritanceForFixture_IncludesBaseInterfaceRelation()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            await service.ScanAsync(new ScanOptions(Path.Combine(GetSampleSolutionRoot(), "SampleSolution.sln"), databasePath), CancellationToken.None);

            var result = await service.QueryAsync(
                new QueryOptions(databasePath, "SELECT type_name, base_type_name, relation_kind FROM v1_type_inheritances WHERE type_name = 'IAdvancedTelemetry'", MaxRows: 10),
                CancellationToken.None);

            Assert.Contains(result.Rows, row =>
                Equals(row["type_name"], "IAdvancedTelemetry")
                && Equals(row["base_type_name"], "IScenarioTelemetry")
                && Equals(row["relation_kind"], "BaseInterface"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Query_ForEachAndCatchVariables_AreExposedByKind()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            await service.ScanAsync(new ScanOptions(Path.Combine(GetSampleSolutionRoot(), "SampleSolution.sln"), databasePath), CancellationToken.None);

            var result = await service.QueryAsync(
                new QueryOptions(databasePath, "SELECT name, kind FROM v1_variables WHERE name IN ('bonus', 'ex') ORDER BY name", MaxRows: 20),
                CancellationToken.None);

            Assert.Contains(result.Rows, row => Equals(row["name"], "bonus") && Equals(row["kind"], "ForEach"));
            Assert.Contains(result.Rows, row => Equals(row["name"], "ex") && Equals(row["kind"], "Catch"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Query_EventAndFieldReferences_AreClassifiedAsPropertySymbolRefs()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            await service.ScanAsync(new ScanOptions(Path.Combine(GetSampleSolutionRoot(), "SampleSolution.sln"), databasePath), CancellationToken.None);

            var result = await service.QueryAsync(
                new QueryOptions(databasePath, "SELECT symbol_name, symbol_kind FROM v1_symbol_refs WHERE symbol_name IN ('ForecastComputed', '_history') ORDER BY symbol_name", MaxRows: 50),
                CancellationToken.None);

            Assert.Contains(result.Rows, row => Equals(row["symbol_name"], "ForecastComputed") && Equals(row["symbol_kind"], "Property"));
            Assert.Contains(result.Rows, row => Equals(row["symbol_name"], "_history") && Equals(row["symbol_kind"], "Property"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ScanChangedOnly_WhenSingleFileChanges_RescansOnlyThatFile()
    {
        var databasePath = CreateTempDatabasePath();
        var workspaceRoot = CreateTempWorkspacePath();

        try
        {
            var service = CreateService();
            CopyDirectory(GetSampleSolutionRoot(), workspaceRoot);
            var solutionPath = Path.Combine(workspaceRoot, "SampleSolution.sln");

            await service.ScanAsync(new ScanOptions(solutionPath, databasePath, ChangedOnly: false), CancellationToken.None);

            var targetFile = Path.Combine(workspaceRoot, "src", "Sample.App", "AdvancedForecastEngine.cs");
            await File.AppendAllTextAsync(targetFile, "\n// changed for scan test", CancellationToken.None);

            var second = await service.ScanAsync(new ScanOptions(solutionPath, databasePath, ChangedOnly: true), CancellationToken.None);

            Assert.Equal(1, second.FilesScanned);
            Assert.True(second.FilesUnchanged >= 1);
            Assert.Equal(0, second.FilesRemoved);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
            DeleteDirectory(workspaceRoot);
        }
    }

    [Fact]
    public async Task ScanChangedOnly_WhenNewFileIsAdded_ScansOnlyNewFile()
    {
        var databasePath = CreateTempDatabasePath();
        var workspaceRoot = CreateTempWorkspacePath();

        try
        {
            var service = CreateService();
            CopyDirectory(GetSampleSolutionRoot(), workspaceRoot);
            var solutionPath = Path.Combine(workspaceRoot, "SampleSolution.sln");

            await service.ScanAsync(new ScanOptions(solutionPath, databasePath, ChangedOnly: false), CancellationToken.None);

            var newFile = Path.Combine(workspaceRoot, "src", "Sample.App", "TransientFeature.cs");
            await File.WriteAllTextAsync(newFile, "namespace Sample.App; public sealed class TransientFeature { public int Run() => 42; }", CancellationToken.None);

            var second = await service.ScanAsync(new ScanOptions(solutionPath, databasePath, ChangedOnly: true), CancellationToken.None);

            Assert.Equal(1, second.FilesScanned);
            Assert.Equal(0, second.FilesRemoved);

            var query = await service.QueryAsync(new QueryOptions(databasePath, "SELECT COUNT(*) AS c FROM v1_types WHERE name = 'TransientFeature'", MaxRows: 10), CancellationToken.None);
            Assert.Equal(1, Convert.ToInt32(query.Rows[0]["c"], CultureInfo.InvariantCulture));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
            DeleteDirectory(workspaceRoot);
        }
    }

    [Fact]
    public async Task Query_MutationStatementsAreRejectedAtServiceBoundary()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var service = CreateService();
            await service.ScanAsync(new ScanOptions(Path.Combine(GetSampleSolutionRoot(), "SampleSolution.sln"), databasePath), CancellationToken.None);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.QueryAsync(new QueryOptions(databasePath, "DELETE FROM v1_files"), CancellationToken.None));

            Assert.Contains("read-only", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static LangQueryService CreateService()
    {
        return new LangQueryService(
            new CSharpCodeFactsExtractor(),
            new SqliteStorageEngine(new ReadOnlySqlSafetyValidator()));
    }

    private static string GetSampleSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "sample_solution");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate tests/sample_solution from test run directory.");
    }

    private static int CountCSharpFiles(string root)
    {
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Count(path => !IsUnderIgnoredDirectory(path));
    }

    private static bool IsUnderIgnoredDirectory(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, ".vs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, ".idea", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, ".vscode", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateTempDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "langquery-integration", Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "facts.db");
    }

    private static string CreateTempWorkspacePath()
    {
        return Path.Combine(Path.GetTempPath(), "langquery-workspace", Guid.NewGuid().ToString("N"));
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

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var sourceFile in Directory.EnumerateFiles(sourcePath))
        {
            var fileName = Path.GetFileName(sourceFile);
            File.Copy(sourceFile, Path.Combine(destinationPath, fileName), overwrite: true);
        }

        foreach (var sourceDirectory in Directory.EnumerateDirectories(sourcePath))
        {
            var directoryName = Path.GetFileName(sourceDirectory);
            CopyDirectory(sourceDirectory, Path.Combine(destinationPath, directoryName));
        }
    }
}
