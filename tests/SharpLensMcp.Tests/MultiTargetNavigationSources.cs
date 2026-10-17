namespace SharpLensMcp.Tests;

// Sources for MultiTargetSolutionFixture that put one declaration of every kind the
// navigation tools report into the multi-targeted project, so each tool sees it once per
// target framework: Multi declares Greeter (attributed, with a reflection call, a typeof,
// factories and an extension method) deriving from Base and implementing IThing;
// Consumer (single-target, referencing Multi(net10.0)) implements IThing, derives from
// Base, and references Greeter directly, through a using alias, and from a factory.
internal static class MultiTargetNavigationSources
{
    public const string Multi = """
        using System;

        namespace Multi
        {
            [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
            public sealed class TagAttribute : Attribute { }

            public interface IThing { void Go(); }

            public abstract class Base { public abstract void Run(); }

            [Tag]
            public class Greeter : Base, IThing
            {
                public override void Run() { }
                public void Go() { }
                public string Greet(string name) => Format(name);
                public string Format(string name) => "Hello " + name;
                public Type Kind() => typeof(Greeter);
                public static Greeter Create() => new Greeter();
                public object Reflect() => typeof(Greeter).GetMethod("Go");
            }

            public static class GreeterFactory
            {
                public static Greeter NewGreeter() => new Greeter();
            }

            public static class GreeterExt
            {
                public static void Poke(this Greeter greeter) { }
            }
        }
        """;

    public const string Consumer = """
        namespace Consumer
        {
            using W = Multi.Greeter;

            public class App : Multi.IThing
            {
                public void Go() { }
                public string Run(W w) => w.Greet("app");
            }

            public class Concrete : Multi.Base
            {
                public override void Run() { }
            }

            public static class Factory
            {
                public static Multi.Greeter CreateGreeter() => new Multi.Greeter();
            }
        }
        """;

    // The (zero-based line, column) of `token` on the first line of `source` that
    // contains `lineMarker`.
    public static (int line, int column) Pos(string source, string lineMarker, string token)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var line = Array.FindIndex(lines, l => l.Contains(lineMarker, StringComparison.Ordinal));
        if (line < 0) throw new ArgumentException($"no line contains '{lineMarker}'", nameof(lineMarker));
        var column = lines[line].IndexOf(token, StringComparison.Ordinal);
        if (column < 0) throw new ArgumentException($"line {line} has no '{token}'", nameof(token));
        return (line, column);
    }
}
