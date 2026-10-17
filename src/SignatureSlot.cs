namespace SharpLensMcp;

// One parameter of a changed signature, in its new position: an existing parameter
// (OldOrdinal, its position in the original parameter list) or a parameter the change
// adds (OldOrdinal -1, with the added name, type and default-value text as given).
internal sealed record SignatureSlot(int OldOrdinal, string? AddedName = null, string? AddedType = null, string? AddedDefault = null)
{
    public bool IsAdded => OldOrdinal < 0;
}
