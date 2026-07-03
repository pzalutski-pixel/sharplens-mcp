using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_interceptors against InterceptorFixture: the legacy-form entry must
// resolve to the exact intercepted call line (the attribute's 1-based
// position converted to this repo's zero-based convention, with the line's
// text attached), and the data-form entry must pass its payload through
// undecoded. A solution without any interceptors returns empty cleanly.
public class InterceptorTests : RoslynServiceTestBase
{
    private string FixturePath => Path.Combine(
        Path.GetDirectoryName(SolutionPath)!, "tests", "SharpLensMcp.Tests", "Fixtures", "InterceptorFixture.cs");

    private int FixtureLineOf(string marker)
    {
        var lines = File.ReadAllLines(FixturePath);
        var index = Array.FindIndex(lines, l => l.Contains(marker));
        index.Should().BeGreaterThan(-1, $"the fixture must contain '{marker}'");
        return index;
    }

    [Fact]
    public async Task FindsBothAttributeForms_WithExactTargets()
    {
        var result = await Service.FindInterceptorsAsync(projectName: "SharpLensMcp.Tests");

        var data = GetData(result);
        var interceptors = (data["interceptors"] as JArray)!;
        interceptors.Count.Should().Be(2, "the fixture declares exactly one legacy and one data form");
        interceptors.Should().OnlyContain(i => i["inGeneratedCode"]!.Value<bool>() == false);

        var legacy = interceptors.First(i =>
            i["interceptorMethod"]!.Value<string>() == "InterceptorImpls.InterceptOriginal");
        legacy["target"]!["form"]!.Value<string>().Should().Be("legacy");
        legacy["target"]!["file"]!.Value<string>().Should().Be(
            "tests/SharpLensMcp.Tests/Fixtures/InterceptorFixture.cs");
        legacy["target"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("intercepted-call marker"),
            "the attribute's 1-based line must convert to the zero-based marker line");
        legacy["target"]!["lineText"]!.Value<string>().Should().Contain("Original();",
            "the intercepted line's text must be resolved through the solution document");
        legacy["interceptorLocation"]!["filePath"]!.Value<string>().Should().EndWith("InterceptorFixture.cs");

        var dataForm = interceptors.First(i =>
            i["interceptorMethod"]!.Value<string>() == "InterceptorImpls.InterceptViaData");
        dataForm["target"]!["form"]!.Value<string>().Should().Be("data");
        dataForm["target"]!["version"]!.Value<int>().Should().Be(1);
        dataForm["target"]!["raw"]!.Value<string>().Should().Be("T0tally-0paque-Data==",
            "the data payload passes through undecoded");
    }

    [Fact]
    public async Task SolutionWithoutInterceptors_ReturnsEmptyCleanly()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(
            "namespace Clean; public class Nothing { public void Here() { } }", "Nothing.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindInterceptorsAsync();

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (json["data"]!["interceptors"] as JArray)!.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownProject_ReturnsInvalidParameterListingProjects()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(
            "namespace Clean; public class Nothing { }", "Nothing.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindInterceptorsAsync(projectName: "NoSuchProject");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter,
            "an unknown project name is an invalid argument, routed through ProjectNotFoundError");
        json["error"]!["Hint"]!.Value<string>().Should().Contain("TestProject",
            "the error must list the available project names");
    }

    [Fact]
    public async Task MalformedZeroLiteral_ClampsToNonNegativePositions()
    {
        // A 0 (or negative) line/character literal is malformed; the 0-based
        // conversion must clamp to 0 rather than report -1.
        const string code = """
            namespace System.Runtime.CompilerServices
            {
                [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
                public sealed class InterceptsLocationAttribute : System.Attribute
                {
                    public InterceptsLocationAttribute(string filePath, int line, int character) { }
                }
            }
            namespace App
            {
                using System.Runtime.CompilerServices;
                public static class Interceptors
                {
                    [InterceptsLocation("App.cs", 0, 0)]
                    public static void Bad() { }
                }
            }
            """;
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "App.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var interceptors = (JObject.FromObject(await service.FindInterceptorsAsync())
            ["data"]!["interceptors"] as JArray)!;
        interceptors.Should().ContainSingle();
        var target = interceptors[0]["target"]!;
        target["form"]!.Value<string>().Should().Be("legacy");
        target["line"]!.Value<int>().Should().Be(0, "a 0 literal must clamp to 0, not -1");
        target["column"]!.Value<int>().Should().Be(0, "a 0 literal must clamp to 0, not -1");
    }

    [Fact]
    public async Task MaxResultsOutOfRange_ReturnsInvalidParameter()
    {
        var result = await Service.FindInterceptorsAsync(maxResults: 0);

        AssertError(result, ErrorCodes.InvalidParameter);
    }
}
