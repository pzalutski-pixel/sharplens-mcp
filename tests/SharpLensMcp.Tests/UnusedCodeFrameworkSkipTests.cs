using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// In-memory tests for find_unused_code covering the framework-bound type skips —
// ImplementsFrameworkInterface and HasFrameworkAttribute
// both run BEFORE the SymbolFinder.FindReferencesAsync call, so types matching
// either filter must be omitted from the unused-symbols list even when they have
// no real references in the workspace.
public class UnusedCodeFrameworkSkipTests
{
    private const string FrameworkInterfaceCode = @"
namespace Microsoft.Extensions.Hosting
{
    public interface IHostedService { }
}

namespace Production
{
    // Implements a real-shaped IHostedService; would otherwise be unused.
    public class MyBackgroundService : Microsoft.Extensions.Hosting.IHostedService { }

    // Has no framework binding and is genuinely unused — must be flagged.
    public class GenuinelyUnused
    {
        public void Foo() { }
    }
}";

    private const string FrameworkAttributeCode = @"
namespace Microsoft.AspNetCore.Mvc
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public class ApiControllerAttribute : System.Attribute { }
}

namespace Production
{
    // Decorated with [ApiController] (HasFrameworkAttribute matches the simple
    // name 'ApiController') — must be skipped even with no references.
    [Microsoft.AspNetCore.Mvc.ApiController]
    public class MyController { }

    // No framework attribute, no references — must be flagged.
    public class PlainUnused
    {
        public void Bar() { }
    }
}";

    // A user attribute whose simple name merely *contains* a framework token
    // ('CollectionView' contains 'Collection', 'MyController' contains
    // 'Controller'). Exact-name matching must NOT suppress the unused class.
    private const string LookalikeAttributeCode = @"
namespace Production
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public class CollectionViewAttribute : System.Attribute { }

    [System.AttributeUsage(System.AttributeTargets.Class)]
    public class MyControllerAttribute : System.Attribute { }

    [CollectionView]
    public class LookalikeOne { }

    [MyController]
    public class LookalikeTwo { }
}";

    private static async Task<JArray> RunAndGetUnused(string code)
    {
        var(workspace, _) = TestHelpers.CreateWorkspaceWithCode(code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var result = await service.FindUnusedCodeAsync(
            projectName: "TestProject",
            includePrivate: false,
            includeInternal: false,
            symbolKindFilter: "Type",
            maxResults: 50);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return (json["data"]!["unusedSymbols"] as JArray)!;
    }

    [Fact]
    public async Task FindUnusedCode_SkipsTypesImplementingFrameworkInterface()
    {
        var unused = await RunAndGetUnused(FrameworkInterfaceCode);
        var names = unused.Select(u => u["fullyQualifiedName"]!.Value<string>()!).ToList();
        names.Should().NotContain(n => n.Contains("MyBackgroundService"),
            "Production.MyBackgroundService implements IHostedService — ImplementsFrameworkInterface must short-circuit before the reference search");
        names.Should().Contain(n => n.Contains("GenuinelyUnused"),
            "Production.GenuinelyUnused has no framework binding and no references — it must be flagged");
    }

    [Fact]
    public async Task FindUnusedCode_SkipsTypesWithFrameworkAttribute()
    {
        var unused = await RunAndGetUnused(FrameworkAttributeCode);
        var names = unused.Select(u => u["fullyQualifiedName"]!.Value<string>()!).ToList();
        names.Should().NotContain(n => n.Contains("MyController"),
            "Production.MyController is decorated with [ApiController] — HasFrameworkAttribute must short-circuit before the reference search");
        names.Should().Contain(n => n.Contains("PlainUnused"),
            "Production.PlainUnused has no framework attribute and no references — it must be flagged");
    }

    [Fact]
    public async Task FindUnusedCode_DoesNotSuppressAttributesMerelyContainingAFrameworkToken()
    {
        var unused = await RunAndGetUnused(LookalikeAttributeCode);
        var names = unused.Select(u => u["fullyQualifiedName"]!.Value<string>()!).ToList();
        names.Should().Contain(n => n.Contains("LookalikeOne"),
            "[CollectionView] is not xUnit's [Collection] — a substring match must not suppress it");
        names.Should().Contain(n => n.Contains("LookalikeTwo"),
            "[MyController] is not [Controller] — a substring match must not suppress it");
    }
}
