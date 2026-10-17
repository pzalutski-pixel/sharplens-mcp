using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_god_objects and check_architecture over a solution whose test project sees the
// library through a retargeting assembly. Both pool every project's types into one
// dependency graph: an edge from a Lib.Tests type targets the Lib type as Lib.Tests'
// compilation binds it, and that target must still be recognized as the Lib type the
// graph enumerated from Lib's own compilation. retarget: false is the control shape;
// both shapes must produce the identical result.
public class RetargetedCouplingTests
{
    private static RoslynService Load(RetargetedSolutionFixture fixture)
    {
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(fixture.Workspace);
        return service;
    }

    private static JToken SuccessData(object result)
    {
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("the response envelope must carry success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return json["data"]!;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GodObjects_CountsAfferentCouplingFromTheTestProject(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests, retarget);

        var data = SuccessData(await Load(fixture).FindGodObjectsAsync(
            projectName: null, minEfferentCoupling: 0, minMembers: 0, maxResults: 50));

        // Lib.Calculator is depended on by Uses and CalculatorExtensions, both in Lib.Tests:
        // afferent 2, score = 0*0.4 + 2*0.4 + 2*0.2 = 1.2. A named base type is a
        // dependency: Square (Lib.Tests) derives from ShapeBase, which implements IShape.
        var candidates = ((JArray)data["candidates"]!)
            .Select(c => (
                c["typeName"]!.Value<string>()!,
                c["efferentCoupling"]!.Value<int>(),
                c["afferentCoupling"]!.Value<int>(),
                c["memberCount"]!.Value<int>(),
                c["score"]!.Value<double>()))
            .ToList();
        candidates.Should().Equal(
            ("Lib.Calculator", 0, 2, 2, 1.2),
            ("Lib.ShapeBase", 1, 1, 1, 1.0),
            ("Lib.Tests.CalculatorExtensions", 1, 0, 1, 0.8),
            ("Lib.Tests.Square", 1, 0, 1, 0.8),
            ("Lib.Tests.Uses", 1, 0, 1, 0.8),
            ("Lib.IShape", 0, 1, 1, 0.6));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProjectLevelRule_ResolvesCrossProjectTargetsToTheirProjectName(bool retarget)
    {
        // Lib's assembly is named Contoso.Lib, so a target type mapped to its ASSEMBLY
        // name instead of its project name is observable: it would match the second rule
        // and miss the first.
        using var fixture = new RetargetedSolutionFixture(
            RetargetedSources.Lib, RetargetedSources.Tests, retarget, libAssemblyName: "Contoso.Lib");
        var rules = new List<ArchitectureRule>
        {
            new() { From = "Lib.Tests", Deny = "Lib" },
            new() { From = "Lib.Tests", Deny = "Contoso.Lib" }
        };

        var data = SuccessData(await Load(fixture).CheckArchitectureAsync(rules, level: "project"));

        data["violationCount"]!.Value<int>().Should().Be(3);
        var violations = ((JArray)data["violations"]!)
            .Select(v => (
                v["fromType"]!.Value<string>()!,
                v["toType"]!.Value<string>()!,
                v["rule"]!["deny"]!.Value<string>()!,
                v["location"]!["line"]!.Value<int>()))
            .ToList();
        violations.Should().BeEquivalentTo(new[]
        {
            ("Lib.Tests.Square", "Lib.ShapeBase", "Lib", 2),
            ("Lib.Tests.CalculatorExtensions", "Lib.Calculator", "Lib", 3),
            ("Lib.Tests.Uses", "Lib.Calculator", "Lib", 8)
        }, "every Lib type belongs to project Lib; Contoso.Lib is only its assembly name");
    }
}
