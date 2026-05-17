namespace SharpLensMcp;

internal sealed record ConstructorMember(
    string Name,
    string Type,
    bool IsReadOnly,
    bool IsNullable,
    string ParamName);
