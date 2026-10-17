using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;
using static SharpLensMcp.Tests.MultiTargetNavigationSources;

namespace SharpLensMcp.Tests;

// find_references' reference kind on the shapes the classifier must see through: a
// null-conditional call (invocation) and a C# 14 null-conditional assignment (write)
// bind their member through a MemberBindingExpression; a deconstruction assignment
// writes every tuple element on its left; an indexer is written by an element
// assignment; and the receiver of an element assignment is only read.
public class ReferenceKindClassificationTests
{
    internal const string Code = """
        namespace Lib
        {
            public class Widget
            {
                public int Count { get; set; }
                public int this[int i] { get => i; set { } }
                public void Ping() { }
            }

            public class Uses
            {
                public void Use(Widget? w, Widget a, Widget[] arr)
                {
                    w?.Ping();
                    w?.Count = 5;
                    int r;
                    (r, a.Count) = (1, 2);
                    a[0] = 1;
                    var c = a.Count;
                    arr[0] = a;
                    _ = r + c;
                }
            }
        }
        """;

    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "public void Ping()", "Ping", new[] { ("w?.Ping();", "Ping", "invocation") } },
        new object[] { "public int Count", "Count", new[]
        {
            ("w?.Count = 5;", "Count", "write"),
            ("(r, a.Count) = (1, 2);", "Count", "write"),
            ("var c = a.Count;", "Count", "read")
        } },
        new object[] { "public int this[int i]", "this", new[] { ("a[0] = 1;", "[", "write") } },
        new object[] { "public void Use(", "arr", new[] { ("arr[0] = a;", "arr", "read") } },
        new object[] { "int r;", "r", new[]
        {
            ("(r, a.Count) = (1, 2);", "r", "write"),
            ("_ = r + c;", "r", "read")
        } },
    };

    private static List<string> Expected((string lineMarker, string token, string kind)[] sites) =>
        sites.Select(s =>
        {
            var (line, column) = Pos(Code, s.lineMarker, s.token);
            return $"{line}:{column}:{s.kind}";
        }).ToList();

    private static (int line, int column) Declaration(string lineMarker, string token) => Pos(Code, lineMarker, token);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FindReferences_ClassifiesEachSite(string lineMarker, string token, (string, string, string)[] sites)
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Uses.cs");
        var service = ToolResult.Load(workspace);
        var (line, column) = Declaration(lineMarker, token);

        var data = ToolResult.Data(await service.FindReferencesAsync(document.FilePath!, line, column));

        ((JArray)data["references"]!).Select(r => $"{r["line"]}:{r["column"]}:{r["kind"]}")
            .Should().Equal(Expected(sites));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FindReferences_ClassifiesEachSite_ViaMcp(string lineMarker, string token, (string, string, string)[] sites)
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Uses.cs");
        var host = new InMemoryToolHost(workspace);
        var (line, column) = Declaration(lineMarker, token);

        var data = await host.DataAsync("find_references", new { filePath = document.FilePath, line, column });

        ((JArray)data["references"]!).Select(r => $"{r["line"]}:{r["column"]}:{r["kind"]}")
            .Should().Equal(Expected(sites));
    }
}
