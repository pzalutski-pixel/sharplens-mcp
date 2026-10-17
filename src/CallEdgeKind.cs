namespace SharpLensMcp;

// How a body reaches the target of a CallEdge.
internal enum CallEdgeKind
{
    // An ordinary method call, including a collection initializer's implicit Add.
    Invocation,
    // new T(...), target-typed new(), or a struct's default constructor.
    ObjectCreation,
    // A constructor's : base(...) / : this(...) call, or the implicit base() call.
    ConstructorInitializer,
    // A method group converted to a delegate (the method runs when the delegate is invoked).
    DelegateCreation,
    // A property or indexer read.
    PropertyGet,
    // A property or indexer write (assignment, object-initializer member, deconstruction target).
    PropertySet,
    // An event subscription (+=).
    EventAdd,
    // An event unsubscription (-=).
    EventRemove,
    // A field, an event used as a value, or a property with no accessor for the access made
    // (a get-only auto-property assigned in a constructor writes its backing field).
    MemberAccess,
    // A user-defined operator, including the operator true/false a user-defined && or || tests.
    Operator,
    // A user-defined implicit or explicit conversion.
    Conversion,
    // A call the compiler emits for a language construct: foreach's GetEnumerator/MoveNext/
    // Current/Dispose, using's Dispose, Deconstruct, await's awaiter members, a record's
    // clone for `with`, a collection expression's builder, and index/range members.
    ImplicitCall
}
