using LangQuery.Core.Abstractions;
using LangQuery.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LangQuery.Roslyn.Extraction;

public sealed class CSharpCodeFactsExtractor : ICodeFactsExtractor
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> MetadataReferences = new(BuildMetadataReferences);

    public bool CanHandle(string filePath)
    {
        return string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase);
    }

    public FileFacts Extract(string filePath, string content, string hash)
    {
        var tree = CSharpSyntaxTree.ParseText(content, new CSharpParseOptions(LanguageVersion.Latest), filePath);
        var root = tree.GetRoot();
        var text = tree.GetText();
        var compilation = CSharpCompilation.Create(
            assemblyName: "LangQuery.Extraction",
            syntaxTrees: [tree],
            references: MetadataReferences.Value,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);

        var types = ExtractTypes(root);
        var typeInheritances = ExtractTypeInheritances(types);
        var methods = ExtractMethods(root, types);

        var variables = new List<VariableFact>();
        var lineVariables = new List<LineVariableFact>();
        var invocations = new List<InvocationFact>();
        var symbolRefs = new List<SymbolReferenceFact>();
        var variableCountByMethodAndLine = new Dictionary<(string MethodKey, int LineNumber), HashSet<string>>();
        var blockDepthByMethodAndLine = new Dictionary<(string MethodKey, int LineNumber), int>();

        foreach (var context in methods)
        {
            var methodVariables = ExtractVariablesForImplementation(context.Node, context.Fact.MethodKey);
            foreach (var variable in methodVariables)
            {
                variables.Add(variable);
            }

            var variablesByName = methodVariables
                .GroupBy(x => x.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(v => v.DeclarationLine).ToArray(), StringComparer.Ordinal);

            foreach (var identifier in EnumerateOwnedDescendants<IdentifierNameSyntax>(context.Node))
            {
                var symbolName = identifier.Identifier.ValueText;
                var lineNumber = tree.GetLineSpan(identifier.Span).StartLinePosition.Line + 1;
                var depth = ComputeBlockDepth(identifier, context.Node);

                UpdateDepth(blockDepthByMethodAndLine, context.Fact.MethodKey, lineNumber, depth);

                if (variablesByName.TryGetValue(symbolName, out var candidates))
                {
                    AddLineVariableUsage(variableCountByMethodAndLine, context.Fact.MethodKey, lineNumber, symbolName);
                    var selected = SelectBestVariable(candidates, lineNumber);
                    lineVariables.Add(new LineVariableFact(lineNumber, context.Fact.MethodKey, symbolName, selected.VariableKey));
                    symbolRefs.Add(new SymbolReferenceFact(
                        lineNumber,
                        context.Fact.MethodKey,
                        symbolName,
                        "Variable",
                        SymbolContainerTypeName: null,
                        SymbolTypeName: selected.TypeName));
                }
                else
                {
                    var symbolDetails = ResolveNonVariableSymbolDetails(identifier, semanticModel);
                    var symbolKind = symbolDetails.SymbolKind ?? ResolveNonVariableSymbolKind(identifier);
                    symbolRefs.Add(new SymbolReferenceFact(
                        lineNumber,
                        context.Fact.MethodKey,
                        symbolName,
                        symbolKind,
                        symbolDetails.SymbolContainerTypeName,
                        symbolDetails.SymbolTypeName));
                }
            }

            foreach (var invocation in EnumerateOwnedDescendants<InvocationExpressionSyntax>(context.Node))
            {
                var lineNumber = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1;
                var targetName = invocation.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    GenericNameSyntax generic => generic.Identifier.ValueText,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                    _ => invocation.Expression.ToString()
                };

                invocations.Add(new InvocationFact(context.Fact.MethodKey, lineNumber, invocation.ToString(), targetName));

                var depth = ComputeBlockDepth(invocation, context.Node);
                UpdateDepth(blockDepthByMethodAndLine, context.Fact.MethodKey, lineNumber, depth);
            }
        }

        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (ResolveMethodForNode(identifier, methods) is not null)
            {
                continue;
            }

            var lineNumber = tree.GetLineSpan(identifier.Span).StartLinePosition.Line + 1;
            var symbolDetails = ResolveNonVariableSymbolDetails(identifier, semanticModel);
            var symbolKind = symbolDetails.SymbolKind ?? ResolveNonVariableSymbolKind(identifier);
            symbolRefs.Add(new SymbolReferenceFact(
                lineNumber,
                null,
                identifier.Identifier.ValueText,
                symbolKind,
                symbolDetails.SymbolContainerTypeName,
                symbolDetails.SymbolTypeName));
        }

        var methodRanges = methods
            .Select(x => new MethodRange(x.Fact.MethodKey, x.Fact.LineStart, x.Fact.LineEnd))
            .ToArray();

        var lineFacts = new List<LineFact>(text.Lines.Count);
        foreach (var line in text.Lines)
        {
            var lineNumber = line.LineNumber + 1;
            var methodKey = ResolveMethodForLine(methodRanges, lineNumber);
            var variableCount = 0;
            var depth = 0;

            if (methodKey is not null)
            {
                if (variableCountByMethodAndLine.TryGetValue((methodKey, lineNumber), out var variableNames))
                {
                    variableCount = variableNames.Count;
                }

                if (blockDepthByMethodAndLine.TryGetValue((methodKey, lineNumber), out var mappedDepth))
                {
                    depth = mappedDepth;
                }
            }

            lineFacts.Add(new LineFact(lineNumber, line.ToString(), methodKey, depth, variableCount));
        }

        return new FileFacts(
            Path.GetFullPath(filePath),
            hash,
            "csharp",
            types.Select(x => x.Fact).ToArray(),
            typeInheritances,
            methods.Select(x => x.Fact).ToArray(),
            lineFacts,
            variables,
            lineVariables,
            invocations,
            symbolRefs);
    }

    private static IReadOnlyList<TypeContext> ExtractTypes(SyntaxNode root)
    {
        var list = new List<TypeContext>();

        foreach (var typeNode in root.DescendantNodes().Where(IsSupportedTypeNode))
        {
            var (name, kind) = GetTypeInfo(typeNode);
            var lineSpan = typeNode.SyntaxTree.GetLineSpan(typeNode.Span).StartLinePosition;
            var typeKey = BuildTypeKey(typeNode, lineSpan.Line + 1, name);
            var fullName = BuildFullTypeName(typeNode, name);
            var modifiers = GetTypeModifiers(typeNode);
            var accessModifier = ResolveAccessModifier(modifiers, ResolveDefaultTypeAccessModifier(typeNode));
            var modifierSet = BuildModifierSet(modifiers);

            var fact = new TypeFact(
                typeKey,
                name,
                kind,
                accessModifier,
                modifierSet,
                fullName,
                lineSpan.Line + 1,
                lineSpan.Character + 1);

            list.Add(new TypeContext(typeNode, fact));
        }

        return list;
    }

    private static IReadOnlyList<TypeInheritanceFact> ExtractTypeInheritances(IReadOnlyList<TypeContext> types)
    {
        var inheritances = new List<TypeInheritanceFact>();

        foreach (var type in types)
        {
            if (type.Node is not TypeDeclarationSyntax typeDeclaration || typeDeclaration.BaseList is null)
            {
                continue;
            }

            for (var index = 0; index < typeDeclaration.BaseList.Types.Count; index++)
            {
                var baseTypeName = typeDeclaration.BaseList.Types[index].Type.ToString();
                if (string.IsNullOrWhiteSpace(baseTypeName))
                {
                    continue;
                }

                inheritances.Add(new TypeInheritanceFact(
                    type.Fact.TypeKey,
                    baseTypeName,
                    ResolveRelationKind(typeDeclaration, index)));
            }
        }

        return inheritances;
    }

    private static IReadOnlyList<MethodContext> ExtractMethods(SyntaxNode root, IReadOnlyList<TypeContext> types)
    {
        var list = new List<MethodContext>();
        var typeByNode = types.ToDictionary(x => x.Node, x => x.Fact, ReferenceEqualityComparer.Instance);

        foreach (var methodNode in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            if (methodNode is not MethodDeclarationSyntax && methodNode is not ConstructorDeclarationSyntax)
            {
                continue;
            }

            AddMethodContext(methodNode, parentMethodKey: null, typeByNode, list);
        }

        return list;
    }

    private static void AddMethodContext(
        SyntaxNode implementationNode,
        string? parentMethodKey,
        IReadOnlyDictionary<SyntaxNode, TypeFact> typeByNode,
        ICollection<MethodContext> collector)
    {
        var fact = BuildMethodFact(implementationNode, parentMethodKey, typeByNode);
        collector.Add(new MethodContext(implementationNode, fact));

        foreach (var nestedNode in GetDirectNestedImplementations(implementationNode))
        {
            AddMethodContext(nestedNode, fact.MethodKey, typeByNode, collector);
        }
    }

    private static IEnumerable<SyntaxNode> GetDirectNestedImplementations(SyntaxNode owner)
    {
        foreach (var candidate in owner.DescendantNodes().Where(IsNestedImplementationNode))
        {
            var nearestOwner = candidate.Ancestors().FirstOrDefault(IsImplementationNode);
            if (ReferenceEquals(nearestOwner, owner))
            {
                yield return candidate;
            }
        }
    }

    private static MethodFact BuildMethodFact(
        SyntaxNode implementationNode,
        string? parentMethodKey,
        IReadOnlyDictionary<SyntaxNode, TypeFact> typeByNode)
    {
        var lineSpan = implementationNode.SyntaxTree.GetLineSpan(implementationNode.Span);
        var lineStart = lineSpan.StartLinePosition.Line + 1;
        var lineEnd = lineSpan.EndLinePosition.Line + 1;
        var columnStart = lineSpan.StartLinePosition.Character + 1;
        var columnEnd = lineSpan.EndLinePosition.Character + 1;

        var (name, returnType, implementationKind, modifiers, defaultAccessModifier) = GetImplementationShape(implementationNode);
        var parameters = GetParametersForImplementation(implementationNode);
        var parameterSignature = BuildParameterSignature(parameters);
        var accessModifier = ResolveAccessModifier(modifiers, defaultAccessModifier);
        var modifierSet = BuildModifierSet(modifiers);

        var typeNode = implementationNode.Ancestors().FirstOrDefault(IsSupportedTypeNode);
        string? typeKey = null;
        if (typeNode is not null && typeByNode.TryGetValue(typeNode, out var typeFact))
        {
            typeKey = typeFact.TypeKey;
        }

        var methodKey = BuildMethodKey(typeKey, parentMethodKey, implementationKind, name, lineStart, columnStart);

        return new MethodFact(
            methodKey,
            name,
            returnType,
            parameterSignature,
            parameters.Count,
            accessModifier,
            modifierSet,
            implementationKind,
            parentMethodKey,
            typeKey,
            lineStart,
            lineEnd,
            columnStart,
            columnEnd);
    }

    private static (string Name, string ReturnType, string Kind, SyntaxTokenList Modifiers, string DefaultAccessModifier) GetImplementationShape(SyntaxNode node)
    {
        return node switch
        {
            MethodDeclarationSyntax method => (
                method.Identifier.ValueText,
                method.ReturnType.ToString(),
                "Method",
                method.Modifiers,
                IsInsideInterface(node) ? "Public" : "Private"),

            ConstructorDeclarationSyntax constructor => (
                constructor.Identifier.ValueText,
                "ctor",
                "Constructor",
                constructor.Modifiers,
                "Private"),

            LocalFunctionStatementSyntax localFunction => (
                localFunction.Identifier.ValueText,
                localFunction.ReturnType.ToString(),
                "LocalFunction",
                localFunction.Modifiers,
                "Local"),

            ParenthesizedLambdaExpressionSyntax => (
                "<lambda>",
                "lambda",
                "Lambda",
                default,
                "Local"),

            SimpleLambdaExpressionSyntax => (
                "<lambda>",
                "lambda",
                "Lambda",
                default,
                "Local"),

            AnonymousMethodExpressionSyntax => (
                "<anonymous>",
                "lambda",
                "AnonymousMethod",
                default,
                "Local"),

            _ => throw new InvalidOperationException($"Unsupported implementation node: {node.Kind()}")
        };
    }

    private static IReadOnlyList<VariableFact> ExtractVariablesForImplementation(SyntaxNode implementationNode, string methodKey)
    {
        var variables = new List<VariableFact>();

        foreach (var parameter in GetParametersForImplementation(implementationNode))
        {
            var name = parameter.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var line = implementationNode.SyntaxTree.GetLineSpan(parameter.Span).StartLinePosition.Line + 1;
            var typeName = parameter.Type?.ToString();
            var variableKey = BuildVariableKey(methodKey, name, line, "Parameter");
            variables.Add(new VariableFact(variableKey, methodKey, name, "Parameter", typeName, line));
        }

        foreach (var declaration in EnumerateOwnedDescendants<VariableDeclaratorSyntax>(implementationNode))
        {
            var name = declaration.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var line = implementationNode.SyntaxTree.GetLineSpan(declaration.Span).StartLinePosition.Line + 1;
            var typeName = (declaration.Parent as VariableDeclarationSyntax)?.Type.ToString();
            var variableKey = BuildVariableKey(methodKey, name, line, "Local");
            variables.Add(new VariableFact(variableKey, methodKey, name, "Local", typeName, line));
        }

        foreach (var foreachStatement in EnumerateOwnedDescendants<ForEachStatementSyntax>(implementationNode))
        {
            var name = foreachStatement.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var line = implementationNode.SyntaxTree.GetLineSpan(foreachStatement.Identifier.Span).StartLinePosition.Line + 1;
            var typeName = foreachStatement.Type?.ToString();
            var variableKey = BuildVariableKey(methodKey, name, line, "ForEach");
            variables.Add(new VariableFact(variableKey, methodKey, name, "ForEach", typeName, line));
        }

        foreach (var catchDeclaration in EnumerateOwnedDescendants<CatchDeclarationSyntax>(implementationNode))
        {
            var name = catchDeclaration.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var line = implementationNode.SyntaxTree.GetLineSpan(catchDeclaration.Identifier.Span).StartLinePosition.Line + 1;
            var typeName = catchDeclaration.Type?.ToString();
            var variableKey = BuildVariableKey(methodKey, name, line, "Catch");
            variables.Add(new VariableFact(variableKey, methodKey, name, "Catch", typeName, line));
        }

        return variables;
    }

    private static IReadOnlyList<ParameterSyntax> GetParametersForImplementation(SyntaxNode implementationNode)
    {
        return implementationNode switch
        {
            MethodDeclarationSyntax method => method.ParameterList.Parameters.ToArray(),
            ConstructorDeclarationSyntax constructor => constructor.ParameterList.Parameters.ToArray(),
            LocalFunctionStatementSyntax localFunction => localFunction.ParameterList.Parameters.ToArray(),
            ParenthesizedLambdaExpressionSyntax lambda => lambda.ParameterList.Parameters.ToArray(),
            SimpleLambdaExpressionSyntax simpleLambda => [simpleLambda.Parameter],
            AnonymousMethodExpressionSyntax anonymousMethod when anonymousMethod.ParameterList is not null => anonymousMethod.ParameterList.Parameters.ToArray(),
            _ => []
        };
    }

    private static string BuildParameterSignature(IReadOnlyList<ParameterSyntax> parameters)
    {
        if (parameters.Count == 0)
        {
            return string.Empty;
        }

        var values = new List<string>(parameters.Count);
        foreach (var parameter in parameters)
        {
            var identifier = parameter.Identifier.ValueText;
            var baseValue = parameter.Type is null
                ? identifier
                : string.IsNullOrWhiteSpace(identifier)
                    ? parameter.Type.ToString()
                    : $"{parameter.Type} {identifier}";

            if (parameter.Modifiers.Count > 0)
            {
                baseValue = $"{string.Join(' ', parameter.Modifiers.Select(modifier => modifier.ValueText))} {baseValue}";
            }

            if (!string.IsNullOrWhiteSpace(baseValue))
            {
                values.Add(baseValue.Trim());
            }
        }

        return values.Count == 0 ? string.Empty : string.Join(", ", values);
    }

    private static string ResolveNonVariableSymbolKind(IdentifierNameSyntax identifier)
    {
        if (IsInvokedIdentifier(identifier))
        {
            return "Method";
        }

        if (IsMemberAccessName(identifier))
        {
            return "Property";
        }

        return "Identifier";
    }

    private static SymbolReferenceDetails ResolveNonVariableSymbolDetails(IdentifierNameSyntax identifier, SemanticModel semanticModel)
    {
        var info = semanticModel.GetSymbolInfo(identifier);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (symbol is null)
        {
            return SymbolReferenceDetails.Empty;
        }

        return symbol switch
        {
            IPropertySymbol property => new SymbolReferenceDetails(
                SymbolKind: "Property",
                SymbolContainerTypeName: property.ContainingType?.Name,
                SymbolTypeName: GetTypeName(property.Type)),

            IMethodSymbol method => new SymbolReferenceDetails(
                SymbolKind: "Method",
                SymbolContainerTypeName: method.ContainingType?.Name,
                SymbolTypeName: GetTypeName(method.ReturnType)),

            IFieldSymbol field => new SymbolReferenceDetails(
                SymbolKind: "Property",
                SymbolContainerTypeName: field.ContainingType?.Name,
                SymbolTypeName: GetTypeName(field.Type)),

            IEventSymbol eventSymbol => new SymbolReferenceDetails(
                SymbolKind: "Property",
                SymbolContainerTypeName: eventSymbol.ContainingType?.Name,
                SymbolTypeName: GetTypeName(eventSymbol.Type)),

            ILocalSymbol local => new SymbolReferenceDetails(
                SymbolKind: "Variable",
                SymbolContainerTypeName: null,
                SymbolTypeName: GetTypeName(local.Type)),

            IParameterSymbol parameter => new SymbolReferenceDetails(
                SymbolKind: "Variable",
                SymbolContainerTypeName: null,
                SymbolTypeName: GetTypeName(parameter.Type)),

            _ => new SymbolReferenceDetails(
                SymbolKind: null,
                SymbolContainerTypeName: symbol.ContainingType?.Name,
                SymbolTypeName: GetSymbolTypeName(symbol))
        };
    }

    private static string? GetSymbolTypeName(ISymbol symbol)
    {
        return symbol switch
        {
            IPropertySymbol property => GetTypeName(property.Type),
            IMethodSymbol method => GetTypeName(method.ReturnType),
            IFieldSymbol field => GetTypeName(field.Type),
            IEventSymbol eventSymbol => GetTypeName(eventSymbol.Type),
            ILocalSymbol local => GetTypeName(local.Type),
            IParameterSymbol parameter => GetTypeName(parameter.Type),
            _ => null
        };
    }

    private static string? GetTypeName(ITypeSymbol? typeSymbol)
    {
        if (typeSymbol is null)
        {
            return null;
        }

        if (typeSymbol is INamedTypeSymbol namedType)
        {
            return namedType.Name;
        }

        return typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    private static IReadOnlyList<MetadataReference> BuildMetadataReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedAssemblies)
        {
            foreach (var path in trustedAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (File.Exists(path))
                {
                    paths.Add(path);
                }
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            AddMetadataReferencePath(paths, assembly);
        }

        TryLoadMetadataAssembly(paths, "Microsoft.Data.Sqlite");

        return paths
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static void TryLoadMetadataAssembly(ISet<string> paths, string assemblyName)
    {
        try
        {
            var assembly = Assembly.Load(new AssemblyName(assemblyName));
            AddMetadataReferencePath(paths, assembly);
        }
        catch
        {
        }
    }

    private static void AddMetadataReferencePath(ISet<string> paths, Assembly assembly)
    {
        if (assembly.IsDynamic)
        {
            return;
        }

        var location = assembly.Location;
        if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
        {
            paths.Add(location);
        }
    }

    private static bool IsInvokedIdentifier(IdentifierNameSyntax identifier)
    {
        if (identifier.Parent is InvocationExpressionSyntax invocation &&
            ReferenceEquals(invocation.Expression, identifier))
        {
            return true;
        }

        if (identifier.Parent is MemberAccessExpressionSyntax memberAccess &&
            ReferenceEquals(memberAccess.Name, identifier) &&
            memberAccess.Parent is InvocationExpressionSyntax memberInvocation &&
            ReferenceEquals(memberInvocation.Expression, memberAccess))
        {
            return true;
        }

        if (identifier.Parent is MemberBindingExpressionSyntax memberBinding &&
            ReferenceEquals(memberBinding.Name, identifier) &&
            memberBinding.Parent is InvocationExpressionSyntax bindingInvocation &&
            ReferenceEquals(bindingInvocation.Expression, memberBinding))
        {
            return true;
        }

        return false;
    }

    private static bool IsMemberAccessName(IdentifierNameSyntax identifier)
    {
        return (identifier.Parent is MemberAccessExpressionSyntax memberAccess && ReferenceEquals(memberAccess.Name, identifier))
            || (identifier.Parent is MemberBindingExpressionSyntax memberBinding && ReferenceEquals(memberBinding.Name, identifier));
    }

    private static IEnumerable<TNode> EnumerateOwnedDescendants<TNode>(SyntaxNode implementationNode)
        where TNode : SyntaxNode
    {
        return implementationNode
            .DescendantNodes(descendIntoChildren: child => ShouldDescendIntoChild(child, implementationNode))
            .OfType<TNode>();
    }

    private static bool ShouldDescendIntoChild(SyntaxNode node, SyntaxNode owner)
    {
        if (ReferenceEquals(node, owner))
        {
            return true;
        }

        return !IsNestedImplementationNode(node);
    }

    private static VariableFact SelectBestVariable(IReadOnlyList<VariableFact> candidates, int usageLine)
    {
        return candidates
            .Where(v => v.DeclarationLine <= usageLine)
            .DefaultIfEmpty(candidates.OrderBy(v => v.DeclarationLine).First())
            .OrderByDescending(v => v.DeclarationLine)
            .First();
    }

    private static MethodContext? ResolveMethodForNode(SyntaxNode node, IReadOnlyList<MethodContext> methods)
    {
        return methods
            .Where(method => method.Node.Span.Start <= node.Span.Start && node.Span.End <= method.Node.Span.End)
            .OrderBy(method => method.Node.Span.Length)
            .FirstOrDefault();
    }

    private static string? ResolveMethodForLine(IReadOnlyList<MethodRange> methods, int lineNumber)
    {
        return methods
            .Where(m => lineNumber >= m.LineStart && lineNumber <= m.LineEnd)
            .OrderBy(m => m.LineEnd - m.LineStart)
            .Select(m => m.MethodKey)
            .FirstOrDefault();
    }

    private static int ComputeBlockDepth(SyntaxNode node, SyntaxNode implementationNode)
    {
        var rootBlock = GetImplementationRootBlock(implementationNode);
        var depth = 0;

        for (var current = node.Parent; current is not null && !ReferenceEquals(current, implementationNode); current = current.Parent)
        {
            if (current is BlockSyntax block && !ReferenceEquals(block, rootBlock))
            {
                depth++;
            }
        }

        return depth;
    }

    private static BlockSyntax? GetImplementationRootBlock(SyntaxNode implementationNode)
    {
        return implementationNode switch
        {
            MethodDeclarationSyntax method => method.Body,
            ConstructorDeclarationSyntax constructor => constructor.Body,
            LocalFunctionStatementSyntax localFunction => localFunction.Body,
            ParenthesizedLambdaExpressionSyntax parenthesizedLambda => parenthesizedLambda.Block,
            SimpleLambdaExpressionSyntax simpleLambda => simpleLambda.Block,
            AnonymousMethodExpressionSyntax anonymousMethod => anonymousMethod.Block,
            _ => null
        };
    }

    private static void AddLineVariableUsage(
        IDictionary<(string MethodKey, int LineNumber), HashSet<string>> lookup,
        string methodKey,
        int lineNumber,
        string variableName)
    {
        if (!lookup.TryGetValue((methodKey, lineNumber), out var values))
        {
            values = new HashSet<string>(StringComparer.Ordinal);
            lookup[(methodKey, lineNumber)] = values;
        }

        values.Add(variableName);
    }

    private static void UpdateDepth(
        IDictionary<(string MethodKey, int LineNumber), int> lookup,
        string methodKey,
        int lineNumber,
        int depth)
    {
        var key = (methodKey, lineNumber);
        if (!lookup.TryGetValue(key, out var existing) || depth > existing)
        {
            lookup[key] = depth;
        }
    }

    private static bool IsSupportedTypeNode(SyntaxNode node)
    {
        return node is TypeDeclarationSyntax or EnumDeclarationSyntax;
    }

    private static bool IsImplementationNode(SyntaxNode node)
    {
        return node is MethodDeclarationSyntax
            or ConstructorDeclarationSyntax
            or LocalFunctionStatementSyntax
            or AnonymousFunctionExpressionSyntax;
    }

    private static bool IsNestedImplementationNode(SyntaxNode node)
    {
        return node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax;
    }

    private static (string Name, string Kind) GetTypeInfo(SyntaxNode node)
    {
        return node switch
        {
            ClassDeclarationSyntax classNode => (classNode.Identifier.ValueText, "Class"),
            StructDeclarationSyntax structNode => (structNode.Identifier.ValueText, "Struct"),
            InterfaceDeclarationSyntax interfaceNode => (interfaceNode.Identifier.ValueText, "Interface"),
            RecordDeclarationSyntax recordNode => (recordNode.Identifier.ValueText, "Record"),
            EnumDeclarationSyntax enumNode => (enumNode.Identifier.ValueText, "Enum"),
            _ => throw new InvalidOperationException($"Unsupported type node: {node.Kind()}")
        };
    }

    private static SyntaxTokenList GetTypeModifiers(SyntaxNode typeNode)
    {
        return typeNode switch
        {
            TypeDeclarationSyntax typeDeclaration => typeDeclaration.Modifiers,
            EnumDeclarationSyntax enumDeclaration => enumDeclaration.Modifiers,
            _ => default
        };
    }

    private static string ResolveDefaultTypeAccessModifier(SyntaxNode typeNode)
    {
        var isNested = typeNode.Ancestors().Any(IsSupportedTypeNode);
        return isNested ? "Private" : "Internal";
    }

    private static string ResolveAccessModifier(SyntaxTokenList modifiers, string fallback)
    {
        var hasPublic = modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
        var hasPrivate = modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));
        var hasProtected = modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword));
        var hasInternal = modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
        var hasFile = modifiers.Any(m => m.IsKind(SyntaxKind.FileKeyword));

        if (hasPublic)
        {
            return "Public";
        }

        if (hasPrivate && hasProtected)
        {
            return "PrivateProtected";
        }

        if (hasProtected && hasInternal)
        {
            return "ProtectedInternal";
        }

        if (hasProtected)
        {
            return "Protected";
        }

        if (hasInternal)
        {
            return "Internal";
        }

        if (hasPrivate)
        {
            return "Private";
        }

        if (hasFile)
        {
            return "File";
        }

        return fallback;
    }

    private static string BuildModifierSet(SyntaxTokenList modifiers)
    {
        var values = new List<string>();

        foreach (var modifier in modifiers)
        {
            if (IsAccessModifierToken(modifier))
            {
                continue;
            }

            var name = NormalizeModifierToken(modifier);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!values.Contains(name, StringComparer.Ordinal))
            {
                values.Add(name);
            }
        }

        return values.Count == 0 ? string.Empty : string.Join(',', values);
    }

    private static bool IsAccessModifierToken(SyntaxToken token)
    {
        return token.IsKind(SyntaxKind.PublicKeyword)
            || token.IsKind(SyntaxKind.PrivateKeyword)
            || token.IsKind(SyntaxKind.ProtectedKeyword)
            || token.IsKind(SyntaxKind.InternalKeyword)
            || token.IsKind(SyntaxKind.FileKeyword);
    }

    private static string NormalizeModifierToken(SyntaxToken token)
    {
        return token.Kind() switch
        {
            SyntaxKind.AbstractKeyword => "Abstract",
            SyntaxKind.AsyncKeyword => "Async",
            SyntaxKind.ConstKeyword => "Const",
            SyntaxKind.ExternKeyword => "Extern",
            SyntaxKind.NewKeyword => "New",
            SyntaxKind.OverrideKeyword => "Override",
            SyntaxKind.PartialKeyword => "Partial",
            SyntaxKind.ReadOnlyKeyword => "ReadOnly",
            SyntaxKind.RefKeyword => "Ref",
            SyntaxKind.SealedKeyword => "Sealed",
            SyntaxKind.StaticKeyword => "Static",
            SyntaxKind.UnsafeKeyword => "Unsafe",
            SyntaxKind.VirtualKeyword => "Virtual",
            _ => ToPascal(token.ValueText)
        };
    }

    private static bool IsInsideInterface(SyntaxNode node)
    {
        return node.Ancestors().Any(ancestor => ancestor is InterfaceDeclarationSyntax);
    }

    private static string ToPascal(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.Length == 1)
        {
            return value.ToUpperInvariant();
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string BuildTypeKey(SyntaxNode typeNode, int line, string name)
    {
        var namespaceName = typeNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Select(ns => ns.Name.ToString())
            .FirstOrDefault();

        var path = namespaceName is null ? name : $"{namespaceName}.{name}";
        return $"{path}@{line}";
    }

    private static string BuildFullTypeName(SyntaxNode typeNode, string typeName)
    {
        var namespaceName = typeNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Select(ns => ns.Name.ToString())
            .FirstOrDefault();

        var parentTypes = typeNode.Ancestors()
            .Where(IsSupportedTypeNode)
            .Select(parent => GetTypeInfo(parent).Name)
            .Reverse()
            .ToArray();

        var nestedName = parentTypes.Length == 0
            ? typeName
            : string.Join('.', parentTypes.Append(typeName));

        return namespaceName is null ? nestedName : $"{namespaceName}.{nestedName}";
    }

    private static string BuildMethodKey(
        string? typeKey,
        string? parentMethodKey,
        string implementationKind,
        string name,
        int lineStart,
        int columnStart)
    {
        var scope = parentMethodKey ?? typeKey ?? "global";
        return $"{scope}:{implementationKind}:{name}@{lineStart}:{columnStart}";
    }

    private static string BuildVariableKey(string methodKey, string variableName, int declarationLine, string kind)
    {
        return $"{methodKey}:{kind}:{variableName}@{declarationLine}";
    }

    private static string ResolveRelationKind(TypeDeclarationSyntax typeDeclaration, int baseTypeIndex)
    {
        if (typeDeclaration is InterfaceDeclarationSyntax)
        {
            return "BaseInterface";
        }

        if (typeDeclaration is StructDeclarationSyntax)
        {
            return "Interface";
        }

        if (baseTypeIndex == 0)
        {
            return "BaseType";
        }

        return "Interface";
    }

    private sealed record TypeContext(SyntaxNode Node, TypeFact Fact);

    private sealed record MethodContext(SyntaxNode Node, MethodFact Fact);

    private sealed record MethodRange(string MethodKey, int LineStart, int LineEnd);

    private sealed record SymbolReferenceDetails(
        string? SymbolKind,
        string? SymbolContainerTypeName,
        string? SymbolTypeName)
    {
        public static SymbolReferenceDetails Empty { get; } = new(null, null, null);
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<SyntaxNode>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public bool Equals(SyntaxNode? x, SyntaxNode? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(SyntaxNode obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
