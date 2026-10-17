namespace SharpLensMcp;

// A validated change_signature request: the new parameter list as slots in their new
// order, and the existing parameters to rename (original ordinal -> new name).
internal sealed record SignaturePlan(IReadOnlyList<SignatureSlot> Slots, IReadOnlyDictionary<int, string> Renames);
