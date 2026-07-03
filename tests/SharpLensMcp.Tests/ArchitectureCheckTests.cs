using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// check_architecture against ArchitectureFixture's deliberate
// Domain -> Infrastructure violation, plus a project-level rule on this
// solution itself (src must never depend on the test project).
public class ArchitectureCheckTests : RoslynServiceTestBase
{
    private static List<ArchitectureRule> Rules(string from, string deny) =>
        new() { new ArchitectureRule { From = from, Deny = deny } };

    [Fact]
    public async Task DomainToInfrastructureEdge_IsFlaggedWithLocation()
    {
        var result = await Service.CheckArchitectureAsync(
            Rules("Production.Domain*", "Production.Infrastructure*"));

        var data = GetData(result);
        data["level"]!.Value<string>().Should().Be("namespace");
        data["rulesEvaluated"]!.Value<int>().Should().Be(1);

        var violations = (data["violations"] as JArray)!;
        violations.Count.Should().Be(1, "exactly one type depends on the denied namespace");
        // edgesChecked is a whole-solution dependency-edge count (not pinnable exactly),
        // but it must cover at least the single flagged edge.
        data["edgesChecked"]!.Value<int>().Should().BeGreaterThanOrEqualTo(violations.Count,
            "every reported violation is one of the edges that was checked");
        violations[0]["fromType"]!.Value<string>().Should().Be("Production.Domain.OrderAggregate");
        violations[0]["toType"]!.Value<string>().Should().Be("Production.Infrastructure.SqlOrderRepository");
        violations[0]["rule"]!["deny"]!.Value<string>().Should().Be("Production.Infrastructure*");
        var fixtureRel = violations[0]["location"]!["filePath"]!.Value<string>()!;
        fixtureRel.Should().EndWith("ArchitectureFixture.cs");
        // Pin the violation's reported line to the 0-based line of the Domain→Infrastructure
        // construction in the fixture (resolved from a marker) instead of `> 0`.
        var fixtureLines = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(SolutionPath)!, fixtureRel));
        var edgeLine = Array.FindIndex(fixtureLines, l => l.Contains("new Production.Infrastructure.SqlOrderRepository()"));
        edgeLine.Should().BeGreaterThan(-1, "the fixture must construct the Infrastructure type in a Domain body");
        violations[0]["location"]!["line"]!.Value<int>().Should().Be(edgeLine,
            "the violation's reported line must equal the construction's 0-based line");
    }

    [Fact]
    public async Task CompliantRule_ReturnsZeroViolations()
    {
        var result = await Service.CheckArchitectureAsync(
            Rules("Production.Infrastructure*", "Production.Domain*"),
            level: "namespace");

        var data = GetData(result);
        (data["violations"] as JArray)!.Should().BeEmpty(
            "the Infrastructure side never references Domain");
    }

    [Fact]
    public async Task ProjectLevelRule_SrcNeverDependsOnTests()
    {
        var result = await Service.CheckArchitectureAsync(
            Rules("SharpLensMcp", "SharpLensMcp.Tests*"),
            level: "project");

        var data = GetData(result);
        data["level"]!.Value<string>().Should().Be("project");
        (data["violations"] as JArray)!.Should().BeEmpty(
            "the production server must never depend on its test projects");
    }

    [Fact]
    public async Task EmptyRules_ReturnsInvalidParameter()
    {
        var result = await Service.CheckArchitectureAsync(new List<ArchitectureRule>());

        AssertError(result, ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task UnknownLevel_ReturnsInvalidParameter()
    {
        var result = await Service.CheckArchitectureAsync(
            Rules("A*", "B*"), level: "assembly");

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("'namespace' or 'project'");
    }
}
