using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// The outcome of organizing one file's usings (RoslynService.ComputeOrganizedUsingsAsync):
// the rewritten compilation unit, how many usings the file had and how many were removed,
// whether the text changed, and whether sorting was skipped because preprocessor
// directives run through the using block. Error is set instead when the file cannot be
// organized; the other members are then meaningless.
internal sealed record OrganizedUsings(
    CompilationUnitSyntax? NewRoot,
    int TotalUsings,
    int RemovedUsings,
    bool Changed,
    bool SortSkippedForDirectives,
    string? Error);
