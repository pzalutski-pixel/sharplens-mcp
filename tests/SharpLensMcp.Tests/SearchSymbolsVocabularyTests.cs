using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// search_symbols labels each result with the symbol-kind vocabulary its kind filter
// accepts: types by their kind (a record is labeled Record, and the Class filter
// includes record classes as Struct includes record structs), members by MemberKind.
// Paging is validated (offset >= 0, maxResults >= 1) and ordered canonically, so
// following nextOffset visits every result exactly once and terminates; an unknown kind
// or matchMode is INVALID_PARAMETER.
public class SearchSymbolsVocabularyTests
{
    internal const string Code = """
        namespace Lib
        {
            public record Person(string Name);
            public record struct PersonPoint(int X);
            public class PersonUser { }
            public struct PersonStruct { }
            public interface IPerson { }
            public enum PersonKind { A }
            public delegate void PersonHandler();
        }
        """;

    private static RoslynService Load()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "People.cs");
        return ToolResult.Load(workspace);
    }

    private static List<string> Results(JToken data) =>
        ((JArray)data["results"]!).Select(r => $"{r["fullyQualifiedName"]}|{r["kind"]}").ToList();

    [Fact]
    public async Task NoFilter_LabelsEachTypeByItsKind()
    {
        var data = ToolResult.Data(await Load().SearchSymbolsAsync("Person", kind: null, maxResults: 50));

        Results(data).Should().Equal(
            "Lib.IPerson|Interface",
            "Lib.Person|Record",
            "Lib.PersonHandler|Delegate",
            "Lib.PersonKind|Enum",
            "Lib.PersonPoint|RecordStruct",
            "Lib.PersonStruct|Struct",
            "Lib.PersonUser|Class");
    }

    [Theory]
    [InlineData("Class", new[] { "Lib.Person|Record", "Lib.PersonUser|Class" })]
    [InlineData("record", new[] { "Lib.Person|Record" })]
    [InlineData("Struct", new[] { "Lib.PersonPoint|RecordStruct", "Lib.PersonStruct|Struct" })]
    [InlineData("RecordStruct", new[] { "Lib.PersonPoint|RecordStruct" })]
    [InlineData("Interface", new[] { "Lib.IPerson|Interface" })]
    [InlineData("Delegate", new[] { "Lib.PersonHandler|Delegate" })]
    public async Task KindFilter_AgreesWithTheLabels(string kind, string[] expected)
    {
        var data = ToolResult.Data(await Load().SearchSymbolsAsync("Person", kind, maxResults: 50));

        Results(data).Should().Equal(expected);
    }

    [Fact]
    public async Task FollowingNextOffset_VisitsEveryResultOnce_AndTerminates()
    {
        var service = Load();
        var visited = new List<string>();
        int? offset = 0;
        var pages = 0;
        while (offset is { } current)
        {
            var data = ToolResult.Data(await service.SearchSymbolsAsync("Person", kind: null, maxResults: 3, offset: current));
            visited.AddRange(Results(data));
            offset = data["pagination"]!["nextOffset"]!.Type == JTokenType.Null ? null : data["pagination"]!["nextOffset"]!.Value<int>();
            data["hasMore"]!.Value<bool>().Should().Be(offset != null);
            pages++;
            pages.Should().BeLessThan(10, "paging must terminate");
        }

        pages.Should().Be(3);
        visited.Should().Equal(Results(ToolResult.Data(await service.SearchSymbolsAsync("Person", kind: null, maxResults: 50))));
    }

    [Theory]
    [InlineData(null, 50, -5, "glob", "offset")]
    [InlineData(null, 0, 0, "glob", "maxResults")]
    [InlineData(null, -1, 0, "glob", "maxResults")]
    [InlineData("Banana", 50, 0, "glob", "kind")]
    [InlineData(null, 50, 0, "regex", "matchMode")]
    public async Task OutOfDomainParameters_AreInvalidParameter(string? kind, int maxResults, int offset, string matchMode, string parameter)
    {
        var error = ToolResult.Error(
            await Load().SearchSymbolsAsync("Person", kind, maxResults, offset: offset, matchMode: matchMode),
            ErrorCodes.InvalidParameter);

        error["Context"]!["parameter"]!.Value<string>().Should().Be(parameter);
    }

    [Fact]
    public async Task Vocabulary_ViaMcp()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "People.cs");
        var host = new InMemoryToolHost(workspace);

        var data = await host.DataAsync("search_symbols", new { query = "Person", kind = "Class" });
        var error = await host.ErrorAsync("search_symbols", new { query = "Person", offset = -5 }, ErrorCodes.InvalidParameter);
        var zero = await host.ErrorAsync("search_symbols", new { query = "Person", maxResults = 0 }, ErrorCodes.InvalidParameter);

        Results(data).Should().Equal("Lib.Person|Record", "Lib.PersonUser|Class");
        error["context"]!["parameter"]!.Value<string>().Should().Be("offset");
        zero["context"]!["parameter"]!.Value<string>().Should().Be("maxResults");
    }
}
