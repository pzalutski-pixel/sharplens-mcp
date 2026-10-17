using System.Text.Json;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Consistency;

// Cross-tool invariants: the same question through different tools gets the same answer.
//   - Argument shape: every array-of-object parameter of every published tool, found
//     from the schemas themselves, rejects a misshapen value with the identical -32602
//     wording, so no tool silently reads a wrong shape as an empty list.
//   - Identity: the coverage walk (find_tests) and the call graph (get_call_graph
//     callers) key symbols on the one cross-compilation identity, so across a
//     retargeting project boundary they agree on which tests reach a library method.
public class CrossToolConsistencyTests : CrossToolConsistencyTestBase
{
    // Every (tool, parameter, required) whose schema type is an array of objects.
    public static IEnumerable<object[]> ObjectArrayParameters()
    {
        var schemas = JArray.Parse(JsonSerializer.Serialize(ToolRegistry.BuildToolSchemas(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        foreach (var schema in schemas)
        {
            var inputSchema = schema["inputSchema"]!;
            var required = (inputSchema["required"] as JArray)?.Select(r => r.Value<string>()!).ToHashSet()
                ?? new HashSet<string>();
            foreach (var property in ((JObject)inputSchema["properties"]!).Properties())
            {
                if (property.Value["type"]?.Value<string>() == "array"
                    && property.Value["items"]?["type"]?.Value<string>() == "object")
                {
                    yield return new object[] { schema["name"]!.Value<string>()!, property.Name, required.Contains(property.Name) };
                }
            }
        }
    }

    [Fact]
    public void ObjectArrayParameters_AreTheKnownSet()
    {
        // Locks the discovery, so the shape invariants below cannot pass by finding nothing.
        ObjectArrayParameters().Select(p => $"{p[0]}.{p[1]}:{((bool)p[2] ? "required" : "optional")}")
            .Should().Equal(
                "get_method_source_batch.methods:required",
                "change_signature.changes:required",
                "find_path_between.sources:required",
                "find_path_between.targets:required",
                "find_path_between.avoidThrough:optional",
                "find_path_between.mustPassThrough:optional",
                "check_architecture.rules:required");
    }

    // Arguments where every OTHER required parameter of the tool is present with a
    // well-typed placeholder, so the parameter under test is the only shape violation.
    private static Dictionary<string, object?> PlaceholderArguments(string tool, string except)
    {
        var schema = JArray.Parse(JsonSerializer.Serialize(ToolRegistry.BuildToolSchemas(),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))
            .Single(s => s["name"]!.Value<string>() == tool)["inputSchema"]!;
        var arguments = new Dictionary<string, object?>();
        foreach (var name in (schema["required"] as JArray)?.Select(r => r.Value<string>()!) ?? Enumerable.Empty<string>())
        {
            if (name == except) continue;
            arguments[name] = schema["properties"]![name]!["type"]!.Value<string>() switch
            {
                "array" => Array.Empty<object>(),
                "integer" => 0,
                "boolean" => false,
                _ => "placeholder"
            };
        }
        return arguments;
    }

    [Theory]
    [MemberData(nameof(ObjectArrayParameters))]
    public async Task EveryObjectArrayParameter_RejectsAnObject_WithTheSameWording(string tool, string parameter, bool required)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode("namespace Probe { public class P { } }", "P.cs");
        Load(workspace);
        var arguments = PlaceholderArguments(tool, except: parameter);
        arguments[parameter] = new { typeName = "P" };

        var message = await CallInvalidParamsAsync(tool, arguments);

        message.Should().Be($"Parameter '{parameter}' for tool '{tool}' must be an array of objects",
            required ? "a required array" : "an optional array");
    }

    [Theory]
    [MemberData(nameof(ObjectArrayParameters))]
    public async Task EveryObjectArrayParameter_RejectsANonObjectElement_WithTheSameWording(string tool, string parameter, bool required)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode("namespace Probe { public class P { } }", "P.cs");
        Load(workspace);
        var arguments = PlaceholderArguments(tool, except: parameter);
        arguments[parameter] = new object[] { 42 };

        var message = await CallInvalidParamsAsync(tool, arguments);

        message.Should().Be($"Parameter '{parameter}[0]' for tool '{tool}' must be an object",
            required ? "a required array" : "an optional array");
    }

    public static IEnumerable<object[]> RequiredObjectArrayParameters() =>
        ObjectArrayParameters().Where(p => (bool)p[2]).Select(p => new[] { p[0], p[1] });

    [Theory]
    [MemberData(nameof(RequiredObjectArrayParameters))]
    public async Task EveryRequiredObjectArrayParameter_RejectsItsAbsence_WithTheSameWording(string tool, string parameter)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode("namespace Probe { public class P { } }", "P.cs");
        Load(workspace);
        var arguments = PlaceholderArguments(tool, except: parameter);

        var message = await CallInvalidParamsAsync(tool, arguments);

        message.Should().Be($"Missing required array parameter '{parameter}' for tool '{tool}'");
    }

    private const string Lib = """
        namespace Lib
        {
            public class Calculator
            {
                public int Chain() => Helper();
                public int Helper() => Leaf();
                public int Leaf() => 1;
                public int Never() => 0;
            }
        }
        """;

    private const string Tests = """
        namespace Xunit
        {
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public class FactAttribute : System.Attribute { }
        }

        namespace Lib.Tests
        {
            public class CalculatorTests
            {
                [Xunit.Fact] public void ChainTest() { _ = new Lib.Calculator().Chain(); }
                [Xunit.Fact] public void HelperTest() { _ = new Lib.Calculator().Helper(); }
                [Xunit.Fact] public void NeverTest() { _ = new Lib.Calculator().Never(); }
            }
        }
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindTests_And_CallGraphCallers_AgreeOnTheTestsReachingAMethod(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(Lib, Tests, retarget);
        Load(fixture.Workspace);
        var (line, column) = RetargetedSources.Locate(Lib, "Helper() => Leaf()");

        var coverage = await CallDataAsync("find_tests", new { typeName = "Lib.Calculator", memberName = "Helper" });
        var graph = await CallDataAsync("get_call_graph", new
        {
            filePath = fixture.LibFilePath, line, column, direction = "callers", maxDepth = 3, maxNodes = 100
        });

        var coveringTests = Names((JArray)coverage["coveringTests"]!,
            t => $"{t["containingType"]!.Value<string>()}.{t["testMethod"]!.Value<string>()}");
        var testCallers = Names(((JArray)graph["nodes"]!)
                .Where(n => n["fullName"]!.Value<string>()!.StartsWith("Lib.Tests.CalculatorTests.", StringComparison.Ordinal)),
            n => n["fullName"]!.Value<string>()!.Replace("()", ""));

        coveringTests.Should().Equal("Lib.Tests.CalculatorTests.ChainTest", "Lib.Tests.CalculatorTests.HelperTest");
        testCallers.Should().Equal(coveringTests,
            "the coverage walk and the call graph must agree on which tests reach Helper");
    }
}
