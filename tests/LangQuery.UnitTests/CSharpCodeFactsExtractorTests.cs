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

    [Fact]
    public void Extract_ProducesConstructorFactsWithCtorReturnType()
    {
        var source = """
            namespace Sample;

            public sealed class Widget
            {
                public Widget(int capacity)
                {
                }
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("Widget.cs", source, "HASH");

        var constructor = Assert.Single(facts.Methods, method => method.ImplementationKind == "Constructor");
        Assert.Equal("Widget", constructor.Name);
        Assert.Equal("ctor", constructor.ReturnType);
        Assert.Equal("Public", constructor.AccessModifier);
        Assert.Equal(1, constructor.ParameterCount);
        Assert.Equal("int capacity", constructor.Parameters);
    }

    [Fact]
    public void Extract_InterfaceMethodDefaultsToPublicAccessModifier()
    {
        var source = """
            namespace Sample;

            public interface IRunner
            {
                void Run();
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("IRunner.cs", source, "HASH");

        var method = Assert.Single(facts.Methods, item => item.Name == "Run");
        Assert.Equal("Method", method.ImplementationKind);
        Assert.Equal("Public", method.AccessModifier);
    }

    [Fact]
    public void Extract_DefaultTypeAccessModifierIsInternalAndNestedTypeDefaultsToPrivate()
    {
        var source = """
            namespace Sample;

            class Outer
            {
                class Inner
                {
                }
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("Outer.cs", source, "HASH");

        var outer = Assert.Single(facts.Types, type => type.Name == "Outer");
        var inner = Assert.Single(facts.Types, type => type.Name == "Inner");

        Assert.Equal("Internal", outer.AccessModifier);
        Assert.Equal("Private", inner.AccessModifier);
    }

    [Fact]
    public void Extract_RecognizesFileScopedTypeRecordAndEnumKinds()
    {
        var source = """
            namespace Sample;

            file sealed class HiddenSignal
            {
            }

            public readonly record struct Snapshot(int Value);

            public enum ForecastState
            {
                Idle,
                Busy
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("Shapes.cs", source, "HASH");

        Assert.Contains(facts.Types, type => type.Name == "HiddenSignal" && type.AccessModifier == "File");
        Assert.Contains(facts.Types, type => type.Name == "Snapshot" && type.Kind == "Record" && type.Modifiers.Contains("ReadOnly", StringComparison.Ordinal));
        Assert.Contains(facts.Types, type => type.Name == "ForecastState" && type.Kind == "Enum");
    }

    [Fact]
    public void Extract_TracksForEachAndCatchVariableKinds()
    {
        var source = """
            using System;
            using System.Collections.Generic;

            namespace Sample;

            public sealed class Processor
            {
                public int Run(IEnumerable<int> values)
                {
                    var total = 0;
                    foreach (var value in values)
                    {
                        total += value;
                    }

                    try
                    {
                        throw new InvalidOperationException();
                    }
                    catch (InvalidOperationException ex)
                    {
                        total += ex.Message.Length;
                    }

                    return total;
                }
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("Processor.cs", source, "HASH");

        Assert.Contains(facts.Variables, variable => variable.Name == "value" && variable.Kind == "ForEach");
        Assert.Contains(facts.Variables, variable => variable.Name == "ex" && variable.Kind == "Catch" && variable.TypeName == "InvalidOperationException");
    }

    [Fact]
    public void Extract_UsesBaseInterfaceRelationForInterfaceInheritance()
    {
        var source = """
            namespace Sample;

            public interface IRoot
            {
            }

            public interface IChild : IRoot
            {
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("Interfaces.cs", source, "HASH");

        var child = Assert.Single(facts.Types, type => type.Name == "IChild");
        Assert.Contains(facts.TypeInheritances, edge => edge.TypeKey == child.TypeKey && edge.BaseTypeName == "IRoot" && edge.RelationKind == "BaseInterface");
    }

    [Fact]
    public void Extract_UsesInterfaceRelationForStructImplementedInterfaces()
    {
        var source = """
            namespace Sample;

            public interface ITag
            {
            }

            public struct Tagged : ITag
            {
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("Tagged.cs", source, "HASH");

        var tagged = Assert.Single(facts.Types, type => type.Name == "Tagged");
        Assert.Contains(facts.TypeInheritances, edge => edge.TypeKey == tagged.TypeKey && edge.BaseTypeName == "ITag" && edge.RelationKind == "Interface");
    }

    [Fact]
    public void Extract_ResolvesProtectedInternalAndPrivateProtectedAccessModifiers()
    {
        var source = """
            namespace Sample;

            public class AccessPlayground
            {
                protected internal void Lift()
                {
                }

                private protected void Clamp()
                {
                }
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("AccessPlayground.cs", source, "HASH");

        Assert.Contains(facts.Methods, method => method.Name == "Lift" && method.AccessModifier == "ProtectedInternal");
        Assert.Contains(facts.Methods, method => method.Name == "Clamp" && method.AccessModifier == "PrivateProtected");
    }

    [Fact]
    public void Extract_IncludesParameterModifiersInParameterSignature()
    {
        var source = """
            namespace Sample;

            public sealed class ModifierMethods
            {
                public void Run(ref int left, out int right, in int baseline, params string[] names)
                {
                    right = left + baseline + names.Length;
                }
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("ModifierMethods.cs", source, "HASH");

        var run = Assert.Single(facts.Methods, method => method.Name == "Run");
        Assert.Contains("ref int left", run.Parameters, StringComparison.Ordinal);
        Assert.Contains("out int right", run.Parameters, StringComparison.Ordinal);
        Assert.Contains("in int baseline", run.Parameters, StringComparison.Ordinal);
        Assert.Contains("params string[] names", run.Parameters, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_ClassifiesFieldAndEventReferencesAsPropertySymbols()
    {
        var source = """
            using System;

            namespace Sample;

            public sealed class SignalEmitter
            {
                private int _count;

                public event EventHandler? Changed;

                public void Tick()
                {
                    _count++;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("SignalEmitter.cs", source, "HASH");

        Assert.Contains(facts.SymbolReferences, reference => reference.SymbolName == "_count" && reference.SymbolKind == "Property" && reference.SymbolTypeName == "Int32");
        Assert.Contains(facts.SymbolReferences, reference => reference.SymbolName == "Changed" && reference.SymbolKind == "Property" && reference.SymbolTypeName == "EventHandler");
    }

    [Fact]
    public void Extract_BuildsNestedFullTypeName()
    {
        var source = """
            namespace Sample;

            public sealed class Outer
            {
                public sealed class Inner
                {
                }
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("NestedTypes.cs", source, "HASH");

        var inner = Assert.Single(facts.Types, type => type.Name == "Inner");
        Assert.Equal("Sample.Outer.Inner", inner.FullName);
    }

    [Fact]
    public void Extract_PrivateConstructorDefaultsToPrivateAccess()
    {
        var source = """
            namespace Sample;

            public sealed class Token
            {
                Token()
                {
                }
            }
            """;

        var extractor = new CSharpCodeFactsExtractor();
        var facts = extractor.Extract("Token.cs", source, "HASH");

        var constructor = Assert.Single(facts.Methods, method => method.ImplementationKind == "Constructor");
        Assert.Equal("Private", constructor.AccessModifier);
    }
}
