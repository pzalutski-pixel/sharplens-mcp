using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// ResolveSymbolByNameAsync (behind rename_symbol, change_signature, get_documentation,
// get_super_method and the other name-addressed tools) indexes overloads in the
// canonical OverloadOrdering order — so overloadIndex 0 of a partial type split across
// a.cs and B.cs is the same Foo(int) every overload-listing tool shows — addresses
// explicit interface implementations by name, and names the declaring base type when
// the member is only inherited.
public class NameAddressingOrderTests
{
    internal static readonly (string code, string fileName)[] Files =
    {
        (@"namespace Lib
{
    public partial class P { public void Foo(int value) { } }
}", "a.cs"),
        (@"namespace Lib
{
    public partial class P { public void Foo(string text) { } }
}", "B.cs"),
        (@"namespace Lib
{
    public interface IShape { void Draw(); }

    public abstract class ShapeBase
    {
        public virtual string Describe() => ""shape"";
    }

    public class Square : ShapeBase, IShape
    {
        void IShape.Draw() { }
    }
}", "Shapes.cs"),
    };

    private static RoslynService CreateService()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithMultipleDocuments(Files);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    [Theory]
    [InlineData(0, "Lib.P.Foo(int)")]
    [InlineData(1, "Lib.P.Foo(string)")]
    public async Task OverloadIndex_FollowsTheCanonicalOrder(int overloadIndex, string expected)
    {
        var (symbol, error) = await CreateService().ResolveSymbolByNameAsync("P", "Foo", overloadIndex);
        error.Should().BeNull();
        symbol!.ToDisplayString().Should().Be(expected);
    }

    [Fact]
    public async Task AmbiguousOverloads_ListCandidatesInTheCanonicalOrder()
    {
        var (_, error) = await CreateService().ResolveSymbolByNameAsync("P", "Foo");
        var json = JObject.FromObject(error!);
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousMatch);
        var candidates = (JArray)json["error"]!["Context"]!["candidates"]!;
        candidates.Select(c => (c["overloadIndex"]!.Value<int>(), c["signature"]!.Value<string>()))
            .Should().Equal((0, "Lib.P.Foo(int)"), (1, "Lib.P.Foo(string)"));
    }

    [Fact]
    public async Task RenameByOverloadIndexZero_RenamesTheOverloadInADotCs()
    {
        var result = await CreateService().RenameSymbolAsync(
            null, null, null, newName: "Bar", preview: true,
            typeName: "P", memberName: "Foo", overloadIndex: 0);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var changes = (JArray)json["data"]!["changes"]!;
        changes.Select(c => Path.GetFileName(c["filePath"]!.Value<string>()))
            .Should().Equal("a.cs");
    }

    [Theory]
    [InlineData("Draw")]
    [InlineData("IShape.Draw")]
    [InlineData("Lib.IShape.Draw")]
    public async Task ExplicitInterfaceImplementation_ResolvesByName(string memberName)
    {
        var (symbol, error) = await CreateService().ResolveSymbolByNameAsync("Square", memberName);
        error.Should().BeNull();
        symbol!.ToDisplayString().Should().Be("Lib.Square.Lib.IShape.Draw()");
    }

    [Fact]
    public async Task InheritedOnlyMember_NamesItsDeclaringType()
    {
        var (_, error) = await CreateService().ResolveSymbolByNameAsync("Square", "Describe");
        var json = JObject.FromObject(error!);
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        json["error"]!["Message"]!.Value<string>().Should().Be(
            "Member 'Describe' is not declared in 'Lib.Square'; it is inherited from 'Lib.ShapeBase'");
        ((JArray)json["error"]!["Context"]!["declaringTypes"]!).Select(t => t.Value<string>())
            .Should().Equal("Lib.ShapeBase");
    }

    [Fact]
    public async Task SuperMethodByName_ExplicitImplementation_ReportsTheInterfaceMember()
    {
        var result = await CreateService().GetSuperMethodAsync(typeName: "Square", memberName: "Draw");
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["symbol"]!.Value<string>().Should().Be("Lib.Square.Lib.IShape.Draw()");
        ((JArray)json["data"]!["implements"]!).Select(i => i["signature"]!.Value<string>())
            .Should().Equal("Lib.IShape.Draw()");
    }
}
