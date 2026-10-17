using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// Classifies a symbol into the MemberKind vocabulary. Classify returns null for a symbol
// that is never listed as a member in its own right: a property/event accessor
// (represented by its property or event), a local function, lambda or other
// compiler-shaped method, and every non-member symbol (namespace, local, parameter, ...).
internal static class MemberClassifier
{
    // The MemberKind names in declaration order: the values a memberKind parameter
    // accepts (ParameterValidation.Choice matches them case-insensitively).
    public static readonly IReadOnlyList<string> KindNames = Enum.GetNames<MemberKind>();

    public static MemberKind? Classify(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => ClassifyMethod(method),
        IPropertySymbol property => property.IsIndexer ? MemberKind.Indexer : MemberKind.Property,
        IFieldSymbol => MemberKind.Field,
        IEventSymbol => MemberKind.Event,
        INamedTypeSymbol { ContainingType: not null } => MemberKind.NestedType,
        _ => null
    };

    private static MemberKind? ClassifyMethod(IMethodSymbol method) => method.MethodKind switch
    {
        MethodKind.Ordinary or MethodKind.ReducedExtension or MethodKind.DelegateInvoke
            or MethodKind.DeclareMethod => MemberKind.Method,
        MethodKind.Constructor => MemberKind.Constructor,
        MethodKind.StaticConstructor => MemberKind.StaticConstructor,
        MethodKind.Destructor => MemberKind.Finalizer,
        MethodKind.UserDefinedOperator or MethodKind.BuiltinOperator => MemberKind.Operator,
        MethodKind.Conversion => MemberKind.Conversion,
        // An explicit implementation is the kind of what it implements (a method or an
        // operator/conversion of a static abstract interface member).
        MethodKind.ExplicitInterfaceImplementation =>
            method.ExplicitInterfaceImplementations.FirstOrDefault() is { } implemented
                ? ClassifyMethod(implemented)
                : MemberKind.Method,
        _ => null
    };
}
