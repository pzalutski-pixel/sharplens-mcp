namespace SharpLensMcp.Tests.Fixtures;

// Targets for move_type_to_file / split_type tests. The file deliberately
// holds several top-level types (move_type_to_file extracts one of them) and
// SplitCandidate carries a mix of keep/move members plus a nested type for
// the nested-type refusal path.

public class MoveCandidateAlpha
{
    public int AlphaValue { get; set; }
}

public class MoveCandidateBeta
{
    public string BetaName { get; set; } = "";
}

public class SplitCandidate
{
    public int KeepValue;

    public int KeepMe() => KeepValue;

    public string MoveMe1() => "one";

    public string MoveMe2() => "two";

    public class NestedHelper
    {
        public int HelperValue;
    }
}
