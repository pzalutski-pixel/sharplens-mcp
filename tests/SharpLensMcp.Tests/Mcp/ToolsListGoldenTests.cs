using System.Text.Json;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// Byte-level golden test for the tools/list wire response. The asset was
// captured from the running server; any change to a tool name, description,
// or schema must update the asset DELIBERATELY (re-capture via:
//   printf '{"jsonrpc":"2.0","id":1,"method":"tools/list"}\n' | dotnet src/bin/Release/net8.0/SharpLensMcp.dll 2>/dev/null | head -1
// ). This guards the tool registry against accidental drift during refactors.
public class ToolsListGoldenTests
{
    [Fact]
    public async Task ToolsList_MatchesGoldenSnapshot()
    {
        var server = new McpServer();
        var response = await server.HandleRequestAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
        response.Should().NotBeNull();

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            Converters = { new RequestId.Converter() }
        };
        var liveJson = JsonSerializer.Serialize(response, jsonOptions);

        var goldenPath = Path.Combine(AppContext.BaseDirectory, "Mcp", "ToolsListGolden.json");
        File.Exists(goldenPath).Should().BeTrue(
            "the golden asset must be copied to the test output directory");
        var goldenJson = File.ReadAllText(goldenPath).Trim();

        // Multi-line tool descriptions come from verbatim string literals, so the
        // newline style INSIDE the wire strings follows the source checkout's line
        // endings (CRLF on Windows, LF elsewhere). Normalize the escaped sequences
        // in both payloads so the comparison is platform- and checkout-stable while
        // every other byte still must match exactly.
        liveJson = liveJson.Replace("\\r\\n", "\\n");
        goldenJson = goldenJson.Replace("\\r\\n", "\\n");

        // Compare as parsed JSON (not raw strings) so whitespace in the asset
        // cannot cause false failures, while property order and every value
        // still must match exactly.
        var live = JToken.Parse(liveJson);
        var golden = JToken.Parse(goldenJson);

        JToken.DeepEquals(live, golden).Should().BeTrue(
            $"the tools/list response must match the golden snapshot exactly. " +
            $"If a schema change is intentional, re-capture the asset. " +
            $"Live tool count: {live["result"]?["tools"]?.Count()}, " +
            $"golden tool count: {golden["result"]?["tools"]?.Count()}");
    }

    [Fact]
    public async Task ToolsList_HasExactlyNinetyTwoTools()
    {
        var server = new McpServer();
        var response = await server.HandleRequestAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
        // Serialize through the wire layer (System.Text.Json) before parsing —
        // the schemas carry JsonNode values that Newtonsoft's object walker
        // cannot traverse directly.
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new RequestId.Converter() }
        };
        var json = JObject.Parse(JsonSerializer.Serialize(response, jsonOptions));
        var tools = json["result"]!["tools"] as JArray;
        tools.Should().NotBeNull();
        tools!.Count.Should().Be(92,
            "adding find_path_between brings the count to 92; the README tool tables and count are synced at release. Changing the count must be a deliberate, documented decision");
    }
}
