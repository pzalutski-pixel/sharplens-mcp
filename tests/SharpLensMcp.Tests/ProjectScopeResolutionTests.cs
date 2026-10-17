using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// RoslynService.ResolveProjectScope / ProjectResolutionError: the project scope a
// project-scoped tool scans for an optional projectName, and the canonical errors for a
// name that resolves to nothing or to several projects. In-process envelope casing:
// success/error lowercase, RoslynError fields PascalCase.
public class ProjectScopeResolutionTests
{
    private static RoslynService Load(Workspace workspace)
    {
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentName_ScopesEveryProject_InLogicalOrder(string? projectName)
    {
        using var fixture = new MultiTargetSolutionFixture();
        var (projects, error) = Load(fixture.Workspace).ResolveProjectScope(projectName);

        error.Should().BeNull();
        projects!.Select(p => p.Name).Should().Equal("Consumer", "Multi(net10.0)", "Multi(net8.0)");
    }

    [Theory]
    [InlineData("Multi", new[] { "Multi(net10.0)", "Multi(net8.0)" })]
    [InlineData("multi(net8.0)", new[] { "Multi(net8.0)" })]
    [InlineData("Consumer", new[] { "Consumer" })]
    public void KnownName_ScopesItsProjects(string projectName, string[] expected)
    {
        using var fixture = new MultiTargetSolutionFixture();
        var (projects, error) = Load(fixture.Workspace).ResolveProjectScope(projectName);

        error.Should().BeNull();
        projects!.Select(p => p.Name).Should().Equal(expected);
    }

    [Fact]
    public void UnknownName_IsInvalidParameter_ListingEveryAcceptedName()
    {
        using var fixture = new MultiTargetSolutionFixture();
        var (projects, error) = Load(fixture.Workspace).ResolveProjectScope("Nope");

        projects.Should().BeNull();
        var json = JObject.FromObject(error!);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be("Project not found: Nope");
        json["error"]!["Hint"]!.Value<string>().Should().Be(
            "Available projects: Consumer, Multi, Multi(net10.0), Multi(net8.0)");
        json["error"]!["Context"]!["projectName"]!.Value<string>().Should().Be("Nope");
        ((JArray)json["error"]!["Context"]!["availableProjects"]!).Select(n => n.Value<string>()).Should().Equal(
            "Consumer", "Multi", "Multi(net10.0)", "Multi(net8.0)");
    }

    [Fact]
    public void ResolvedName_HasNoError()
    {
        using var fixture = new MultiTargetSolutionFixture();
        var service = Load(fixture.Workspace);
        var resolved = service.GetLogicalProjects().Resolve("Multi");

        var error = () => service.ProjectResolutionError(resolved);
        error.Should().Throw<ArgumentException>().WithMessage("A resolved project name has no error*");
    }

    [Fact]
    public void CaseOnlyClash_IsAmbiguousMatch_ListingTheCandidates()
    {
        var root = Path.Combine(Path.GetTempPath(), "scope-case");
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        foreach (var (name, folder) in new[] { ("Tools", "a"), ("tools", "b") })
        {
            solution = solution.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Create(), name, name, LanguageNames.CSharp,
                filePath: Path.Combine(root, folder, $"{name}.csproj"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
        }
        workspace.TryApplyChanges(solution);

        var (projects, error) = Load(workspace).ResolveProjectScope("TOOLS");

        projects.Should().BeNull();
        var json = JObject.FromObject(error!);
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousMatch);
        json["error"]!["Message"]!.Value<string>().Should().Be("Project name 'TOOLS' matches 2 projects");
        ((JArray)json["error"]!["Context"]!["candidates"]!)
            .Select(c => (c["name"]!.Value<string>(), c["filePath"]!.Value<string>()))
            .Should().Equal(
                ("Tools", Path.Combine(root, "a", "Tools.csproj").Replace('\\', '/')),
                ("tools", Path.Combine(root, "b", "tools.csproj").Replace('\\', '/')));
    }
}
