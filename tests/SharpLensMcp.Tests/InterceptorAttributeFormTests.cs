using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_interceptors on the spellings an [InterceptsLocation] attribute takes, at both
// levels (ToolPath): an alias-qualified name (global::InterceptsLocation) and the legacy
// form with named arguments in any order.
public class InterceptorAttributeFormTests
{
    internal const string Source = """
        [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
        public sealed class InterceptsLocationAttribute : System.Attribute
        {
            public InterceptsLocationAttribute(string filePath, int line, int character) { }
        }

        namespace Demo
        {
            public static class Interceptors
            {
                [global::InterceptsLocation("a.cs", 3, 5)]
                public static void Aliased() { }

                [InterceptsLocation(line: 7, character: 9, filePath: "b.cs")]
                public static void Named() { }
            }
        }
        """;

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task AliasQualifiedAndNamedArgumentForms_AreFoundWithTheirTargets(ToolPath toolPath)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Source, "Interceptors.cs");
        var runner = new ToolPathRunner(toolPath, workspace);

        var data = await runner.DataAsync("find_interceptors", new { }, s => s.FindInterceptorsAsync());

        ((JArray)data["interceptors"]!)
            .Select(i => (
                i["interceptorMethod"]!.Value<string>(),
                i["target"]!["form"]!.Value<string>(),
                i["target"]!["file"]!.Value<string>(),
                i["target"]!["line"]!.Value<int>(),
                i["target"]!["column"]!.Value<int>()))
            .Should().Equal(
                ("Interceptors.Aliased", "legacy", "a.cs", 2, 4),
                ("Interceptors.Named", "legacy", "b.cs", 6, 8));
    }
}
