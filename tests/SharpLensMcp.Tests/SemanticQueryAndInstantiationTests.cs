using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// semantic_query type matching is whole-word (int ≠ uint, Task ≠ TaskCompletionSource),
// and get_instantiation_options.hasBuilder resolves a real {Type}Builder type, not a
// filename substring.
public class SemanticQueryAndInstantiationTests
{
    private static RoslynService Load(string code, string fileName = "Probe.cs")
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, fileName);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    private static async Task<List<string>> QueryMethodNamesByReturnType(RoslynService service, string returnType)
    {
        var result = await service.SemanticQueryAsync(
            kinds: new List<string> { "Method" },
            isAsync: null, namespaceFilter: null, accessibility: null, isStatic: null,
            type: null, returnType: returnType, attributes: null,
            parameterIncludes: null, parameterExcludes: null, maxResults: null);
        return (JObject.FromObject(result)["data"]!["results"] as JArray)!
            .Select(r => r["name"]!.Value<string>()!)
            .ToList();
    }

    [Fact]
    public async Task SemanticQuery_ReturnType_MatchesWholeWord_NotSubstring()
    {
        const string code = """
            using System.Threading.Tasks;
            namespace QProbe
            {
                public class QHost
                {
                    public int ReturnsInt() => 0;
                    public uint ReturnsUint() => 0;
                    public Task ReturnsTask() => null!;
                    public TaskCompletionSource ReturnsTcs() => null!;
                }
            }
            """;
        var service = Load(code);

        var intMatches = await QueryMethodNamesByReturnType(service, "int");
        intMatches.Should().Contain("ReturnsInt");
        intMatches.Should().NotContain("ReturnsUint", "`int` must not match the substring inside `uint`");

        var taskMatches = await QueryMethodNamesByReturnType(service, "Task");
        taskMatches.Should().Contain("ReturnsTask");
        taskMatches.Should().NotContain("ReturnsTcs", "`Task` must not match `TaskCompletionSource`");
    }

    [Fact]
    public async Task GetInstantiationOptions_HasBuilder_RequiresARealBuilderType()
    {
        const string withBuilder = """
            namespace BProbe
            {
                public class Widget { public Widget() { } }
                public class WidgetBuilder { public Widget Build() => new Widget(); }
            }
            """;
        var present = JObject.FromObject(await Load(withBuilder).GetInstantiationOptionsAsync("Widget"))["data"]!;
        present["hasBuilder"]!.Value<bool>().Should().BeTrue("a WidgetBuilder type exists in source");

        const string withoutBuilder = """
            namespace BProbe
            {
                public class Widget { public Widget() { } }
            }
            """;
        var absent = JObject.FromObject(await Load(withoutBuilder).GetInstantiationOptionsAsync("Widget"))["data"]!;
        absent["hasBuilder"]!.Value<bool>().Should().BeFalse("no WidgetBuilder type exists");
    }
}
