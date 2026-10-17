namespace SharpLensMcp;

// How one call site supplies one parameter, read from the bound call: Index is the
// argument's position in the argument list (Explicit), Elements the positions of an
// expanded params call's surplus arguments (Expanded).
internal sealed record BoundArgument(
    BoundArgumentKind Kind, int Index = -1, bool IsNamed = false, IReadOnlyList<int>? Elements = null);
