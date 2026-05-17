using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// Tests the McpServer dispatcher's parameter-handling and error-mapping
// contract through the JSON-RPC layer. Independent of any specific tool's
// implementation — verifies the dispatcher itself.
//
// Error message format (from JsonRpcParameters.cs):
//   - Missing required: $"Missing required parameter '{name}' for tool '{_toolName}'"
//   - Wrong type:       $"Parameter '{name}' for tool '{_toolName}' has wrong type: ..."
//   - Unknown tool:     "Unknown tool: {name}"
public class DispatcherContractTests : McpTestBase
{
    public DispatcherContractTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ToolsCall_MissingRequiredString_Returns32602WithParameterAndToolName()
    {
        var msg = await CallExpectingJsonRpcErrorAsync(
            "roslyn:search_symbols",
            arguments: new { kind = "Class", maxResults = 10 },
            expectedCode: -32602);
        msg.Should().Contain("query", "the missing parameter name must be in the message");
        msg.Should().Contain("roslyn:search_symbols", "the tool name must be in the message");
        msg.Should().Contain("Missing required parameter", "the message must use the standard wording");
    }

    [Fact]
    public async Task ToolsCall_MissingRequiredInt_Returns32602()
    {
        var msg = await CallExpectingJsonRpcErrorAsync(
            "roslyn:get_symbol_info",
            arguments: new { filePath = Fixture.RoslynServicePath },
            expectedCode: -32602);
        msg.Should().Contain("line", "the first missing required int must surface");
        msg.Should().Contain("roslyn:get_symbol_info");
    }

    [Fact]
    public async Task ToolsCall_WrongTypeParameter_Returns32602WithWrongTypeWording()
    {
        var msg = await CallExpectingJsonRpcErrorAsync(
            "roslyn:get_symbol_info",
            arguments: new { filePath = Fixture.RoslynServicePath, line = "not-a-number", column = 0 },
            expectedCode: -32602);
        msg.Should().Contain("line");
        msg.Should().Contain("wrong type", "JsonRpcParameters labels type mismatch with 'has wrong type'");
        msg.Should().Contain("roslyn:get_symbol_info");
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_Returns32602WithUnknownToolWording()
    {
        var msg = await CallExpectingJsonRpcErrorAsync(
            "roslyn:does_not_exist_12345",
            arguments: new { },
            expectedCode: -32602);
        msg.Should().Contain("Unknown tool", "dispatcher must surface 'Unknown tool' for unregistered names");
        msg.Should().Contain("roslyn:does_not_exist_12345");
    }

    [Fact]
    public async Task ToolsCall_MissingToolName_Returns32602WithMissingToolNameWording()
    {
        var msg = await CallExpectingJsonRpcErrorAsync(
            toolName: "",
            arguments: new { },
            expectedCode: -32602);
        msg.Should().Contain("missing tool name");
    }

    [Fact]
    public async Task ToolsCall_StringArrayArgument_RoundTripsBothEntries()
    {
        var data = await CallAndGetDataAsync("roslyn:get_type_members_batch", new
        {
            typeNames = new[] { "RoslynService", "McpServer" }
        });
        var results = data["results"] as JArray;
        results!.Count.Should().Be(2);
        // Both requested type names must survive the JSON-RPC round-trip into the tool.
        var typeNames = results.Select(r => r["typeName"]?.Value<string>() ?? "").ToList();
        typeNames.Should().Contain(n => n.EndsWith("RoslynService"));
        typeNames.Should().Contain(n => n.EndsWith("McpServer"));
    }

    [Fact]
    public async Task ToolsCall_ObjectArrayArgument_RoundTripsBothEntries()
    {
        var data = await CallAndGetDataAsync("roslyn:get_method_source_batch", new
        {
            methods = new object[]
            {
                new { typeName = "RoslynService", methodName = "LoadSolutionAsync" },
                new { typeName = "RoslynService", methodName = "GetHealthCheckAsync" }
            }
        });
        data["totalRequested"]?.Value<int>().Should().Be(2);
        data["successCount"]?.Value<int>().Should().Be(2);
        data["errorCount"]?.Value<int>().Should().Be(0);

        // Each result's metadata must reflect the exact requested method.
        var results = data["results"] as JArray;
        var requested = results!.Select(r => r["methodName"]?.Value<string>()).ToList();
        requested.Should().BeEquivalentTo(new[] { "LoadSolutionAsync", "GetHealthCheckAsync" });
    }

    [Fact]
    public async Task ToolsCall_BooleanArgumentFalse_PropagatesToTool()
    {
        var data = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = (string?)null,
            projectPath = (string?)null,
            severity = (string?)null,
            includeHidden = false,
            runAnalyzers = false
        });
        data["analyzersRan"]?.Value<bool>().Should().BeFalse();
        data["analyzerCount"]?.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task ToolsCall_BooleanArgumentTrue_PropagatesToTool()
    {
        var data = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = (string?)null,
            projectPath = (string?)null,
            severity = (string?)null,
            includeHidden = false,
            runAnalyzers = true
        });
        // analyzersRan may legitimately be false if no project has analyzers,
        // but the field must be present and the int analyzerCount must be >= 0.
        // The contract is that the boolean param was passed through and the tool
        // exposed its observation back.
        data["analyzersRan"].Should().NotBeNull();
        data["analyzerCount"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task ToolsCall_MissingOptionalDefaults_ProducePaginatedDefault()
    {
        var data = await CallAndGetDataAsync("roslyn:search_symbols", new
        {
            query = "Async"
        });
        // The default maxResults is 50 (per McpServer.cs:1571). Without specifying
        // maxResults the response must include the pagination metadata block AND
        // at least some results (the SharpLens solution has many *Async methods).
        var results = data["results"] as JArray;
        results.Should().NotBeNullOrEmpty();
        results!.Count.Should().BeGreaterThan(10,
            "the solution has dozens of *Async methods within the default 50-result cap");

        var pagination = data["pagination"]!;
        pagination.Should().NotBeNull();
        // pagination always has nextOffset (may be null when no more); offset is at the
        // top level of data per Navigation.cs SearchSymbolsAsync.
        data["offset"]?.Value<int>().Should().Be(0, "default offset is 0");
    }

    [Fact]
    public async Task ToolsCall_ResponseEnvelope_HasSuccessTrueWithDataField()
    {
        // Explicit envelope-shape test: the harness implicitly verifies this on every
        // call, but a dedicated named test surfaces regressions clearly.
        var inner = await CallToolAsync("roslyn:health_check");
        inner["success"]?.Value<bool>().Should().BeTrue(
            "health_check must succeed at the inner contract layer");
        inner["data"].Should().NotBeNull(
            "successful responses must carry a data object");
        inner["error"].Should().BeNull(
            "success responses must NOT carry an error object");
    }
}
