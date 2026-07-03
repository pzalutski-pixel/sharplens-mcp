using System.Text.Json;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// Schema⇄dispatcher parity: for every published tool, the set of parameters the
// dispatch arm READS must exactly equal the set its schema DECLARES. Catches both
// drift directions — a dispatcher reading an undeclared parameter (the
// parseGodotHints bug class) and a schema declaring a parameter the dispatcher
// ignores (stale schema). Works by probing each tool with empty arguments while
// JsonRpcParameters records every accessor call; all argument accessors in a
// dispatch arm evaluate BEFORE the service method runs, so the recorded read-set
// is complete even though the unloaded service fails every call fast.
public class ToolSchemaParityTests
{
    [Fact]
    public async Task EveryTool_ReadsExactlyTheParametersItsSchemaDeclares()
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var schemasJson = JsonSerializer.Serialize(ToolRegistry.BuildToolSchemas(), jsonOptions);
        var schemas = JArray.Parse(schemasJson);
        schemas.Count.Should().Be(ToolRegistry.Definitions.Count, "every published tool must be probed");

        // Unloaded service: every solution-dependent tool throws
        // SolutionNotLoadedException immediately after argument evaluation.
        var service = new RoslynService();
        var failures = new List<string>();

        foreach (var tool in schemas)
        {
            var name = tool["name"]!.Value<string>()!;
            var declared = (tool["inputSchema"]!["properties"] as JObject)?
                .Properties().Select(prop => prop.Name).ToHashSet() ?? new HashSet<string>();

            var recorded = new HashSet<string>();
            JsonRpcParameters.ProbeLog.Value = recorded;
            try
            {
                var p = new JsonRpcParameters(null, name);
                await ToolRegistry.DispatchAsync(name, p, service);
            }
            catch
            {
                // Expected: probing with empty args against an unloaded service
                // fails — after the read-set we care about was already recorded.
            }
            finally
            {
                JsonRpcParameters.ProbeLog.Value = null;
            }

            var undeclared = recorded.Except(declared).OrderBy(n => n).ToList();
            var unread = declared.Except(recorded).OrderBy(n => n).ToList();
            if (undeclared.Count > 0)
            {
                failures.Add($"{name}: dispatcher reads parameters the schema does not declare: {string.Join(", ", undeclared)}");
            }
            if (unread.Count > 0)
            {
                failures.Add($"{name}: schema declares parameters the dispatcher never reads: {string.Join(", ", unread)}");
            }
        }

        failures.Should().BeEmpty(
            "every tool's schema and dispatch arm must agree on the parameter set:\n" +
            string.Join("\n", failures));
    }

    [Fact]
    public void EveryDefinition_NameMatchesItsSchemaName()
    {
        // The ToolDefinition record carries the dispatch name and the schema
        // carries the published name — pairing them in one record makes drift
        // unlikely, this test makes it impossible to ship.
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        foreach (var definition in ToolRegistry.Definitions)
        {
            var schema = JObject.Parse(JsonSerializer.Serialize(definition.Schema, jsonOptions));
            schema["name"]!.Value<string>().Should().Be(definition.Name,
                "the record's dispatch name and its schema's published name must be the same tool");
        }
    }
}
