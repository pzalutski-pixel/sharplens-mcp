using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One application of an attribute in source (RoslynService.FindAttributeApplicationsAsync):
// the symbol it is reported on, its C# attribute target ("assembly", "module", "type",
// "method", "field", "property", "event", "param", "return", "typevar"), the bound
// attribute, and where the attribute is written. An attribute applied to a compiler-made
// symbol is reported on the declaration that made it: [field:] on an auto-property or a
// field-like event on that property or event.
internal sealed record AttributeApplication(ISymbol Owner, string Target, AttributeData Attribute, Location AttributeLocation);
