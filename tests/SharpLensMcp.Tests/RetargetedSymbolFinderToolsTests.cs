using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Tools that find cross-project usages through SymbolFinder (or re-resolve the target
// inside each project's compilation) over a solution whose test project sees the
// library through a retargeting assembly. SymbolFinder maps a symbol across the
// solution's compilations itself, so a Lib declaration's usages inside Lib.Tests are
// found whether or not Lib.Tests binds Lib through a retargeting assembly. These lock
// that: retarget: false is the control shape, and both shapes must produce the
// identical result. SymbolFinder's result order is not part of the contract, so lists
// are compared as sets.
public class RetargetedSymbolFinderToolsTests
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
    public async Task FindCallers_FindsTheCallerInTheTestProject(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests, retarget);
        var (line, column) = RetargetedSources.Locate(RetargetedSources.Lib, "Helper(int x)");

        var data = SuccessData(await Load(fixture).FindCallersAsync(fixture.LibFilePath, line, column));

        ((JArray)data["callers"]!)
            .Select(c => (c["callingSymbol"]!["signature"]!.Value<string>()!, c["location"]!["line"]!.Value<int>()))
            .Should().BeEquivalentTo(new[]
            {
                ("Lib.Calculator.Add(int, int)", 6),
                ("Lib.Tests.Uses.Call()", 9)
            });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindReferences_FindsBothReferencesInTheTestProject(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests, retarget);
        var (line, column) = RetargetedSources.Locate(RetargetedSources.Lib, "Add(int a");

        var data = SuccessData(await Load(fixture).FindReferencesAsync(fixture.LibFilePath, line, column));

        data["totalReferences"]!.Value<int>().Should().Be(2);
        ((JArray)data["references"]!)
            .Select(r => (r["line"]!.Value<int>(), r["kind"]!.Value<string>()!))
            .Should().BeEquivalentTo(new[] { (3, "invocation"), (9, "invocation") },
                "CalculatorExtensions.Twice (line 3) and Uses.Call (line 9) of Tests.cs each invoke Add");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindImplementations_IncludesTheTestProjectImplementer(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests, retarget);
        var (line, column) = RetargetedSources.Locate(RetargetedSources.Lib, "IShape {");

        var data = SuccessData(await Load(fixture).FindImplementationsAsync(fixture.LibFilePath, line, column));

        ((JArray)data["implementations"]!).Select(i => i["name"]!.Value<string>()!)
            .Should().BeEquivalentTo("Lib.ShapeBase", "Lib.Tests.Square");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetDerivedTypes_IncludesTheTestProjectSubclass(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests, retarget);

        var data = SuccessData(await Load(fixture).GetDerivedTypesAsync("Lib.ShapeBase"));

        ((JArray)data["derivedTypes"]!)
            .Select(d => (d["fullName"]!.Value<string>()!, d["directBase"]!.Value<string>()!))
            .Should().Equal(("Lib.Tests.Square", "Lib.ShapeBase"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnalyzeMethod_GroupsTheCallerInTheTestProject(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests, retarget);

        var data = SuccessData(await Load(fixture).AnalyzeMethodAsync("Lib.Calculator", "Helper"));

        data["totalCallerGroups"]!.Value<int>().Should().Be(2);
        ((JArray)data["callers"]!).Select(c => c["callingMethod"]!.Value<string>()!)
            .Should().BeEquivalentTo("Lib.Calculator.Add(int, int)", "Lib.Tests.Uses.Call()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetExtensionMethods_FindsTheTestProjectExtension(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests, retarget);

        var data = SuccessData(await Load(fixture).GetExtensionMethodsAsync("Lib.Calculator"));

        var groups = (JArray)data["groups"]!;
        groups.Select(g => g["containingClass"]!.Value<string>()!).Should().Equal("Lib.Tests.CalculatorExtensions");
        ((JArray)groups[0]["members"]!).Select(m => m["signature"]!.Value<string>()!)
            .Should().Equal("Lib.Tests.CalculatorExtensions.Twice(Lib.Calculator, int)");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindUnusedCode_CountsUsagesFromTheTestProject(bool retarget)
    {
        // Add is used only from Lib.Tests; Unused is used nowhere.
        const string lib = """
            namespace Lib
            {
                public class Calculator
                {
                    public int Add(int a, int b) => a + b;
                    public int Unused() => 0;
                }
            }
            """;
        const string tests = """
            namespace Lib.Tests
            {
                public class Uses { public int Call() => new Lib.Calculator().Add(1, 2); }
            }
            """;
        using var fixture = new RetargetedSolutionFixture(lib, tests, retarget);

        var data = SuccessData(await Load(fixture).FindUnusedCodeAsync(
            RetargetedSolutionFixture.LibProjectName, includePrivate: false, includeInternal: false));

        ((JArray)data["unusedSymbols"]!).Select(s => s["fullyQualifiedName"]!.Value<string>()!)
            .Should().Equal("Lib.Calculator.Unused()");
    }
}
