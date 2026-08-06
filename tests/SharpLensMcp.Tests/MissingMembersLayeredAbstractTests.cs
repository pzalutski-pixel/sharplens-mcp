using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Regression for issue #13: get_missing_members must not report an abstract member
// as missing when an intermediate abstract base already overrides it. In
// Square : Polygon : Shape, Shape.Area() is abstract, Polygon provides the override,
// and Square compiles with nothing left to implement. The abstract-base check
// inspected only the queried type's OWN declared members (INamedTypeSymbol.GetMembers
// returns directly-declared members only), so Polygon's inherited override was
// invisible and Area() was falsely reported as missing.
public class MissingMembersLayeredAbstractTests
{
    private const string LayeredAbstractHierarchy = @"
namespace Fixtures;

public abstract class Shape
{
    public abstract double Area();
}

public abstract class Polygon : Shape
{
    public override double Area() => 0;
}

public sealed class Square : Polygon
{
}
";

    // An intermediate abstract base RE-ABSTRACTS the member (abstract override), then
    // the concrete leaf implements it. The slot is satisfied at the leaf; nothing missing.
    private const string ReAbstractedThenImplemented = @"
namespace Fixtures;

public abstract class Shape
{
    public abstract double Area();
}

public abstract class Polygon : Shape
{
    public abstract override double Area();
}

public sealed class Square : Polygon
{
    public override double Area() => 4;
}
";

    // Same re-abstraction, but the concrete leaf does NOT implement it (CS0534 — tolerated
    // by the in-memory seam). The slot is unsatisfied and appears as an abstract member at
    // TWO levels (Shape and Polygon); it must be reported exactly once.
    private const string ReAbstractedNotImplemented = @"
namespace Fixtures;

public abstract class Shape
{
    public abstract double Area();
}

public abstract class Polygon : Shape
{
    public abstract override double Area();
}

public sealed class Square : Polygon
{
}
";

    private static (int line, int column) LocateSquare(string code)
    {
        var nameOffset = TestHelpers.FindTextPosition(code, "class Square") + "class ".Length;
        var lineStart = code.LastIndexOf('\n', nameOffset - 1) + 1;
        var lineIndex = code.Substring(0, nameOffset).Count(c => c == '\n');
        return (lineIndex, nameOffset - lineStart);
    }

    [Fact]
    public async Task GetMissingMembers_WhenIntermediateAbstractBaseImplementsMember_ReportsEmpty()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            LayeredAbstractHierarchy, fileName: "LayeredAbstract.cs");

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var (line, column) = LocateSquare(LayeredAbstractHierarchy);

        var result = await service.GetMissingMembersAsync(document.FilePath!, line, column);

        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var data = json["data"]!;
        data["typeName"].Should().NotBeNull();
        data["typeName"]!.Value<string>().Should().EndWith("Square");

        var missing = data["missingMembers"] as JArray;
        missing.Should().NotBeNull();
        missing!.Count.Should().Be(0,
            "Square compiles: Polygon already overrides Shape.Area(), so nothing is missing");
    }

    [Fact]
    public async Task GetMissingMembers_WhenReAbstractedMemberImplementedAtLeaf_ReportsEmpty()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            ReAbstractedThenImplemented, fileName: "ReAbstractImpl.cs");

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var (line, column) = LocateSquare(ReAbstractedThenImplemented);

        var result = await service.GetMissingMembersAsync(document.FilePath!, line, column);

        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var missing = json["data"]!["missingMembers"] as JArray;
        missing.Should().NotBeNull();
        missing!.Count.Should().Be(0,
            "Square implements the re-abstracted Area(), so nothing is missing");
    }

    [Fact]
    public async Task GetMissingMembers_WhenReAbstractedMemberNotImplemented_ReportsOnceFromOriginalBase()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            ReAbstractedNotImplemented, fileName: "ReAbstractMissing.cs");

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var (line, column) = LocateSquare(ReAbstractedNotImplemented);

        var result = await service.GetMissingMembersAsync(document.FilePath!, line, column);

        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var missing = json["data"]!["missingMembers"] as JArray;
        missing.Should().NotBeNull();
        // Area is abstract at BOTH Shape and Polygon — the dedup must report it once.
        missing!.Count.Should().Be(1,
            "the unsatisfied slot must be reported exactly once despite appearing at two levels");
        var entry = missing[0];
        entry["memberName"]!.Value<string>().Should().Be("Area");
        entry["fromAbstractClass"]!.Value<string>().Should().EndWith("Shape",
            "the slot is attributed to the base that originally declared the abstract member");
    }

    // The satisfaction walk resolves an abstract PROPERTY through an intermediate base's
    // override, exercising the IPropertySymbol arm of the override-chain helpers (methods
    // alone would leave that branch unproven).
    private const string LayeredAbstractProperty = @"
