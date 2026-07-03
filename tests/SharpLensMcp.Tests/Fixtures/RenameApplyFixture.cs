namespace SharpLensMcp.Tests.Fixtures;

// Drives rename_symbol's apply path (preview:false). The private method below is
// referenced only within this file, so applying a rename touches this fixture
// alone — the snapshot/restore test can safely revert it. The method name is not
// repeated in prose here, so a successful rename leaves no occurrence of it.
public class RenameApplyTarget
{
    public int Run() => RenameApplyOriginal() + RenameApplyOriginal();

    private int RenameApplyOriginal() => 21;
}
