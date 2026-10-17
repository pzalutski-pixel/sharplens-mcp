namespace SharpLensMcp;

// How a call supplies one parameter (see BoundArgument).
internal enum BoundArgumentKind
{
    // An argument written in the argument list, positional or named.
    Explicit,

    // The surplus arguments of a params parameter called in expanded form (none or more).
    Expanded,

    // No argument: the parameter's default value (or caller information) applies.
    Omitted,

    // The receiver of a reduced-form extension call (`x.M()`): outside the argument list.
    Receiver
}
