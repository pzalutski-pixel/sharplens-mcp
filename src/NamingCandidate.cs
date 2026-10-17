using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One named symbol find_naming_violations checks: how it is reported (Display), what a
// naming rule matches against (the editorconfig symbol kind, the name without an '@'
// escape, the accessibility value and the modifiers), and where its name is.
internal sealed record NamingCandidate(
    string Display, string Kind, string Name, string Accessibility, IReadOnlySet<string> Modifiers, Location Location);
