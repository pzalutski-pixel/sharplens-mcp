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

    [Fact]
    public async Task GetMissingMembers_OnIncompleteIDisposable_ReportsDispose()
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

        var missing = json["data"]!["missingMembers"] as JArray;
        missing.Should().NotBeNullOrEmpty(
            "IncompleteDisposable declares : IDisposable but omits Dispose()");
        missing!.Any(m => m["memberName"]?.Value<string>() == "Dispose")
            .Should().BeTrue("Dispose must appear in the missing members list");
        missing!.Any(m => m["fromInterface"]?.Value<string>()?.EndsWith("IDisposable") == true)
            .Should().BeTrue("the missing Dispose must be attributed to IDisposable");
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
}
