using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// The strict-shape contract of the array-of-object tool arguments, through the
// JSON-RPC dispatcher: a value of the wrong shape (an object where an array is
// expected, a non-object element, a missing required array or required element
// field) is a -32602 invalid-params error naming the tool and the parameter path —
// never silently read as an empty list. Each call runs on a fresh server loaded with
// a small in-memory solution, so a correctly shaped call has an exact known answer.
public class NestedArgumentShapeViaMcpTests
{
    private const string Code = """
        namespace Shapes
        {
            public class Chain
            {
                public void A() { B(); }
                public void B() { C(); }
                public void C() { }
                public int Add(int amount) => amount + 1;
            }
        }
        """;

    private static McpHarnessOps Load()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Chain.cs");
        var server = new McpServer();
        server.LoadFromWorkspaceForTesting(workspace);
        return new McpHarnessOps(server);
    }

    private static async Task<string> InvalidParamsMessageAsync(string tool, object arguments)
    {
        try
        {
            var inner = await Load().CallToolAsync(tool, arguments);
            throw new Xunit.Sdk.XunitException($"Expected JSON-RPC -32602 from '{tool}' but the tool returned: {inner}");
        }
        catch (McpJsonRpcException ex)
        {
            ex.Code.Should().Be(-32602, $"a wrong argument shape is invalid params; envelope: {ex.Envelope}");
            var message = ex.Envelope["error"]!["message"]!.Value<string>()!;
            message.Should().Contain($"'{tool}'", "every -32602 names the tool");
            return message;
        }
    }

    private static object Endpoint(string typeName, string memberName) => new { typeName, memberName };

    [Fact]
    public async Task FindPathBetween_WellShapedBarrier_CutsTheOnlyPath()
    {
        // Control: the array-shaped barrier is honored, so the object-shaped cases below
        // must not be read as "no barrier".
        var inner = await Load().CallToolAsync("find_path_between", new
        {
            sources = new[] { Endpoint("Chain", "A") },
            targets = new[] { Endpoint("Chain", "C") },
            avoidThrough = new[] { Endpoint("Chain", "B") }
        });

        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        ((JArray)inner["data"]!["pairs"]!)[0]["reachable"]!.Value<bool>().Should().BeFalse(
            "B is the only bridge from A to C");
    }

    [Fact]
    public async Task FindPathBetween_ObjectBarrier_IsInvalidParams()
    {
        var message = await InvalidParamsMessageAsync("find_path_between", new
        {
            sources = new[] { Endpoint("Chain", "A") },
            targets = new[] { Endpoint("Chain", "C") },
            avoidThrough = Endpoint("Chain", "B")
        });

        message.Should().Be("Parameter 'avoidThrough' for tool 'find_path_between' must be an array of objects");
    }

    [Fact]
    public async Task FindPathBetween_ObjectCheckpoint_IsInvalidParams()
    {
        var message = await InvalidParamsMessageAsync("find_path_between", new
        {
            sources = new[] { Endpoint("Chain", "A") },
            targets = new[] { Endpoint("Chain", "C") },
            mustPassThrough = Endpoint("Chain", "B")
        });

        message.Should().Be("Parameter 'mustPassThrough' for tool 'find_path_between' must be an array of objects");
    }

    [Fact]
    public async Task FindPathBetween_MissingSources_IsInvalidParams()
    {
        var message = await InvalidParamsMessageAsync("find_path_between", new
        {
            targets = new[] { Endpoint("Chain", "C") }
        });

        message.Should().Be("Missing required array parameter 'sources' for tool 'find_path_between'");
    }

    [Fact]
    public async Task FindPathBetween_StringElement_IsInvalidParams()
    {
        var message = await InvalidParamsMessageAsync("find_path_between", new
        {
            sources = new object[] { "Chain.A" },
            targets = new[] { Endpoint("Chain", "C") }
        });

        message.Should().Be("Parameter 'sources[0]' for tool 'find_path_between' must be an object");
    }

    [Fact]
    public async Task FindPathBetween_ElementWithoutMemberName_IsInvalidParams()
    {
        var message = await InvalidParamsMessageAsync("find_path_between", new
        {
            sources = new[] { Endpoint("Chain", "A") },
            targets = new object[] { Endpoint("Chain", "C"), new { typeName = "Chain" } }
        });

        message.Should().Be("Missing required parameter 'targets[1].memberName' for tool 'find_path_between'");
    }

    [Fact]
    public async Task ChangeSignature_MissingChanges_IsInvalidParams()
    {
        var message = await InvalidParamsMessageAsync("change_signature", new
        {
            typeName = "Chain",
            memberName = "Add"
        });

        message.Should().Be("Missing required array parameter 'changes' for tool 'change_signature'");
    }

    [Fact]
    public async Task ChangeSignature_ObjectChanges_IsInvalidParams()
    {
        var message = await InvalidParamsMessageAsync("change_signature", new
        {
            typeName = "Chain",
            memberName = "Add",
            changes = new { action = "rename", name = "amount", newName = "value" }
        });

        message.Should().Be("Parameter 'changes' for tool 'change_signature' must be an array of objects");
    }

    [Fact]
    public async Task ChangeSignature_NullOrderElement_IsInvalidParams()
    {
        var message = await InvalidParamsMessageAsync("change_signature", new
        {
            typeName = "Chain",
            memberName = "Add",
            changes = new object[] { new { action = "reorder", order = new string?[] { null } } }
        });

        message.Should().Be("Parameter 'changes[0].order[0]' for tool 'change_signature' must be a string");
    }

    [Fact]
    public async Task CheckArchitecture_ObjectRules_IsInvalidParams()
    {
        var message = await InvalidParamsMessageAsync("check_architecture", new
        {
            rules = new { from = "Shapes*", deny = "System*" }
        });

        message.Should().Be("Parameter 'rules' for tool 'check_architecture' must be an array of objects");
    }

    [Fact]
    public async Task CheckArchitecture_MissingRules_IsInvalidParams()
    {
        var message = await InvalidParamsMessageAsync("check_architecture", new { level = "namespace" });

        message.Should().Be("Missing required array parameter 'rules' for tool 'check_architecture'");
    }

    [Fact]
    public async Task GetMethodSourceBatch_ObjectMethods_IsInvalidParams()
    {
        var message = await InvalidParamsMessageAsync("get_method_source_batch", new
        {
            methods = new { typeName = "Chain", methodName = "A" }
        });

        message.Should().Be("Parameter 'methods' for tool 'get_method_source_batch' must be an array of objects");
    }

    [Fact]
    public async Task GetMethodSourceBatch_ElementWithoutMethodName_IsInvalidParams()
    {
        var message = await InvalidParamsMessageAsync("get_method_source_batch", new
        {
            methods = new object[] { new { typeName = "Chain" } }
        });

        message.Should().Be("Missing required parameter 'methods[0].methodName' for tool 'get_method_source_batch'");
    }
}
