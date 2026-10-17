namespace SharpLensMcp;

// One reference a body makes in the coverage graph: the node it reaches, and whether the
// reference is a virtual or interface dispatch (so the member that actually runs may be
// any override or implementation of it).
internal readonly record struct ReachableCallee(ReachableNode Node, bool ViaDispatch);
