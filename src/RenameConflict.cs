using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// One location the Renamer could not keep meaning the same after a rename: a
// declaration the new name collides with, or a reference that would bind to something
// else. Span is in the document as it was BEFORE the rename. Kind is "unresolved" (a
// reference conflict the Renamer could not fix), "unresolvable" (a declaration
// conflict no rewrite can fix) or "possiblyResolvable".
internal sealed record RenameConflict(DocumentId DocumentId, TextSpan Span, string Kind);
