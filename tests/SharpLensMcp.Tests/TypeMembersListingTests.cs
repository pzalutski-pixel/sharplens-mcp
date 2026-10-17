using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;
using static SharpLensMcp.Tests.MultiTargetNavigationSources;

namespace SharpLensMcp.Tests;

// get_type_members lists a type's members by the one listing rule (UserVisibleMembers)
// and labels them with the MemberKind vocabulary, which is also what memberKind filters
// by: accessors are represented by their property or event and never listed, every
// listed kind is counted in memberCounts, an explicit interface implementation is flagged
// (it is not part of the type's public surface), and inheritance follows the C# rules —
// no private base members, no base constructors, overridden members once. Out-of-domain
// verbosity/memberKind/maxResults values are INVALID_PARAMETER, and a member without
// source has a null location in full verbosity.
public class TypeMembersListingTests
{
    internal const string Code = """
        namespace Lib
        {
            public interface IShape { double Area { get; } void Draw(); }

            public abstract class ShapeBase
            {
                private int _secret;
                protected int Id { get; set; }
                public abstract double Perimeter { get; }
                public virtual string Describe() => "shape" + _secret;
            }

            public class Square : ShapeBase, IShape
            {
                public Square() { }
                public double Side { get; set; }
                public double Area => Side * Side;
                void IShape.Draw() { }
                public override double Perimeter => 4 * Side;
                public static Square operator +(Square a, Square b) => new Square { Side = a.Side + b.Side };
                public int this[int i] => i;
                public event System.EventHandler? Changed;
                public void Resize(int n) { }
                public void Resize(double n) { }
            }

            public class Failure : System.Exception { }
        }
        """;

