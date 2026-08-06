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
            "search_symbols",
            arguments: new { kind = "Class", maxResults = 10 },
            expectedCode: -32602);
        msg.Should().Contain("query", "the missing parameter name must be in the message");
        msg.Should().Contain("search_symbols", "the tool name must be in the message");
        msg.Should().Contain("Missing required parameter", "the message must use the standard wording");
    }

    [Fact]
    public async Task ToolsCall_MissingRequiredInt_Returns32602WithStandardWording()
    {
        var msg = await CallExpectingJsonRpcErrorAsync(
            "get_symbol_info",
            arguments: new { filePath = Fixture.RoslynServicePath },
            expectedCode: -32602);
        msg.Should().Contain("line", "the first missing required int must surface");
        msg.Should().Contain("get_symbol_info");
        msg.Should().Contain("Missing required parameter",
            "the int-missing branch must use the same wording as the string-missing branch");
    }

    [Fact]
    public async Task ToolsCall_WrongTypeParameter_Returns32602WithWrongTypeWording()
    {
        var msg = await CallExpectingJsonRpcErrorAsync(
            "get_symbol_info",
            arguments: new { filePath = Fixture.RoslynServicePath, line = "not-a-number", column = 0 },
            expectedCode: -32602);
        msg.Should().Contain("line");
        msg.Should().Contain("wrong type", "JsonRpcParameters labels type mismatch with 'has wrong type'");
        msg.Should().Contain("get_symbol_info");
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_Returns32602WithUnknownToolWording()
    {
        var msg = await CallExpectingJsonRpcErrorAsync(
            "does_not_exist_12345",
            arguments: new { },
            expectedCode: -32602);
        msg.Should().Contain("Unknown tool", "dispatcher must surface 'Unknown tool' for unregistered names");
        msg.Should().Contain("does_not_exist_12345");
    }

    [Fact]
    public async Task ToolsCall_MissingToolName_Returns32602WithInvalidParamsWording()
    {
        var msg = await CallExpectingJsonRpcErrorAsync(
            toolName: "",
            arguments: new { },
            expectedCode: -32602);
        msg.Should().Contain("Invalid params",
            "the dispatcher must use the standard 'Invalid params' prefix");
        msg.Should().Contain("missing tool name");
    }

    [Fact]
    public async Task ToolsCall_MissingRequiredStringArray_Returns32602WithArrayWording()
    {
        // get_type_members_batch's typeNames is RequiredStringArray.
        // Missing the field must surface the array-
        // specific wording — distinct from the scalar 'Missing required parameter' one.
        var msg = await CallExpectingJsonRpcErrorAsync(
            "get_type_members_batch",
            arguments: new { },
            expectedCode: -32602);
        msg.Should().Contain("Missing required array parameter",
            "RequiredStringArray uses a distinct error wording from scalar Required");
        msg.Should().Contain("typeNames");
        msg.Should().Contain("get_type_members_batch");
    }

    [Fact]
    public async Task ToolsCall_OptionalStringArrayWithNonArrayValue_Returns32602WithArrayWording()
    {
        // semantic_query's "kinds" is OptionalStringArray. Passing
        // a scalar where an array is expected must surface "must be an array of strings"
        // not the generic 'has wrong type' wording.
        var msg = await CallExpectingJsonRpcErrorAsync(
            "semantic_query",
            arguments: new { kinds = "Class" },
            expectedCode: -32602);
        msg.Should().Contain("kinds");
        msg.Should().Contain("must be an array of strings",
            "the OptionalStringArray branch must surface its own wording");
        msg.Should().Contain("semantic_query");
    }

    [Fact]
    public async Task ToolsCall_StringArrayArgument_RoundTripsBothEntriesWithBatchMetadata()
    {
        var data = await CallAndGetDataAsync("get_type_members_batch", new
        {
            typeNames = new[] { "RoslynService", "McpServer" }
        });
        // Lock the batch-tool metadata — totalRequested,
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
        var data = await CallAndGetDataAsync("get_method_source_batch", new
        {
            methods = new object[]
            {
                new { typeName = "RoslynService", methodName = "LoadSolutionAsync" },
                new { typeName = "RoslynService", methodName = "GetHealthCheckAsync" }
            }
        });
        // Lock the batch-tool metadata. NotBeNull-first is
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
        var data = await CallAndGetDataAsync("get_diagnostics", new
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
        var data = await CallAndGetDataAsync("get_diagnostics", new
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
        var data = await CallAndGetDataAsync("search_symbols", new
        {
            query = "Async"
        });
        // Default maxResults=50, default offset=0.
        var results = (data["results"] as JArray)!;
        results.Count.Should().BeGreaterThan(10,
            "the SharpLens solution has dozens of *Async methods within the default 50-result cap");
        results.Count.Should().BeLessOrEqualTo(50,
            "the default maxResults=50 must cap the returned list");

        // Top-level pagination metadata. NotBeNull-first on
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
        var inner = await CallToolAsync("health_check");
        inner["success"]!.Value<bool>().Should().BeTrue(
            "health_check must succeed at the inner contract layer");
        inner["data"].Should().NotBeNull(
            "successful responses must carry a data object");
        inner["error"].Should().BeNull(
            "success responses must NOT carry an error object");
    }

    [Fact]
    public async Task ToolsCall_UnexpectedHandlerException_SurfacesAsInBandIsErrorResult()
    {
        // An exception escaping a tool handler is a TOOL failure: it must surface as an
        // in-band CallToolResult with isError, where the model can read and recover from
        // it — not as a JSON-RPC -32603 protocol error. Trigger the generic catch through
        // the deterministic fault seam rather than a specific tool's out-of-range input, so
        // hardening any one tool (as the region tools now are) can't silently delete this
        // contract's only coverage.
        var server = new McpServer(TimeSpan.FromSeconds(30),
            testToolCallFault: new InvalidOperationException("simulated handler fault"));

        var wireOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Converters = { new RequestId.Converter() }
        };
        var request = System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 9301,
            method = "tools/call",
            @params = new
            {
                name = "health_check",
                arguments = new { }
            }
        });

        var response = await server.HandleRequestAsync(request);
        var envelope = JObject.Parse(System.Text.Json.JsonSerializer.Serialize(response, wireOptions));

        envelope["error"].Should().BeNull(
            "tool failures must not use JSON-RPC error responses");
        envelope["result"]!["isError"]!.Value<bool>().Should().BeTrue(
            "the CallToolResult must flag the failure in-band");
        var inner = JObject.Parse(envelope["result"]!["content"]![0]!["text"]!.Value<string>()!);
        inner["success"]!.Value<bool>().Should().BeFalse();
        inner["error"]!["code"]!.Value<string>().Should().Be("INTERNAL_ERROR");
        inner["error"]!["message"]!.Value<string>().Should().Contain("Unhandled error executing tool",
            "the generic catch prefixes the escaping handler exception message");
        inner["error"]!["hint"]!.Value<string>().Should().NotBeNullOrEmpty(
            "the envelope must carry a recovery hint like every structured tool error");
    }

    [Fact]
    public async Task ToolsCall_BeforeSolutionLoad_ReturnsStructuredSolutionNotLoadedError()
    {
        // A fresh server with no solution loaded must answer with the standard
        // structured envelope (SOLUTION_NOT_LOADED + recovery hint), not the
        // opaque -32603 internal error a bare exception used to produce.
        var freshServer = new McpServer();
        var harness = new McpHarnessOps(freshServer);
        var inner = await harness.CallToolAsync("get_type_members", new { typeName = "Anything" });

        inner["success"]!.Value<bool>().Should().BeFalse(
            "tools called before load_solution must fail in-band, not at the JSON-RPC layer");
        inner["error"].Should().NotBeNull();
        // Wire format is camelCase: the dispatcher serializes with the CamelCase
        // naming policy (direct-service tests see PascalCase via JObject.FromObject).
        inner["error"]!["code"]!.Value<string>().Should().Be("SOLUTION_NOT_LOADED");
        inner["error"]!["hint"]!.Value<string>().Should().Contain("load_solution",
            "the hint must tell the agent how to recover");
    }
}
