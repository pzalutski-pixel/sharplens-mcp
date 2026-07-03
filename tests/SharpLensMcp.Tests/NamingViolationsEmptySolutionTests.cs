using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_naming_violations must not call ProjectNotFoundError(null): a null projectName
// on a solution with no projects is a clean empty result, while an unknown name is the
// structured InvalidParameter error — matching the sibling tools.
public class NamingViolationsEmptySolutionTests
{
    [Fact]
    public async Task NullProjectName_EmptySolution_ReturnsEmptySuccess()
    {
        var workspace = new AdhocWorkspace();
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.FindNamingViolationsAsync(projectName: null));
        json["success"]!.Value<bool>().Should().BeTrue(
            "a null project name on an empty solution is not an error: " + json);
        (json["data"]!["violations"] as JArray)!.Should().BeEmpty(
            "an empty solution has no symbols to flag");
    }

    [Fact]
    public async Task UnknownProjectName_ReturnsInvalidParameter()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(
            "namespace N { public class C { } }", "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.FindNamingViolationsAsync(projectName: "DoesNotExist"));
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }
}
