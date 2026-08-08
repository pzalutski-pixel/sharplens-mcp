namespace SharpLensMcp;

/// <summary>
/// A method endpoint addressed by type name plus member name — the addressing
/// find_path_between uses for its sources, targets, and barriers. A member name
/// may resolve to several overloads; all of them are treated as the endpoint.
/// </summary>
public sealed record MemberRef(string TypeName, string MemberName);
