using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// In-memory tests for the EstimateCyclomaticAsync formula (1 + branches) at
// Quality.cs:226-241 AND the descending sort at Quality.cs:166. Builds a
// fixture with three methods of deterministic and distinct branch counts,
// runs find_untested_code (no test methods → all production methods are
// uncovered), and asserts:
//   - each method's complexity == 1 + counted branches
//   - results are returned in descending complexity order
public class EstimateCyclomaticAndSortTests
{
    private const string Code = @"
namespace Production
{
    public class Target
    {
        public int Trivial()
        {
            return 0;
        }

        public int Three(int x)
        {
            if (x > 0) return 1;
            if (x < 0) return -1;
            for (int i = 0; i < 10; i++) return 0;
            return 0;
        }

        public int Six(int x)
        {
            if (x > 0)
            {
                if (x > 10)
                {
                    if (x > 100) return 3;
                }
            }
            for (int i = 0; i < 10; i++) { }
            for (int j = 0; j < 10; j++) { }
            switch (x)
            {
                case 0: return 0;
            }
            return 0;
        }
    }
}";

    private static async Task<JArray> RunAndGetUncovered()
    {
        var(workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var result = await service.FindUntestedCodeAsync(projectName: null, includeProperties: false, includeInternal: false, maxResults: 50);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return (json["data"]!["uncoveredSymbols"] as JArray)!;
    }

    [Fact]
    public async Task EstimateCyclomatic_TrivialMethod_ReturnsOne()
    {
        var uncovered = await RunAndGetUncovered();
        var trivial = uncovered.First(u => u["fullName"]!.Value<string>()!.Contains("Trivial"));
        trivial["complexity"]!.Value<int>().Should().Be(1,
            "1 + 0 branches = 1 (no if/for/foreach/while/case/catch/conditional)");
    }

    [Fact]
    public async Task EstimateCyclomatic_ThreeBranches_ReturnsFour()
    {
        var uncovered = await RunAndGetUncovered();
        var three = uncovered.First(u => u["fullName"]!.Value<string>()!.Contains("Three"));
        three["complexity"]!.Value<int>().Should().Be(4,
            "1 + (2 IfStatementSyntax + 1 ForStatementSyntax) = 4");
    }

    [Fact]
    public async Task EstimateCyclomatic_SixBranches_ReturnsSeven()
    {
        var uncovered = await RunAndGetUncovered();
        var six = uncovered.First(u => u["fullName"]!.Value<string>()!.Contains("Six"));
        six["complexity"]!.Value<int>().Should().Be(7,
            "1 + (3 IfStatementSyntax + 2 ForStatementSyntax + 1 CaseSwitchLabelSyntax) = 7");
    }

    [Fact]
    public async Task FindUntestedCode_SortsResults_ByComplexityDescending()
    {
        var uncovered = await RunAndGetUncovered();
        var targets = uncovered
            .Where(u =>
            {
                var n = u["fullName"]!.Value<string>()!;
                return n.Contains("Trivial") || n.Contains("Three") || n.Contains("Six");
            })
            .Select(u => (
                name: u["fullName"]!.Value<string>()!,
                complexity: u["complexity"]!.Value<int>()))
            .ToList();
        targets.Should().HaveCount(3, "all three production methods must surface as uncovered");
        var complexities = targets.Select(t => t.complexity).ToList();
        complexities.Should().BeInDescendingOrder("Quality.cs:166 sorts uncovered by complexity descending");
        complexities[0].Should().Be(7);
        complexities[1].Should().Be(4);
        complexities[2].Should().Be(1);
    }
}
