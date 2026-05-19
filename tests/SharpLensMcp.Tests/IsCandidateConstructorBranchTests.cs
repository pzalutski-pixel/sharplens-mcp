using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// In-memory test for the IsCandidate Constructor branch at Quality.cs:206-208.
// The Ordinary || Constructor permit-list at line 206-207 is what lets explicit
// public ctors surface in find_untested_code's uncovered list — a regression
// here would silently drop ctors from the report.
public class IsCandidateConstructorBranchTests
{
    private const string Code = @"
namespace Production
{
    public class CtorTarget
    {
        // Explicit public ctor. With no test attribute in the solution, every
        // candidate member surfaces as uncovered; the ctor must appear in the
        // list (kind=Method, fullName ends with .CtorTarget).
        public CtorTarget()
        {
        }

        public int Sibling()
        {
            return 0;
        }
    }
}";

    [Fact]
    public async Task FindUntestedCode_FlagsExplicitPublicConstructor()
    {
        var(workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var result = await service.FindUntestedCodeAsync(projectName: null, includeProperties: false, includeInternal: false, maxResults: 50);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var uncovered = (json["data"]!["uncoveredSymbols"] as JArray)!;
        var ctorEntry = uncovered.FirstOrDefault(u =>
        {
            var name = u["fullName"]!.Value<string>()!;
            return name.Contains("CtorTarget.CtorTarget()") || name.EndsWith(".CtorTarget()");
        });
        ctorEntry.Should().NotBeNull(
            "the public CtorTarget() constructor must surface as uncovered — IsCandidate's `m.MethodKind == MethodKind.Constructor` branch (Quality.cs:206-207) is what permits it");
        ctorEntry!["kind"]!.Value<string>().Should().Be("Method",
            "constructors are IMethodSymbol → ISymbol.Kind == Method");
        ctorEntry["accessibility"]!.Value<string>().Should().Be("Public");
    }
}
