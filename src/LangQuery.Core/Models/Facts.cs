namespace LangQuery.Core.Models;

public sealed record SourceFileFingerprint(string Path, string Hash, string Language);

public sealed record FileFacts(
    string Path,
    string Hash,
    string Language,
    IReadOnlyList<TypeFact> Types,
    IReadOnlyList<TypeInheritanceFact> TypeInheritances,
    IReadOnlyList<MethodFact> Methods,
    IReadOnlyList<LineFact> Lines,
    IReadOnlyList<VariableFact> Variables,
    IReadOnlyList<LineVariableFact> LineVariables,
    IReadOnlyList<InvocationFact> Invocations,
    IReadOnlyList<SymbolReferenceFact> SymbolReferences);

public sealed record TypeFact(
    string TypeKey,
    string Name,
    string Kind,
    string AccessModifier,
    string Modifiers,
    string FullName,
    int Line,
    int Column);

public sealed record TypeInheritanceFact(
    string TypeKey,
    string BaseTypeName,
    string RelationKind);

public sealed record MethodFact(
    string MethodKey,
    string Name,
    string ReturnType,
    string Parameters,
    string AccessModifier,
    string Modifiers,
    string ImplementationKind,
    string? ParentMethodKey,
    string? TypeKey,
    int LineStart,
    int LineEnd,
    int ColumnStart,
    int ColumnEnd);

public sealed record LineFact(
    int LineNumber,
    string Text,
    string? MethodKey,
    int BlockDepthInMethod);

public sealed record VariableFact(
    string VariableKey,
    string MethodKey,
    string Name,
    string Kind,
    string? TypeName,
    int DeclarationLine);

public sealed record LineVariableFact(
    int LineNumber,
    string VariableKey);

public sealed record InvocationFact(
    string MethodKey,
    int LineNumber,
    string Expression,
    string TargetName);

public sealed record SymbolReferenceFact(
    int LineNumber,
    string? MethodKey,
    string SymbolName,
    string SymbolKind,
    string? SymbolContainerTypeName = null,
    string? SymbolTypeName = null);
