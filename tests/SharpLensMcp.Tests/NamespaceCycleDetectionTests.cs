using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_circular_dependencies at the namespace level: every namespace declared in
// a file contributes edges (not just the first), and usings resolve to their
// namespace symbol — so a two-namespace cycle in one file is detected.
public class NamespaceCycleDetectionTests
{
    private const string Code = @"
namespace CycleNsA
{
    using CycleNsB;
    public class A { public B Make() => null; }
}

namespace CycleNsB
{
    using CycleNsA;
    public class B { public A Make() => null; }
}";

    [Fact]
    public async Task FindCircularDependencies_Namespace_DetectsTwoNamespaceCycleInOneFile()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindCircularDependenciesAsync("namespace");
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var data = json["data"]!;
        data["hasCycles"]!.Value<bool>().Should().BeTrue(
            "CycleNsA uses CycleNsB and CycleNsB uses CycleNsA — both namespaces must be keyed, not just the first");
        var cycles = data["cycles"]!.ToString();
        cycles.Should().Contain("CycleNsA");
        cycles.Should().Contain("CycleNsB");
    }
}
