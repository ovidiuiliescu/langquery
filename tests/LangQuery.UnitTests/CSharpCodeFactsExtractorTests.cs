using LangQuery.Roslyn.Extraction;

namespace LangQuery.UnitTests;

public sealed class CSharpCodeFactsExtractorTests
{
    [Fact]
    public void CanHandle_ReturnsTrueForCSharpFiles()
    {
        var extractor = new CSharpCodeFactsExtractor();

        Assert.True(extractor.CanHandle("Feature.cs"));
        Assert.False(extractor.CanHandle("Feature.txt"));
    }

    [Fact]
    public void Extract_ProducesMethodVariableInvocationAndLineFacts()
    {
        var source = """
            namespace Sample;

            public sealed class Calculator
            {
                public int Add(int left, int right)
                {
                    var sum = left + right;
                    if (sum > 0)
                    {
                        System.Console.WriteLine(sum);
                    }

                    return sum;
                }
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("Calculator.cs", source, "HASH");

        Assert.Contains(facts.Types, type =>
            type.Name == "Calculator"
            && type.Kind == "Class"
            && type.AccessModifier == "Public"
            && type.Modifiers.Contains("Sealed", StringComparison.Ordinal));

        var method = Assert.Single(facts.Methods, method => method.Name == "Add");
        Assert.Equal("int", method.ReturnType);
        Assert.Equal(2, method.ParameterCount);
        Assert.Equal("int left, int right", method.Parameters);
        Assert.Equal("Method", method.ImplementationKind);
        Assert.Equal("Public", method.AccessModifier);

        Assert.Contains(facts.Variables, variable => variable.Name == "left" && variable.Kind == "Parameter");
        Assert.Contains(facts.Variables, variable => variable.Name == "right" && variable.Kind == "Parameter");
        Assert.Contains(facts.Variables, variable => variable.Name == "sum" && variable.Kind == "Local" && variable.TypeName == "var");

        Assert.Contains(facts.Invocations, invocation => invocation.LineNumber == 10 && invocation.TargetName == "WriteLine");
        Assert.Contains(facts.LineVariables, usage => usage.LineNumber == 10 && usage.VariableName == "sum");
        Assert.Contains(facts.SymbolReferences, reference => reference.SymbolName == "WriteLine" && reference.SymbolKind == "Method");

        var invocationLine = Assert.Single(facts.Lines, line => line.LineNumber == 10);
        Assert.Equal(1, invocationLine.BlockDepthInMethod);
        Assert.Equal(1, invocationLine.VariableCount);

        var returnLine = Assert.Single(facts.Lines, line => line.LineNumber == 13);
        Assert.Equal(0, returnLine.BlockDepthInMethod);
        Assert.Equal(1, returnLine.VariableCount);
    }

    [Fact]
    public void Extract_ClassifiesMemberAccessAsPropertyAndMethod()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Data.Sqlite;

            namespace Sample;

            public sealed class QueryBuilder
            {
                public async Task<int> BuildAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken cancellationToken)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = tx;
                    command.CommandText = "SELECT id FROM files WHERE path = $path;";
                    command.Parameters.AddWithValue("$path", "value");
                    var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    return Convert.ToInt32(result);
                }
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("QueryBuilder.cs", source, "HASH");

        Assert.Contains(facts.SymbolReferences, reference =>
            reference.SymbolName == "Parameters"
            && reference.SymbolKind == "Property"
            && reference.SymbolContainerTypeName == "SqliteCommand"
            && reference.SymbolTypeName == "SqliteParameterCollection");
        Assert.Contains(facts.SymbolReferences, reference => reference.SymbolName == "CommandText" && reference.SymbolKind == "Property");
        Assert.Contains(facts.SymbolReferences, reference => reference.SymbolName == "AddWithValue" && reference.SymbolKind == "Method");
        Assert.Contains(facts.SymbolReferences, reference => reference.SymbolName == "ExecuteScalarAsync" && reference.SymbolKind == "Method");
    }

    [Fact]
    public void Extract_TracksNestedImplementationsWithInnermostScopeOwnership()
    {
        var source = """
            namespace Sample;

            public abstract class Pipeline
            {
                protected virtual int Run(int seed)
                {
                    int LocalAdjust(int value)
                    {
                        return value + seed;
                    }

                    Func<int, int> boost = value =>
                    {
                        System.Console.WriteLine(value);
                        return LocalAdjust(value);
                    };

                    Predicate<int> isPositive = delegate (int candidate)
                    {
                        return candidate > 0;
                    };

                    var result = boost(seed);
                    return isPositive(result) ? result : seed;
                }
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("Pipeline.cs", source, "HASH");

        var type = Assert.Single(facts.Types, item => item.Name == "Pipeline");
        Assert.Equal("Public", type.AccessModifier);
        Assert.Equal("Abstract", type.Modifiers);

        var rootMethod = Assert.Single(facts.Methods, method => method.Name == "Run" && method.ImplementationKind == "Method");
        Assert.Equal("Protected", rootMethod.AccessModifier);
        Assert.Contains("Virtual", rootMethod.Modifiers, StringComparison.Ordinal);
        Assert.Null(rootMethod.ParentMethodKey);

        var localFunction = Assert.Single(facts.Methods, method => method.Name == "LocalAdjust" && method.ImplementationKind == "LocalFunction");
        Assert.Equal(rootMethod.MethodKey, localFunction.ParentMethodKey);
        Assert.Equal("Local", localFunction.AccessModifier);
        Assert.Equal("int value", localFunction.Parameters);

        var lambda = Assert.Single(facts.Methods, method => method.ImplementationKind == "Lambda");
        Assert.Equal(rootMethod.MethodKey, lambda.ParentMethodKey);
        Assert.Equal("Local", lambda.AccessModifier);

        var anonymous = Assert.Single(facts.Methods, method => method.ImplementationKind == "AnonymousMethod");
        Assert.Equal(rootMethod.MethodKey, anonymous.ParentMethodKey);
        Assert.Equal("Local", anonymous.AccessModifier);

        Assert.Contains(facts.Variables, variable => variable.MethodKey == lambda.MethodKey && variable.Name == "value" && variable.Kind == "Parameter");
        Assert.Contains(facts.Variables, variable => variable.MethodKey == anonymous.MethodKey && variable.Name == "candidate" && variable.Kind == "Parameter");
        Assert.DoesNotContain(facts.Variables, variable => variable.MethodKey == rootMethod.MethodKey && variable.Name == "candidate");

        Assert.Contains(facts.Invocations, invocation => invocation.TargetName == "WriteLine" && invocation.MethodKey == lambda.MethodKey);

        var anonymousReturnLine = Assert.Single(facts.Lines, line => line.Text.Contains("return candidate > 0;", StringComparison.Ordinal));
        Assert.Equal(anonymous.MethodKey, anonymousReturnLine.MethodKey);
    }

    [Fact]
    public void Extract_ProducesInheritanceFacts()
    {
        var source = """
            namespace Sample;

            public abstract class BaseComputation
            {
            }

            public interface ITraceable
            {
            }

            public sealed class RevenueCalculator : BaseComputation, ITraceable
            {
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("RevenueCalculator.cs", source, "HASH");

        var revenueType = Assert.Single(facts.Types, type => type.Name == "RevenueCalculator");

        Assert.Contains(
            facts.TypeInheritances,
            inheritance =>
                inheritance.TypeKey == revenueType.TypeKey &&
                inheritance.BaseTypeName == "BaseComputation" &&
                inheritance.RelationKind == "BaseType");

        Assert.Contains(
            facts.TypeInheritances,
            inheritance =>
                inheritance.TypeKey == revenueType.TypeKey &&
                inheritance.BaseTypeName == "ITraceable" &&
                inheritance.RelationKind == "Interface");
    }
}
