using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// In-memory tests for find_reflection_usage covering the System.Activator and
// System.Delegate.DynamicInvoke branches at Discovery.cs:192-193. Each branch
// is reached by exact (namespace, type, method) tuples beyond the
// System.Reflection.* catch-all on line 190.
public class FindReflectionUsageActivatorTests
{
    private const string Code = @"
namespace Production
{
    public class Target { }

    public class UsesReflection
    {
        public void CallActivator()
        {
            // Hits the Activator branch: ns=System, type=Activator.
            var instance = System.Activator.CreateInstance(typeof(Target));
        }

        public void CallDynamicInvoke()
        {
            System.Action<int> handler = x => { };
            // Hits the Delegate.DynamicInvoke branch: ns=System, type=Delegate,
            // method=DynamicInvoke. (DynamicInvoke is declared on System.Delegate;
            // all delegate types inherit it.)
            handler.DynamicInvoke(1);
        }
    }
}";

    private static async Task<JArray> RunAndGetUsages()
    {
        var(workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var result = await service.FindReflectionUsageAsync();
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return (json["data"]!["usages"] as JArray)!;
    }

    [Fact]
    public async Task FindReflectionUsage_DetectsActivatorCreateInstance()
    {
        var usages = await RunAndGetUsages();
        var apis = usages.Select(u => u["reflectionApi"]!.Value<string>()!).ToList();
        apis.Should().Contain("Activator.CreateInstance",
            "the impl emits reflectionApi as `{ContainingTypeName}.{MethodName}` for the Activator branch");
        var entry = usages.First(u => u["reflectionApi"]!.Value<string>() == "Activator.CreateInstance");
        entry["context"]!.Value<string>()!.Should().Contain("typeof(Target)",
            "the captured context substring must include the call expression's typeof argument");
    }

    [Fact]
    public async Task FindReflectionUsage_DetectsDelegateDynamicInvoke()
    {
        var usages = await RunAndGetUsages();
        var apis = usages.Select(u => u["reflectionApi"]!.Value<string>()!).ToList();
        apis.Should().Contain("Delegate.DynamicInvoke",
            "the impl emits reflectionApi as `Delegate.DynamicInvoke` only when both type AND method match (other Delegate methods stay out)");
    }
}
