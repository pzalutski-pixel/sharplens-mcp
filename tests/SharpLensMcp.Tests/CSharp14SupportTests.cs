using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks that the referenced Roslyn understands current C# (the analyzed code is
// the user's, written against whatever compiler they run — the server must parse
// it even when this repo's own SDK is older). The C# 14 surface lives in string
// fixtures because the test PROJECT compiles with the installed SDK's compiler,
// while these strings are parsed by the NuGet Roslyn via AdhocWorkspace.
public class CSharp14SupportTests
{
    private const string CSharp14Code = @"
namespace Modern;

public class Widget
{
    // field-backed property (C# 14 'field' keyword)
    public int Count { get; set => field = value < 0 ? 0 : value; }

    public string? Name { get; set; }
}

public static class WidgetOps
{
    // null-conditional assignment (C# 14)
    public static void ClearName(Widget? w)
    {
        w?.Name = null;
    }
}

public static class WidgetExtensions
{
    // extension members block (C# 14)
    extension(Widget w)
    {
        public int DoubleCount => w.Count * 2;
    }
}";

    private static (RoslynService service, string filePath) CreateService()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(CSharp14Code, "Widget.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, document.FilePath!);
    }

    [Fact]
    public async Task GetDiagnostics_OnCSharp14Constructs_ReportsZeroErrors()
    {
        var (service, filePath) = CreateService();
        var result = await service.GetDiagnosticsAsync(filePath, projectPath: null, severity: null, includeHidden: false, runAnalyzers: false);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["errorCount"].Should().NotBeNull("diagnostics response must include errorCount");
        json["data"]!["errorCount"]!.Value<int>().Should().Be(0,
            "field-backed properties, null-conditional assignment, and extension blocks must parse cleanly: {0}",
            json["data"]!["diagnostics"]);
    }

    [Fact]
    public async Task SearchSymbols_FindsTypeDeclaredAlongsideCSharp14Constructs()
    {
        var (service, _) = CreateService();
        var result = await service.SearchSymbolsAsync("Widget", kind: "Class", maxResults: 10);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var results = json["data"]!["results"] as JArray;
        results.Should().NotBeNullOrEmpty("Widget must be findable in a file using C# 14 syntax");
        results!.Select(r => r["name"]?.Value<string>())
            .Should().Contain("Widget");
    }

    [Fact]
    public async Task GetTypeMembers_OnFieldBackedPropertyType_ListsTheProperty()
    {
        var (service, _) = CreateService();
        var result = await service.GetTypeMembersAsync("Widget");
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var members = json["data"]!["members"] as JArray;
        members.Should().NotBeNullOrEmpty();
        members!.Select(m => m["name"]?.Value<string>())
            .Should().Contain("Count", "the field-backed property must surface as a normal property symbol");
    }
}
