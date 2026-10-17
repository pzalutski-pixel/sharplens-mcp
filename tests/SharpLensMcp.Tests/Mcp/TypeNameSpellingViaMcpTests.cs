using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// The type-name spellings of TypeNameSpellingTests through the JSON-RPC dispatcher:
// a dotted nested name, a generic display name and a keyword alias resolve, and an
// ambiguous simple name returns AMBIGUOUS_TYPE (camelCase on the wire) whose
// candidates resolve when passed back. Runs on a fresh McpServer loaded with the
// in-memory fixture.
public class TypeNameSpellingViaMcpTests
{
    private static McpHarnessOps Load()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(TypeNameSpellingTests.Code, "Nested.cs");
        var server = new McpServer();
        server.LoadFromWorkspaceForTesting(workspace);
        return new McpHarnessOps(server);
    }

    [Theory]
    [InlineData("Lib.OuterA.Builder", "Lib.OuterA.Builder")]
    [InlineData("Lib.Repo<T>", "Lib.Repo<T>")]
    [InlineData("object", "object")]
    public async Task GetTypeMembers_ResolvesTheSpelling(string typeName, string expected)
    {
        var inner = await Load().CallToolAsync("get_type_members", new { typeName });
        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        inner["data"]!["typeName"]!.Value<string>().Should().Be(expected);
    }

    [Fact]
    public async Task GetTypeMembers_AmbiguousNestedName_CandidatesRoundTrip()
    {
        var ops = Load();
        var inner = await ops.CallToolAsync("get_type_members", new { typeName = "Builder" });
        inner["success"]!.Value<bool>().Should().BeFalse();
        inner["error"]!["code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousType);
        var candidates = (inner["error"]!["context"]!["candidates"] as JArray)!
            .Select(c => c.Value<string>()!)
            .ToList();
        candidates.Should().Equal("Lib.OuterA.Builder", "Lib.OuterB.Builder");

        var retried = await ops.CallToolAsync("get_type_members", new { typeName = candidates[1] });
        retried["success"]!.Value<bool>().Should().BeTrue(retried.ToString());
        retried["data"]!["typeName"]!.Value<string>().Should().Be("Lib.OuterB.Builder");
    }

    [Fact]
    public async Task CheckTypeCompatibility_KeywordAliases()
    {
        var inner = await Load().CallToolAsync("check_type_compatibility",
            new { sourceType = "int", targetType = "long" });
        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        inner["data"]!["sourceType"]!.Value<string>().Should().Be("int");
        inner["data"]!["targetType"]!.Value<string>().Should().Be("long");
        inner["data"]!["conversionKind"]!.Value<string>().Should().Be("ImplicitNumeric");
    }

    [Fact]
    public async Task GetTypeMembers_NonTypeSpelling_ReturnsTypeNotFound()
    {
        var inner = await Load().CallToolAsync("get_type_members", new { typeName = "int[]" });
        inner["success"]!.Value<bool>().Should().BeFalse();
        inner["error"]!["code"]!.Value<string>().Should().Be(ErrorCodes.TypeNotFound);
    }
}
