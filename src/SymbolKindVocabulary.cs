using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The kind vocabulary of the symbol-search tools (search_symbols, semantic_query,
// get_containing_member): what they label a result with and what their kind filters
// accept, so a filter value always selects exactly the results labeled with it.
//   - A type is labeled by its kind: Class, Record (a record class), Struct, RecordStruct,
//     Interface, Enum or Delegate. A record IS a class and a record struct IS a struct, so
//     the Class filter also selects Record results and the Struct filter RecordStruct
//     results; every other filter selects its own label alone.
//   - A member is labeled by its MemberKind (Method, Constructor, StaticConstructor,
//     Finalizer, Property, Indexer, Field, Event, Operator, Conversion); a nested type is
//     labeled by its type kind, never NestedType.
//   - A namespace is labeled Namespace.
// Label is null for everything else (accessors, local functions, parameters, extension
// blocks, error types), which these tools do not report.
internal static class SymbolKindVocabulary
{
    public const string Namespace = "Namespace";
    public const string Class = "Class";
    public const string Record = "Record";
    public const string Struct = "Struct";
    public const string RecordStruct = "RecordStruct";

    public static readonly IReadOnlyList<string> TypeKinds =
        new[] { Class, Record, Struct, RecordStruct, "Interface", "Enum", "Delegate" };

    public static readonly IReadOnlyList<string> MemberKinds =
        MemberClassifier.KindNames.Where(k => k != nameof(MemberKind.NestedType)).ToList();

    // Every type and member kind, in vocabulary order.
    public static readonly IReadOnlyList<string> TypeAndMemberKinds = TypeKinds.Concat(MemberKinds).ToList();

    // TypeAndMemberKinds followed by Namespace.
    public static readonly IReadOnlyList<string> AllKinds = TypeAndMemberKinds.Append(Namespace).ToList();

    public static string? Label(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => Namespace,
        INamedTypeSymbol type => TypeLabel(type),
        _ => MemberClassifier.Classify(symbol)?.ToString()
    };

    private static string? TypeLabel(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Class => type.IsRecord ? Record : Class,
        TypeKind.Struct => type.IsRecord ? RecordStruct : Struct,
        TypeKind.Interface => "Interface",
        TypeKind.Enum => "Enum",
        TypeKind.Delegate => "Delegate",
        _ => null
    };

    // Whether a result labeled `label` is selected by the (canonical) filter value.
    public static bool Selects(string filter, string label) =>
        filter == label
        || (filter == Class && label == Record)
        || (filter == Struct && label == RecordStruct);
}
