using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One call site change_signature found, once per source location (a multi-targeted
// file's site is bound once per target framework): where it is, its argument list as
// written, the annotation marking its rebuilt call in the edited document (null when
// the argument list stays as written), and whether the change edits it (its argument
// list, or a named argument a parameter rename relabels).
internal sealed record SignatureCallSiteRecord(
    string FilePath, int Line, int Column, DocumentId DocumentId, string Before, SyntaxAnnotation? After, bool Changed);