    private static (RoslynService service, string path) Load()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Shapes.cs");
        return (ToolResult.Load(workspace), document.FilePath!);
    }

    private static List<string> Entries(JToken data) =>
        ((JArray)data["members"]!).Select(m => $"{m["name"]}|{m["kind"]}").ToList();

    [Fact]
    public async Task DeclaredMembers_NoAccessors_KindsFromTheVocabulary_CountsSumToTotal()
    {
        var (service, _) = Load();

        var data = ToolResult.Data(await service.GetTypeMembersAsync("Lib.Square"));

        Entries(data).Should().Equal(
            ".ctor|Constructor",
            "Side|Property",
            "Area|Property",
            "Lib.IShape.Draw|Method",
            "Perimeter|Property",
            "op_Addition|Operator",
            "this[]|Indexer",
            "Changed|Event",
            "Resize|Method");
        data["totalMembers"]!.Value<int>().Should().Be(9);
        var counts = (JObject)data["memberCounts"]!;
        counts.Properties().ToDictionary(p => p.Name, p => p.Value.Value<int>()).Should().BeEquivalentTo(
            new Dictionary<string, int>
            {
                ["Constructor"] = 1, ["Property"] = 3, ["Method"] = 2, ["Operator"] = 1, ["Indexer"] = 1, ["Event"] = 1
            });
        var draw = ((JArray)data["members"]!).Single(m => m["name"]!.Value<string>() == "Lib.IShape.Draw");
        draw["isExplicitInterfaceImplementation"]!.Value<bool>().Should().BeTrue();
        var side = ((JArray)data["members"]!).Single(m => m["name"]!.Value<string>() == "Side");
        side["isExplicitInterfaceImplementation"]!.Value<bool>().Should().BeFalse();
    }

    [Theory]
    [InlineData("Constructor", new[] { ".ctor|Constructor" })]
    [InlineData("operator", new[] { "op_Addition|Operator" })]
    [InlineData("Indexer", new[] { "this[]|Indexer" })]
    [InlineData("Method", new[] { "Lib.IShape.Draw|Method", "Resize|Method" })]
    [InlineData("Event", new[] { "Changed|Event" })]
    public async Task MemberKindFilter_AgreesWithTheLabels(string memberKind, string[] expected)
    {
        var (service, _) = Load();

        var data = ToolResult.Data(await service.GetTypeMembersAsync("Lib.Square", memberKind: memberKind));

        Entries(data).Should().Equal(expected);
    }

    [Fact]
    public async Task IncludeInherited_AddsAccessibleBaseMembers_NotPrivatesOrOverriddenOnes()
    {
        var (service, _) = Load();

        var data = ToolResult.Data(await service.GetTypeMembersAsync("Lib.Square", includeInherited: true));

        var members = (JArray)data["members"]!;
        members.Where(m => m["isInherited"]!.Value<bool>()).Select(m => $"{m["name"]}|{m["kind"]}|{m["accessibility"]}")
            .Should().Equal("Id|Property|Protected", "Describe|Method|Public");
        members.Select(m => m["name"]!.Value<string>()).Should().NotContain("_secret");
        members.Count(m => m["name"]!.Value<string>() == "Perimeter").Should().Be(1);
        data["totalMembers"]!.Value<int>().Should().Be(11);
    }

    [Fact]
    public async Task FullVerbosity_SourceMemberLocation_AndNullForAMemberWithoutSource()
    {
        var (service, path) = Load();

        var square = ToolResult.Data(await service.GetTypeMembersAsync("Lib.Square", verbosity: "full", memberKind: "Event"));
        var failure = ToolResult.Data(await service.GetTypeMembersAsync(
            "Lib.Failure", includeInherited: true, memberKind: "Property", verbosity: "full", maxResults: 100));

        var (line, column) = Pos(Code, "public event System.EventHandler", "Changed");
        var changed = ((JArray)square["members"]!).Single();
        changed["location"]!["filePath"]!.Value<string>().Should().Be(ToolResult.Reported(path));
        changed["location"]!["line"]!.Value<int>().Should().Be(line);
        changed["location"]!["column"]!.Value<int>().Should().Be(column);
        var message = ((JArray)failure["members"]!).Single(m => m["name"]!.Value<string>() == "Message");
        message["location"]!.Type.Should().Be(JTokenType.Null, "a metadata member has no source location");
    }

    [Theory]
    [InlineData(null, "verbose", 100, "verbosity")]
    [InlineData("Accessor", "compact", 100, "memberKind")]
    [InlineData(null, "compact", -1, "maxResults")]
    [InlineData(null, "compact", 0, "maxResults")]
    public async Task OutOfDomainParameters_AreInvalidParameter(string? memberKind, string verbosity, int maxResults, string parameter)
    {
        var (service, _) = Load();

        var error = ToolResult.Error(
            await service.GetTypeMembersAsync("Lib.Square", memberKind: memberKind, verbosity: verbosity, maxResults: maxResults),
            ErrorCodes.InvalidParameter);

        error["Context"]!["parameter"]!.Value<string>().Should().Be(parameter);
    }

    [Fact]
    public async Task Listing_ViaMcp()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Shapes.cs");
        var host = new InMemoryToolHost(workspace);

        var data = await host.DataAsync("get_type_members", new { typeName = "Lib.Square", memberKind = "Indexer" });
        var error = await host.ErrorAsync("get_type_members", new { typeName = "Lib.Square", verbosity = "verbose" }, ErrorCodes.InvalidParameter);

        Entries(data).Should().Equal("this[]|Indexer");
        error["context"]!["parameter"]!.Value<string>().Should().Be("verbosity");
    }

    [Fact]
    public async Task Batch_PartialFailure_IsNotTruncated()
    {
        var (service, _) = Load();

        var response = await service.GetTypeMembersBatchAsync(new List<string> { "Lib.Square", "Lib.Nope" }, verbosity: "summary");

        var data = ToolResult.Data(response);
        data["successCount"]!.Value<int>().Should().Be(1);
        data["errorCount"]!.Value<int>().Should().Be(1);
        var meta = ToolResult.Meta(response);
        meta["TotalCount"]!.Value<int>().Should().Be(2);
        meta["ReturnedCount"]!.Value<int>().Should().Be(1);
        meta["Truncated"]!.Value<bool>().Should().BeFalse("a type that failed to resolve is not a cut-off result");
    }

    [Fact]
    public async Task Batch_PartialFailure_IsNotTruncated_ViaMcp()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Shapes.cs");
        var host = new InMemoryToolHost(workspace);

        var inner = await host.CallAsync("get_type_members_batch", new { typeNames = new[] { "Lib.Square", "Lib.Nope" } });

        inner["meta"]!["totalCount"]!.Value<int>().Should().Be(2);
        inner["meta"]!["returnedCount"]!.Value<int>().Should().Be(1);
        inner["meta"]!["truncated"]!.Value<bool>().Should().BeFalse();
    }
}
