namespace SharpLensMcp;

// One named documentation entry: a <param>/<typeparam> (Name = parameter name)
// or an <exception> (Name = exception type).
internal sealed record DocEntry(string Name, string Text);
