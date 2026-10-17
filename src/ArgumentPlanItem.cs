namespace SharpLensMcp;

// One argument of a rewritten call, in its new position:
//   - an existing argument (OriginalIndex), kept as written or given the name of the
//     parameter it binds to (NameToAdd) when it can no longer stay positional;
//   - an added argument (Expression, positional or named by NameToAdd);
//   - the surplus arguments of an expanded params call collected into one named array
//     argument (ElementIndices, NameToAdd, ElementType).
internal sealed record ArgumentPlanItem(
    int OriginalIndex = -1,
    string? NameToAdd = null,
    string? Expression = null,
    IReadOnlyList<int>? ElementIndices = null,
    string? ElementType = null)
{
    public static ArgumentPlanItem Original(int index, string? nameToAdd = null) => new(index, nameToAdd);

    public static ArgumentPlanItem Added(string expression, string? name) => new(NameToAdd: name, Expression: expression);

    public static ArgumentPlanItem ParamsArray(IReadOnlyList<int> elementIndices, string name, string elementType) =>
        new(NameToAdd: name, ElementIndices: elementIndices, ElementType: elementType);
}
