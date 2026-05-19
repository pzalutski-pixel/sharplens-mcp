using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// Tests the McpServer dispatcher's parameter-handling and error-mapping
// contract through the JSON-RPC layer. Independent of any specific tool's
// implementation — verifies the dispatcher itself.
//
// Error message format (from src/JsonRpcParameters.cs):
//   - Missing required scalar: $"Missing required parameter '{name}' for tool '{_toolName}'"
//   - Missing required array:  $"Missing required array parameter '{name}' for tool '{_toolName}'"
//   - Wrong type:              $"Parameter '{name}' for tool '{_toolName}' has wrong type: ..."
//   - Non-array given to array: $"Parameter '{name}' for tool '{_toolName}' must be an array of strings"
//   - Unknown tool:            "Unknown tool: {name}"
//   - Missing tool name:       "Invalid params: missing tool name"
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
    public async Task ToolsCall_MissingRequiredInt_Returns32602WithStandardWording()
    {
        var msg = await CallExpectingJsonRpcErrorAsync(
            "roslyn:get_symbol_info",
            arguments: new { filePath = Fixture.RoslynServicePath },
            expectedCode: -32602);
        msg.Should().Contain("line", "the first missing required int must surface");
        msg.Should().Contain("roslyn:get_symbol_info");
        msg.Should().Contain("Missing required parameter",
            "the int-missing branch must use the same wording as the string-missing branch (JsonRpcParameters.cs:26)");
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
    public async Task ToolsCall_MissingToolName_Returns32602WithInvalidParamsWording()
    {
        var msg = await CallExpectingJsonRpcErrorAsync(
            toolName: "",
            arguments: new { },
            expectedCode: -32602);
        msg.Should().Contain("Invalid params",
            "the dispatcher must use the standard 'Invalid params' prefix (McpServer.cs:1551)");
        msg.Should().Contain("missing tool name");
    }

    [Fact]
    public async Task ToolsCall_MissingRequiredStringArray_Returns32602WithArrayWording()
    {
        // get_type_members_batch's typeNames is RequiredStringArray (McpServer.cs:1878,
        // JsonRpcParameters.cs:103-108). Missing the field must surface the array-
        // specific wording — distinct from the scalar 'Missing required parameter' one.
        var msg = await CallExpectingJsonRpcErrorAsync(
            "roslyn:get_type_members_batch",
            arguments: new { },
            expectedCode: -32602);
        msg.Should().Contain("Missing required array parameter",
            "RequiredStringArray uses a distinct error wording from scalar Required");
        msg.Should().Contain("typeNames");
        msg.Should().Contain("roslyn:get_type_members_batch");
    }

    [Fact]
    public async Task ToolsCall_OptionalStringArrayWithNonArrayValue_Returns32602WithArrayWording()
    {
        // semantic_query's "kinds" is OptionalStringArray (McpServer.cs:1603). Passing
        // a scalar where an array is expected must surface "must be an array of strings"
        // (JsonRpcParameters.cs:99-100), not the generic 'has wrong type' wording.
        var msg = await CallExpectingJsonRpcErrorAsync(
            "roslyn:semantic_query",
            arguments: new { kinds = "Class" },
            expectedCode: -32602);
        msg.Should().Contain("kinds");
        msg.Should().Contain("must be an array of strings",
            "the OptionalStringArray branch must surface its own wording (JsonRpcParameters.cs:99-100)");
        msg.Should().Contain("roslyn:semantic_query");
    }

    [Fact]
    public async Task ToolsCall_StringArrayArgument_RoundTripsBothEntriesWithBatchMetadata()
    {
        var data = await CallAndGetDataAsync("roslyn:get_type_members_batch", new
        {
            typeNames = new[] { "RoslynService", "McpServer" }
        });
        // Lock the batch-tool metadata (TypeDiscovery.cs:169-171) — totalRequested,
        // successCount, errorCount must match the request shape.
        data["totalRequested"]!.Value<int>().Should().Be(2);
        data["successCount"]!.Value<int>().Should().Be(2,
            "both requested type names must resolve in the SharpLensMcp solution");
        data["errorCount"]!.Value<int>().Should().Be(0);

        var results = (data["results"] as JArray)!;
        results.Count.Should().Be(2);
        // Tightened from `r["typeName"]?.Value<string>() ?? ""` — a missing field
        // now NREs instead of silently being replaced by empty string.
        var typeNames = results.Select(r => r["typeName"]!.Value<string>()!).ToList();
        typeNames.Should().Contain(n => n.EndsWith("RoslynService"));
        typeNames.Should().Contain(n => n.EndsWith("McpServer"));
    }

    [Fact]
    public async Task ToolsCall_ObjectArrayArgument_RoundTripsBothEntriesWithBatchMetadata()
    {
        var data = await CallAndGetDataAsync("roslyn:get_method_source_batch", new
        {
            methods = new object[]
            {
                new { typeName = "RoslynService", methodName = "LoadSolutionAsync" },
                new { typeName = "RoslynService", methodName = "GetHealthCheckAsync" }
            }
        });
        // Lock the batch-tool metadata (Inspection.cs:1489-1491). NotBeNull-first is
        // redundant after the `!` bang — the bang throws on missing, so the next
        // assertion is the actual contract lock.
        data["totalRequested"]!.Value<int>().Should().Be(2);
        data["successCount"]!.Value<int>().Should().Be(2);
        data["errorCount"]!.Value<int>().Should().Be(0);

        // Each result envelope reflects the exact requested (typeName, methodName).
        var results = (data["results"] as JArray)!;
        var requested = results.Select(r => r["methodName"]!.Value<string>()!).ToList();
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
        data["analyzersRan"]!.Value<bool>().Should().BeFalse(
            "runAnalyzers=false must propagate to analyzersRan=false");
        data["analyzerCount"]!.Value<int>().Should().Be(0,
            "with analyzers skipped, the count must be zero");
    }

    [Fact]
    public async Task ToolsCall_BooleanArgumentTrue_PropagatesToToolAndRunsAnalyzers()
    {
        var data = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = (string?)null,
            projectPath = (string?)null,
            severity = (string?)null,
            includeHidden = false,
            runAnalyzers = true
        });
        // The SharpLens solution loads SharpLensMcp.Tests.TestAnalyzers, so analyzers
        // ARE available. Lock the affirmative contract instead of branching on the
        // returned values — the prior `if (ran) ... else ...` form was a smoke pattern
        // that let either outcome pass.
        data["analyzersRan"]!.Value<bool>().Should().BeTrue(
            "runAnalyzers=true must actually load and run analyzers in this solution");
        data["analyzerCount"]!.Value<int>().Should().BeGreaterThan(0,
            "the SharpLens solution has at least one analyzer reference (TestAnalyzers)");
    }

    [Fact]
    public async Task ToolsCall_MissingOptionalDefaults_ProducePaginatedDefault()
    {
        var data = await CallAndGetDataAsync("roslyn:search_symbols", new
        {
            query = "Async"
        });
        // Default maxResults=50, default offset=0 (per McpServer.cs:1598, 1600).
        var results = (data["results"] as JArray)!;
        results.Count.Should().BeGreaterThan(10,
            "the SharpLens solution has dozens of *Async methods within the default 50-result cap");
        results.Count.Should().BeLessOrEqualTo(50,
            "the default maxResults=50 must cap the returned list");

        // Top-level pagination metadata (Navigation.cs:686-694). NotBeNull-first on
        // every accessor defeats the null-conditional silent-pass.
        data["offset"]!.Value<int>().Should().Be(0, "default offset is 0");
        data["hasMore"].Should().NotBeNull("response must include hasMore field");
        data["pagination"]!["nextOffset"].Should().NotBeNull(
            "pagination block must always include nextOffset (value may be null when no more)");
    }

    [Fact]
    public async Task ToolsCall_ResponseEnvelope_HasSuccessTrueWithDataField()
    {
        // Explicit envelope-shape test: the harness implicitly verifies this on every
        // call, but a dedicated named test surfaces regressions clearly.
        var inner = await CallToolAsync("roslyn:health_check");
        inner["success"]!.Value<bool>().Should().BeTrue(
            "health_check must succeed at the inner contract layer");
        inner["data"].Should().NotBeNull(
            "successful responses must carry a data object");
        inner["error"].Should().BeNull(
            "success responses must NOT carry an error object");
    }
}
