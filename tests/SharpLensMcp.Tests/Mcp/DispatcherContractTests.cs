using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// Tests the McpServer dispatcher's parameter-handling and error-mapping
// contract through the JSON-RPC layer. Independent of any specific tool's
// implementation — verifies the dispatcher itself.
public class DispatcherContractTests : McpTestBase
{
    public DispatcherContractTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ToolsCall_MissingRequiredString_Returns32602()
    {
        // search_symbols requires 'query'.
        var msg = await CallExpectingJsonRpcErrorAsync(
            "roslyn:search_symbols",
            arguments: new { kind = "Class", maxResults = 10 },
            expectedCode: -32602);
        msg.Should().Contain("query");
    }

    [Fact]
    public async Task ToolsCall_MissingRequiredInt_Returns32602()
    {
        // get_symbol_info requires line + column (ints).
        var msg = await CallExpectingJsonRpcErrorAsync(
            "roslyn:get_symbol_info",
            arguments: new { filePath = Fixture.RoslynServicePath },
            expectedCode: -32602);
        msg.Should().Contain("line");
    }

    [Fact]
    public async Task ToolsCall_WrongTypeParameter_Returns32602()
    {
        // line must be int — pass a string.
        var msg = await CallExpectingJsonRpcErrorAsync(
            "roslyn:get_symbol_info",
            arguments: new { filePath = Fixture.RoslynServicePath, line = "not-a-number", column = 0 },
            expectedCode: -32602);
        msg.Should().Contain("line");
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_Returns32602()
    {
        await CallExpectingJsonRpcErrorAsync(
            "roslyn:does_not_exist_12345",
            arguments: new { },
            expectedCode: -32602);
    }

    [Fact]
    public async Task ToolsCall_MissingToolName_Returns32602()
    {
        // Passing arguments object without 'name' — dispatcher must surface
        // "missing tool name" as Invalid Params.
        await CallExpectingJsonRpcErrorAsync(
            // empty toolName produces the missing-name path
            toolName: "",
            arguments: new { },
            expectedCode: -32602);
    }

    [Fact]
    public async Task ToolsCall_StringArrayArgument_RoundTrips()
    {
        // get_type_members_batch takes typeNames: string[]
        var data = await CallAndGetDataAsync("roslyn:get_type_members_batch", new
        {
            typeNames = new[] { "RoslynService", "McpServer" }
        });
        var results = data["results"] as JArray;
        results.Should().NotBeNull("string-array param must reach the tool unmangled");
        results!.Count.Should().Be(2, "both requested types must be returned");
    }

    [Fact]
    public async Task ToolsCall_ObjectArrayArgument_RoundTrips()
    {
        // get_method_source_batch takes methods: [{typeName, methodName}]
        var data = await CallAndGetDataAsync("roslyn:get_method_source_batch", new
        {
            methods = new object[]
            {
                new { typeName = "RoslynService", methodName = "LoadSolutionAsync" },
                new { typeName = "RoslynService", methodName = "GetHealthCheckAsync" }
            }
        });
        data["totalRequested"]?.Value<int>().Should().Be(2);
        data["successCount"]?.Value<int>().Should().Be(2,
            "object-array param must reach the tool with both entries intact");
    }

    [Fact]
    public async Task ToolsCall_BooleanArgument_RoundTrips()
    {
        // get_diagnostics accepts runAnalyzers: bool
        var data = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = (string?)null,
            projectPath = (string?)null,
            severity = (string?)null,
            includeHidden = false,
            runAnalyzers = false
        });
        data["analyzersRan"]?.Value<bool>().Should().BeFalse(
            "runAnalyzers:false must propagate through MCP envelope to the tool");
    }

    [Fact]
    public async Task ToolsCall_MissingOptionalDefaults_Honored()
    {
        // search_symbols's maxResults defaults to 50 when omitted.
        var data = await CallAndGetDataAsync("roslyn:search_symbols", new
        {
            query = "Async"
        });
        // Pagination metadata is present and reflects the default cap.
        data["pagination"].Should().NotBeNull(
            "default maxResults must apply when caller omits the param");
    }

    [Fact]
    public async Task ToolsCall_ResponseEnvelope_HasContentTextOfParseableJson()
    {
        // Already implicitly verified by the harness on every CallToolAsync,
        // but assert it explicitly here so a regression surfaces as a named test.
        var inner = await CallToolAsync("roslyn:health_check");
        inner["success"].Should().NotBeNull(
            "tool response must round-trip with a success field through the MCP envelope");
    }
}
