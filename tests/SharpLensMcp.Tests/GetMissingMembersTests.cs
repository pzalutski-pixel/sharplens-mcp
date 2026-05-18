using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Tests get_missing_members against incomplete code via the AdhocWorkspace seam
// (LoadFromWorkspaceForTesting). A real on-disk fixture won't work because
// CS0535 is a hardwired compiler error and we don't suppress diagnostics.
// In-memory testing matches how dotnet/roslyn itself tests scenarios where
// the input deliberately doesn't compile.
public class GetMissingMembersTests
{
    private const string IncompleteIDisposableImpl = @"
using System;

namespace Fixtures;

public class IncompleteDisposable : IDisposable
{
    public string ResourceName { get; init; } = """";
    // Dispose() intentionally missing.
}
";

    private const string CompleteClass = @"
namespace Fixtures;

public class FullyImplemented
{
    public int X { get; }
}
";

    // For the abstract-base branch (Inspection.cs:760+). Concrete class derives from
    // abstract base but doesn't override one of the abstract methods.
    private const string IncompleteAbstractInheritor = @"
namespace Fixtures;

public abstract class AbstractBase
{
    public abstract int RequiredOverride();
    public abstract string AnotherRequired();
}

public class PartiallyImplemented : AbstractBase
{
    public override int RequiredOverride() => 42;
    // AnotherRequired() intentionally NOT overridden — must surface as missing
    // with fromAbstractClass field, not fromInterface.
}
";

    // For NotAType: code with only a namespace, no type declarations.
    private const string NoTypeCode = @"
namespace Fixtures;
// Only a namespace declaration — no class/struct/interface here.
";

    [Fact]
    public async Task GetMissingMembers_OnIncompleteIDisposable_ReportsDisposeWithFullEntryShape()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            IncompleteIDisposableImpl, fileName: "Incomplete.cs");

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        // Cursor on the class declaration line: `public class IncompleteDisposable : IDisposable`
        var classLine = TestHelpers.FindTextPosition(IncompleteIDisposableImpl, "class IncompleteDisposable");
        var lineIndex = IncompleteIDisposableImpl.Substring(0, classLine).Count(c => c == '\n');

        var result = await service.GetMissingMembersAsync(document.FilePath!, lineIndex, column: 14);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;

        // Response shape (Inspection.cs:790-795): typeName + isAbstract + interfaces +
        // missingMembers. All four fields must round-trip with locked values.
        data["typeName"]?.Value<string>().Should().EndWith("IncompleteDisposable");
        data["isAbstract"]?.Value<bool>().Should().BeFalse(
            "IncompleteDisposable is concrete, not abstract");
        var interfaces = data["interfaces"] as JArray;
        interfaces.Should().NotBeNullOrEmpty();
        interfaces!.Any(i => i.Value<string>()?.EndsWith("IDisposable") == true)
            .Should().BeTrue("the interfaces field must list IDisposable");

        var missing = data["missingMembers"] as JArray;
        missing.Should().NotBeNullOrEmpty(
            "IncompleteDisposable declares : IDisposable but omits Dispose()");

        // Lock the Dispose entry's full shape — fromInterface, kind, signature.
        var dispose = missing!.First(m => m["memberName"]?.Value<string>() == "Dispose");
        dispose["fromInterface"]?.Value<string>().Should().EndWith("IDisposable",
            "the missing Dispose must be attributed to IDisposable, not fromAbstractClass");
        dispose["fromAbstractClass"].Should().BeNull(
            "interface-sourced entries must NOT carry the fromAbstractClass field");
        dispose["kind"]?.Value<string>().Should().Be("Method");
        dispose["signature"]?.Value<string>().Should().Contain("Dispose",
            "the signature must surface the method name");
        dispose["returnType"]?.Value<string>().Should().Be("void",
            "Dispose() returns void");
    }

    [Fact]
    public async Task GetMissingMembers_OnCompleteClass_ReportsEmpty()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            CompleteClass, fileName: "Complete.cs");

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var classLine = TestHelpers.FindTextPosition(CompleteClass, "class FullyImplemented");
        var lineIndex = CompleteClass.Substring(0, classLine).Count(c => c == '\n');

        var result = await service.GetMissingMembersAsync(document.FilePath!, lineIndex, column: 14);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        var missing = json["data"]!["missingMembers"] as JArray;
        missing.Should().NotBeNull();
        missing!.Count.Should().Be(0, "a class with no interface/abstract obligations has nothing missing");
    }

    [Fact]
    public async Task GetMissingMembers_OnIncompleteAbstractInheritor_ReportsFromAbstractClassBranch()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            IncompleteAbstractInheritor, fileName: "AbstractInheritor.cs");

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        // Point at PartiallyImplemented (which extends AbstractBase but only overrides
        // one of the two abstract members).
        var classLine = TestHelpers.FindTextPosition(IncompleteAbstractInheritor, "class PartiallyImplemented");
        var lineIndex = IncompleteAbstractInheritor.Substring(0, classLine).Count(c => c == '\n');

        var result = await service.GetMissingMembersAsync(document.FilePath!, lineIndex, column: 14);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        data["isAbstract"]?.Value<bool>().Should().BeFalse(
            "PartiallyImplemented is concrete");

        var missing = data["missingMembers"] as JArray;
        missing.Should().NotBeNullOrEmpty();
        // Only AnotherRequired is missing (RequiredOverride is implemented).
        missing!.Count.Should().Be(1,
            "exactly one abstract member is unoverridden");
        var entry = missing[0];
        entry["memberName"]?.Value<string>().Should().Be("AnotherRequired");
        entry["kind"]?.Value<string>().Should().Be("Method");
        entry["returnType"]?.Value<string>().Should().Be("string");
        // The abstract-base branch (Inspection.cs:775-782) uses fromAbstractClass
        // INSTEAD of fromInterface — the field shape distinguishes the source.
        entry["fromAbstractClass"]?.Value<string>().Should().EndWith("AbstractBase",
            "abstract-sourced entries must carry fromAbstractClass");
        entry["fromInterface"].Should().BeNull(
            "abstract-sourced entries must NOT carry the fromInterface field");
    }

    [Fact]
    public async Task GetMissingMembers_AtPositionWithNoType_ReturnsNotAType()
    {
        // No type at the cursor → Inspection.cs:715-722 returns NotAType.
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            NoTypeCode, fileName: "NoType.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.GetMissingMembersAsync(document.FilePath!, line: 1, column: 0);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeFalse();
        json["error"]?["code"]?.Value<string>().Should().Be(ErrorCodes.NotAType);
        json["error"]?["hint"]?.Value<string>().Should().Contain("class or struct");
    }

    [Fact]
    public async Task GetMissingMembers_OnFileNotInSolution_ReturnsFileNotInSolution()
    {
        // No workspace loaded with the requested file — Inspection.cs:678-685.
        // Use a fresh service that has any workspace, then ask for a stranger path.
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(
            CompleteClass, fileName: "Loaded.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var fakePath = Path.Combine(Path.GetTempPath(), $"not-in-workspace-{Guid.NewGuid():N}.cs");
        var result = await service.GetMissingMembersAsync(fakePath, line: 0, column: 0);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeFalse();
        json["error"]?["code"]?.Value<string>().Should().Be(ErrorCodes.FileNotInSolution);
    }
}
