using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;
using static SharpLensMcp.Tests.RefactoringTestKit;

namespace SharpLensMcp.Tests;

// Every refactoring and compound tool that resolves a type by name tells the caller,
// in data.notice, when the name it resolved also matches framework types - the source
// App.Timer shadows System.Threading.Timer - and reports a null notice when the type
// was addressed by position or nothing is shadowed.
public class ShadowNoticeWiringTests
{
    internal const string Source = """
        namespace App
        {
            public class Timer
            {
                private int _count;
                public int Interval { get; set; }
                public void Tick(string label) { _count++; }
                public int Read() => _count;
            }

            public class Clock { }
        }
        """;

    internal const string Notice =
        "'Timer' resolved to App.Timer; also matches System.Threading.Timer — pass the fully-qualified name to target it.";

    public static IEnumerable<object[]> Tools => new[]
    {
        new object[] { "rename_symbol" }, new object[] { "change_signature" }, new object[] { "extract_interface" },
        new object[] { "generate_constructor" }, new object[] { "add_null_checks" }, new object[] { "generate_equality_members" },
        new object[] { "generate_test_stub" }, new object[] { "move_type_to_file" }, new object[] { "split_type" },
        new object[] { "get_type_overview" }, new object[] { "analyze_method" },
    };

    internal static Task<object> Call(RoslynService service, string tool, string typeName) => tool switch
    {
        "rename_symbol" => service.RenameSymbolAsync(null, null, null, "ReadCount", preview: true, typeName: typeName, memberName: "Read"),
        "change_signature" => service.ChangeSignatureAsync(null, null, null,
            new List<SignatureChange> { new() { Action = "rename", Name = "label", NewName = "name" } },
            preview: true, typeName: typeName, memberName: "Tick"),
        "extract_interface" => service.ExtractInterfaceAsync(null, null, null, "ITimer", null, typeName: typeName),
        "generate_constructor" => service.GenerateConstructorAsync(null, null, null, typeName: typeName),
        "add_null_checks" => service.AddNullChecksAsync(null, null, null, preview: true, typeName: typeName, memberName: "Tick"),
        "generate_equality_members" => service.GenerateEqualityMembersAsync(null, null, null, preview: true, typeName: typeName),
        "generate_test_stub" => service.GenerateTestStubAsync(typeName, "Tick"),
        "move_type_to_file" => service.MoveTypeToFileAsync(typeName),
        "split_type" => service.SplitTypeAsync(typeName, new List<string> { "Read" }),
        "get_type_overview" => service.GetTypeOverviewAsync(typeName),
        "analyze_method" => service.AnalyzeMethodAsync(typeName, "Tick"),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null)
    };

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task ShadowingTypeName_SurfacesTheNotice(string tool)
    {
        var (service, _, _) = Load(Source, "App.cs");

        var json = Json(await Call(service, tool, "Timer"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["notice"]!.Value<string>().Should().Be(Notice);
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task FullyQualifiedTypeName_HasANullNotice(string tool)
    {
        var (service, _, _) = Load(Source, "App.cs");

        var json = Json(await Call(service, tool, "App.Timer"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["notice"]!.Type.Should().Be(JTokenType.Null);
    }
}
