using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_unused_code skips types that implement a framework interface, but the match
// must be exact (or a generic instantiation), not a substring: a type whose
// interface name merely starts with a framework type's name is NOT a framework type.
public class ImplementsFrameworkInterfaceMatchTests
{
    private const string Code = @"
namespace FluentValidation
{
    public interface IValidator { }
    public interface IValidatorMetadata { }
}
namespace Demo
{
    public class RealValidator : FluentValidation.IValidator { }
    public class FakeMeta : FluentValidation.IValidatorMetadata { }
}
";

    [Fact]
    public async Task SubstringFrameworkInterfaceName_IsNotTreatedAsFramework()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Validators.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindUnusedCodeAsync(
            "TestProject", includePrivate: false, includeInternal: false, symbolKindFilter: null);
        var names = (JObject.FromObject(result)["data"]!["unusedSymbols"] as JArray)!
            .Select(s => s["fullyQualifiedName"]!.Value<string>()!).ToList();

        names.Should().Contain("Demo.FakeMeta",
            "FakeMeta implements FluentValidation.IValidatorMetadata — a distinct type whose name merely starts with the framework entry, so an unused one must be reported");
        names.Should().NotContain("Demo.RealValidator",
            "RealValidator genuinely implements FluentValidation.IValidator and must be skipped as a framework implementation");
    }
}
