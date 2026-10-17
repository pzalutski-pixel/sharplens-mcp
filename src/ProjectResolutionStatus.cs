namespace SharpLensMcp;

// The outcome of resolving a user-supplied project name (LogicalProjectModel.Resolve).
internal enum ProjectResolutionStatus
{
    // The name addressed exactly one project scope.
    Resolved,

    // No project answers to the name.
    NotFound,

    // The name addresses several distinct projects and no exact-case match settles it.
    Ambiguous
}
