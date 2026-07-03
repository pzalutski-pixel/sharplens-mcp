namespace SharpLensMcp.Tests.Fixtures;

// Drives find_references / find_callers with EXACT, controlled counts so the
// tests assert fixed numbers instead of inequalities against the live, evolving
// RoslynService source.
//
// Anchor is invoked exactly FIVE times, each from a distinct caller method, so:
//   - find_references on Anchor reports exactly 5 references (all invocations)
//   - find_callers on Anchor reports exactly 5 callers
//   - five exceeds a maxResults=3 cap, so the cap test can prove total > capped
public class ReferenceCountTarget
{
    public int Anchor() => 1;

    public int CallerOne() => Anchor();
    public int CallerTwo() => Anchor();
    public int CallerThree() => Anchor();
    public int CallerFour() => Anchor();
    public int CallerFive() => Anchor();
}
