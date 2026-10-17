namespace SharpLensMcp;

// How many entry points of each kind the coverage walk started from.
internal sealed record EntryPointCounts(int Tests, int StepBindings, int Hooks);
