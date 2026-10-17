namespace SharpLensMcp.Tests;

// The Lib / Lib.Tests sources the retargeted cross-compilation tests load into
// RetargetedSolutionFixture. Lib.Tests reaches every Lib declaration from its own
// bodies (a call, an override, an extension method), and Lib.Calculator.Add calls
// Helper inside Lib, so Helper is bound once in each project's compilation.
internal static class RetargetedSources
{
    public const string Lib = """
        namespace Lib
        {
            public interface IShape { int Area(); }
            public abstract class ShapeBase : IShape { public abstract int Area(); }
            public class Calculator
            {
                public int Add(int a, int b) => Helper(a) + b;
                public int Helper(int x) => x;
            }
        }
        """;

    public const string Tests = """
        namespace Lib.Tests
        {
            public class Square : Lib.ShapeBase { public override int Area() => 4; }
            public static class CalculatorExtensions { public static int Twice(this Lib.Calculator c, int x) => c.Add(x, x); }
            public class Uses
            {
                public int Call()
                {
                    var c = new Lib.Calculator();
                    return c.Add(1, 2) + c.Helper(3);
                }
            }
        }
        """;

    // Zero-based (line, column) of the first occurrence of marker in source.
    public static (int line, int column) Locate(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) throw new ArgumentException($"marker '{marker}' not found", nameof(marker));
        var before = source[..index];
        return (before.Count(c => c == '\n'), index - (before.LastIndexOf('\n') + 1));
    }
}
