using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// The count bounds of find_god_objects and find_untested_code are validated like every
// other limit (ParameterValidation): a maxResults of 0 or less, or a negative threshold,
// is INVALID_PARAMETER naming the parameter and its domain, at both levels (ToolPath).
public class QualityLimitValidationTests
{
    private static ToolPathRunner Load(ToolPath toolPath)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(
            "namespace Q { public class C { public void M() { } } }", "Q.cs");
        return new ToolPathRunner(toolPath, workspace);
    }

    [Theory]
    [InlineData(ToolPath.Service, 0, 20, 20, "maxResults must be between 1 and 1000 (got 0)")]
    [InlineData(ToolPath.Dispatcher, 0, 20, 20, "maxResults must be between 1 and 1000 (got 0)")]
    [InlineData(ToolPath.Service, 20, -1, 20, "minEfferentCoupling must be between 0 and 100000 (got -1)")]
    [InlineData(ToolPath.Dispatcher, 20, -1, 20, "minEfferentCoupling must be between 0 and 100000 (got -1)")]
    [InlineData(ToolPath.Service, 20, 20, -3, "minMembers must be between 0 and 100000 (got -3)")]
    [InlineData(ToolPath.Dispatcher, 20, 20, -3, "minMembers must be between 0 and 100000 (got -3)")]
    public async Task GodObjects_OutOfDomainBounds_AreInvalidParameter(
        ToolPath toolPath, int maxResults, int minEfferentCoupling, int minMembers, string message)
    {
        var error = await Load(toolPath).ErrorAsync("find_god_objects",
            new { minEfferentCoupling, minMembers, maxResults },
            s => s.FindGodObjectsAsync(null, minEfferentCoupling, minMembers, maxResults));

        error["code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be(message);
    }

    [Theory]
    [InlineData(ToolPath.Service, 0)]
    [InlineData(ToolPath.Dispatcher, 0)]
    [InlineData(ToolPath.Service, -2)]
    [InlineData(ToolPath.Dispatcher, -2)]
    public async Task UntestedCode_NonPositiveMaxResults_IsInvalidParameter(ToolPath toolPath, int maxResults)
    {
        var error = await Load(toolPath).ErrorAsync("find_untested_code", new { maxResults },
            s => s.FindUntestedCodeAsync(maxResults: maxResults));

        error["code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be($"maxResults must be between 1 and 1000 (got {maxResults})");
    }
}
