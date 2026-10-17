namespace SharpLensMcp.Tests;

// The default sources MultiTargetSolutionFixture loads. Multi.Greeter.Greet calls Format,
// so each target framework's compilation binds that call site once (the same file and
// span twice); Modern exists only under NET10_0_OR_GREATER and calls Format once more,
// in the net10.0 compilation alone. Consumer.App.Run calls Greet through the net10.0
// project reference.
internal static class MultiTargetSources
{
    public const string MultiFileName = "Greeter.cs";
    public const string ConsumerFileName = "App.cs";

    public const string Multi = """
        namespace Multi
        {
            public class Greeter
            {
                public string Greet(string name) => Format(name);
                public string Format(string name) => "Hello " + name;
        #if NET10_0_OR_GREATER
                public string Modern() => Format("net10");
        #endif
            }
        }
        """;

    public const string Consumer = """
        namespace Consumer
        {
            public class App
            {
                public string Run() => new Multi.Greeter().Greet("app");
            }
        }
        """;
}
