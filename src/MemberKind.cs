namespace SharpLensMcp;

// The documented vocabulary for the kind of a type member, as every member-listing tool
// reports it and as a memberKind filter accepts it (MemberClassifier.Classify assigns it;
// MemberClassifier.KindNames lists the names in this order). An explicit interface
// implementation has the kind of the member it implements.
internal enum MemberKind
{
    // An ordinary method (including an extension method and a delegate's Invoke).
    Method,

    // An instance constructor, a record's primary constructor included.
    Constructor,

    // A static constructor (type initializer).
    StaticConstructor,

    // A finalizer (~T()).
    Finalizer,

    // A non-indexer property.
    Property,

    // An indexer (this[...]).
    Indexer,

    // A field: instance, static, const, and enum members alike.
    Field,

    // An event.
    Event,

    // A user-defined operator (operator +, operator ==, ...).
    Operator,

    // A user-defined conversion (implicit/explicit operator T).
    Conversion,

    // A type declared inside the type.
    NestedType
}
