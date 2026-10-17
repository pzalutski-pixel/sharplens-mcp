using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;
using static SharpLensMcp.Tests.MultiTargetNavigationSources;

namespace SharpLensMcp.Tests;

// The navigation tools reject a parameter value outside its documented domain with
// INVALID_PARAMETER naming the parameter — an unknown changeType or reference kind, a
// limit below 1, an unknown semantic_query kind — instead of a silent default, clamp or
// empty "safe" result.
public class NavigationParameterDomainTests
{
    internal const string Code = """
        namespace Lib
        {
            public interface IShape { }
            public class Widget : IShape
            {
                public int Count { get; set; }
                public void Use() { Count = 1; }
            }
        }
        """;

    private static (RoslynService service, string path) Load()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Widget.cs");
        return (ToolResult.Load(workspace), document.FilePath!);
    }

    private static string Parameter(object response) =>
        ToolResult.Error(response, ErrorCodes.InvalidParameter)["Context"]!["parameter"]!.Value<string>()!;

    [Fact]
    public async Task PositionTools_RejectOutOfDomainValues()
    {
        var (service, path) = Load();
        var (line, column) = Pos(Code, "public int Count", "Count");
        var (typeLine, typeColumn) = Pos(Code, "public interface IShape", "IShape");

        Parameter(await service.AnalyzeChangeImpactAsync(path, line, column, "bogus")).Should().Be("changeType");
        Parameter(await service.FindReferencesAsync(path, line, column, kindFilter: "writes")).Should().Be("kind");
        Parameter(await service.FindReferencesAsync(path, line, column, maxResults: 0)).Should().Be("maxResults");
        Parameter(await service.FindReferencesAsync(path, line, column, maxResults: -1)).Should().Be("maxResults");
        Parameter(await service.FindImplementationsAsync(path, typeLine, typeColumn, maxResults: 0)).Should().Be("maxResults");
    }

    [Fact]
    public async Task ChangeTypeAndKind_AcceptTheirDocumentedValuesCaseInsensitively()
    {
        var (service, path) = Load();
        var (line, column) = Pos(Code, "public int Count", "Count");

        var impact = ToolResult.Data(await service.AnalyzeChangeImpactAsync(path, line, column, "DELETE"));
        var writes = ToolResult.Data(await service.FindReferencesAsync(path, line, column, kindFilter: "Write"));

        impact["changeType"]!.Value<string>().Should().Be("delete");
        impact["breakingChanges"]!.Value<int>().Should().Be(1);
        impact["safe"]!.Value<bool>().Should().BeFalse();
        writes["kindFilter"]!.Value<string>().Should().Be("write");
        writes["totalReferencesAfterFilter"]!.Value<int>().Should().Be(1);
    }

    [Fact]
    public async Task NameTools_RejectOutOfDomainValues()
    {
        var (service, _) = Load();

        Parameter(await service.GetDerivedTypesAsync("Lib.IShape", maxResults: 0)).Should().Be("maxResults");
        Parameter(await service.GetTypeMembersBatchAsync(new List<string> { "Lib.Widget" }, maxResultsPerType: 0)).Should().Be("maxResultsPerType");
        Parameter(await service.GetMethodSourceBatchAsync(
            new List<Dictionary<string, object>> { new() { ["typeName"] = "Lib.Widget", ["methodName"] = "Use" } }, maxMethods: 0))
            .Should().Be("maxMethods");
        Parameter(await service.SemanticQueryAsync(null, null, null, null, null, null, null, null, null, null, maxResults: -1))
            .Should().Be("maxResults");
        Parameter(await service.SemanticQueryAsync(new List<string> { "Banana" }, null, null, null, null, null, null, null, null, null, null))
            .Should().Be("kinds");
    }

    [Fact]
    public async Task OutOfDomainValues_ViaMcp()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Widget.cs");
        var host = new InMemoryToolHost(workspace);
        var (line, column) = Pos(Code, "public int Count", "Count");

        var bogus = await host.ErrorAsync("analyze_change_impact",
            new { filePath = document.FilePath, line, column, changeType = "bogus" }, ErrorCodes.InvalidParameter);
        var writes = await host.ErrorAsync("find_references",
            new { filePath = document.FilePath, line, column, kind = "writes" }, ErrorCodes.InvalidParameter);
        var query = await host.ErrorAsync("semantic_query", new { maxResults = -1 }, ErrorCodes.InvalidParameter);

        bogus["context"]!["parameter"]!.Value<string>().Should().Be("changeType");
        writes["context"]!["parameter"]!.Value<string>().Should().Be("kind");
        query["context"]!["parameter"]!.Value<string>().Should().Be("maxResults");
    }
}