namespace Fixtures;

public abstract class Shape
{
    public abstract double Area { get; }
}

public abstract class Polygon : Shape
{
    public override double Area => 0;
}

public sealed class Square : Polygon
{
}
";

    // A grandparent's abstract member with NO override anywhere and a concrete leaf: the
    // member is genuinely missing and must be reported once, attributed to the base that
    // declared it — the reporting counterpart of the intermediate-override case.
    private const string GrandparentAbstractNotOverridden = @"
namespace Fixtures;

public abstract class Shape
{
    public abstract double Area();
}

public abstract class Polygon : Shape
{
}

public sealed class Square : Polygon
{
}
";

    // Overloaded abstract methods where the intermediate base overrides only one: the walk
    // must match slots by override chain, not by name, so the un-overridden overload is the
    // one reported.
    private const string OverloadedAbstractPartiallyOverridden = @"
namespace Fixtures;

public abstract class Shape
{
    public abstract void F();
    public abstract void F(int n);
}

public abstract class Polygon : Shape
{
    public override void F() { }
}

public sealed class Square : Polygon
{
}
";

    [Fact]
    public async Task GetMissingMembers_WhenIntermediateBaseOverridesAbstractProperty_ReportsEmpty()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            LayeredAbstractProperty, fileName: "LayeredAbstractProperty.cs");

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var (line, column) = LocateSquare(LayeredAbstractProperty);

        var result = await service.GetMissingMembersAsync(document.FilePath!, line, column);

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var missing = json["data"]!["missingMembers"] as JArray;
        missing.Should().NotBeNull();
        missing!.Count.Should().Be(0,
            "Polygon already overrides Shape.Area (a property), so nothing is missing on Square");
    }

    [Fact]
    public async Task GetMissingMembers_WhenGrandparentAbstractNeverOverridden_ReportsOnceFromOriginalBase()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            GrandparentAbstractNotOverridden, fileName: "GrandparentAbstract.cs");

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var (line, column) = LocateSquare(GrandparentAbstractNotOverridden);

        var result = await service.GetMissingMembersAsync(document.FilePath!, line, column);

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var missing = json["data"]!["missingMembers"] as JArray;
        missing.Should().NotBeNull();
        missing!.Count.Should().Be(1,
            "Area is abstract in Shape, unoverridden through Polygon, and unimplemented in Square");
        var entry = missing[0];
        entry["memberName"]!.Value<string>().Should().Be("Area");
        entry["fromAbstractClass"]!.Value<string>().Should().EndWith("Shape",
            "the member is attributed to Shape, two levels up, where it was declared");
    }

    [Fact]
    public async Task GetMissingMembers_WhenOnlyOneAbstractOverloadOverridden_ReportsTheOtherOverload()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            OverloadedAbstractPartiallyOverridden, fileName: "OverloadedAbstract.cs");

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var (line, column) = LocateSquare(OverloadedAbstractPartiallyOverridden);

        var result = await service.GetMissingMembersAsync(document.FilePath!, line, column);

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var missing = json["data"]!["missingMembers"] as JArray;
        missing.Should().NotBeNull();
        // F() is satisfied by Polygon's override; F(int) is not — exactly one is missing.
        missing!.Count.Should().Be(1,
            "the parameterless F() is overridden; only the F(int) overload remains unimplemented");
        var entry = missing[0];
        entry["memberName"]!.Value<string>().Should().Be("F");
        entry["signature"]!.Value<string>().Should().Contain("int",
            "the reported overload must be F(int), distinguished by its signature");
    }
}
