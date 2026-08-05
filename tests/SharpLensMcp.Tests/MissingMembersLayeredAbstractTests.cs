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
}
