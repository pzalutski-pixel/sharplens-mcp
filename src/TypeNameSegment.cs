namespace SharpLensMcp;

// One dotted segment of a by-name type reference: a namespace or type name, plus
// the generic arity when the caller spelled one — `Repo<T>`, `Repo<int>` and
// `Repo`1` all carry arity 1; a bare `Repo` carries null (any arity).
internal sealed record TypeNameSegment(string Name, int? Arity);
