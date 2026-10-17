using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_untested_code scopes its surface diff by logical project: the plain name of a
// multi-targeted project covers every target framework and lists each uncovered member
// once, a framework-qualified name covers that framework alone. Multi is net8.0 + net10.0;
// Modern exists only under NET10_0_OR_GREATER; the consumer's test calls Add.
public class CoverageMultiTargetScopeTests
{
    internal const string MultiSource = """
        namespace Multi
        {
            public class Calc
            {
                public int Add(int a, int b) => a + b;
                public int Never() => 0;
        #if NET10_0_OR_GREATER
                public int Modern() => 1;
        #endif
            }
        }
        """;

    internal static readonly string ConsumerSource = CoverageFrameworkStubs.Source + """

        namespace Consumer.Tests
        {
            public class CalcTests
            {
                [Xunit.Fact]
                public void AddTest() => new Multi.Calc().Add(1, 2);
            }
        }
        """;

    private static async Task<JToken> UntestedAsync(MultiTargetSolutionFixture fixture, string projectName)
    {
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(fixture.Workspace);
        var json = JObject.FromObject(await service.FindUntestedCodeAsync(projectName));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return json["data"]!;
    }

    [Fact]
    public async Task PlainName_CoversEveryTargetFramework_AndListsEachUncoveredMemberOnce()
    {
        using var fixture = new MultiTargetSolutionFixture(MultiSource, ConsumerSource);

        var data = await UntestedAsync(fixture, MultiTargetSolutionFixture.MultiName);

        data["productionProject"]!.Value<string>().Should().Be("Multi");
        ((JArray)data["uncoveredSymbols"]!).Select(s => s["fullName"]!.Value<string>())
            .Should().Equal("Multi.Calc.Modern()", "Multi.Calc.Never()");
    }

    [Fact]
    public async Task FrameworkQualifiedName_CoversThatFrameworkAlone()
    {
        using var fixture = new MultiTargetSolutionFixture(MultiSource, ConsumerSource);

        var data = await UntestedAsync(fixture, MultiTargetSolutionFixture.Net8ProjectName);

        data["productionProject"]!.Value<string>().Should().Be("Multi(net8.0)");
        ((JArray)data["uncoveredSymbols"]!).Select(s => s["fullName"]!.Value<string>())
            .Should().Equal("Multi.Calc.Never()");
    }
}
