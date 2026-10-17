using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace SharpLensMcp.Tests;

// The logical-project model: grouping per-target-framework projects by project file,
// name parsing, ordering, and resolution of a user-supplied projectName.
public class LogicalProjectModelTests
{
    [Theory]
    [InlineData("Multi(net8.0)", "Multi", "net8.0")]
    [InlineData("Multi(net10.0)", "Multi", "net10.0")]
    [InlineData("Lib.Core(netstandard2.0)", "Lib.Core", "netstandard2.0")]
    [InlineData("App(net8.0-windows10.0.19041)", "App", "net8.0-windows10.0.19041")]
    [InlineData("Legacy(net48)", "Legacy", "net48")]
    [InlineData("Plain", "Plain", null)]
    [InlineData("Tool(beta)", "Tool(beta)", null)]
    [InlineData("Tool()", "Tool()", null)]
    [InlineData("(net8.0)", "(net8.0)", null)]
    public void SplitName_SeparatesOnlyAFrameworkShapedSuffix(string projectName, string name, string? framework)
    {
        LogicalProject.SplitName(projectName).Should().Be((name, framework));
    }

    [Fact]
    public void Build_GroupsTheFrameworkProjectsOfOneFile()
    {
        using var fixture = new MultiTargetSolutionFixture();
        var model = LogicalProjectModel.Build(fixture.Workspace.CurrentSolution);

        model.Projects.Select(p => p.Name).Should().Equal("Consumer", "Multi");

        var consumer = model.Projects[0];
        consumer.FilePath.Should().Be(fixture.ConsumerProjectFilePath);
        consumer.Projects.Select(p => p.Name).Should().Equal("Consumer");
        consumer.TargetFrameworks.Should().BeEmpty();
        consumer.IsMultiTargeted.Should().BeFalse();

        var multi = model.Projects[1];
        multi.FilePath.Should().Be(fixture.MultiProjectFilePath);
        multi.Projects.Select(p => p.Name).Should().Equal("Multi(net10.0)", "Multi(net8.0)");
        multi.TargetFrameworks.Should().Equal("net10.0", "net8.0");
        multi.IsMultiTargeted.Should().BeTrue();

        model.Of(fixture.Workspace.CurrentSolution.GetProject(fixture.Net8ProjectId)!).Should().BeSameAs(multi);
        model.Of(fixture.ConsumerProjectId).Should().BeSameAs(consumer);
        LogicalProject.TargetFrameworkOf(multi.Projects[1]).Should().Be("net8.0");
        LogicalProject.TargetFrameworkOf(consumer.Projects[0]).Should().BeNull();
    }

    [Fact]
    public void ValidNames_ListPlainThenFrameworkQualifiedNames()
    {
        using var fixture = new MultiTargetSolutionFixture();
        LogicalProjectModel.Build(fixture.Workspace.CurrentSolution).ValidNames
            .Should().Equal("Consumer", "Multi", "Multi(net10.0)", "Multi(net8.0)");
    }

    [Theory]
    [InlineData("Multi", "Multi", new[] { "Multi(net10.0)", "Multi(net8.0)" })]
    [InlineData("multi", "Multi", new[] { "Multi(net10.0)", "Multi(net8.0)" })]
    [InlineData("Multi(net8.0)", "Multi", new[] { "Multi(net8.0)" })]
    [InlineData("MULTI(NET10.0)", "Multi", new[] { "Multi(net10.0)" })]
    [InlineData("consumer", "Consumer", new[] { "Consumer" })]
    public void Resolve_PlainOrFrameworkQualifiedName_CaseInsensitively(
        string requested, string logicalName, string[] expectedProjects)
    {
        using var fixture = new MultiTargetSolutionFixture();
        var resolution = LogicalProjectModel.Build(fixture.Workspace.CurrentSolution).Resolve(requested);

        resolution.Status.Should().Be(ProjectResolutionStatus.Resolved);
        resolution.RequestedName.Should().Be(requested);
        resolution.Project!.Name.Should().Be(logicalName);
        resolution.Projects.Select(p => p.Name).Should().Equal(expectedProjects);
        resolution.Candidates.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Nope")]
    [InlineData("Multi(net9.0)")]
    [InlineData("Multi(")]
    [InlineData("")]
    public void Resolve_UnknownName_IsNotFound_WithEveryValidName(string requested)
    {
        using var fixture = new MultiTargetSolutionFixture();
        var resolution = LogicalProjectModel.Build(fixture.Workspace.CurrentSolution).Resolve(requested);

        resolution.Status.Should().Be(ProjectResolutionStatus.NotFound);
        resolution.Project.Should().BeNull();
        resolution.Projects.Should().BeEmpty();
        resolution.ValidNames.Should().Equal("Consumer", "Multi", "Multi(net10.0)", "Multi(net8.0)");
    }

    // A solution of single-target projects with the given (name, project file) pairs; a
    // null file leaves the project without a FilePath.
    private static Solution Projects(params (string Name, string? FilePath)[] projects)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        foreach (var (name, filePath) in projects)
        {
            solution = solution.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Create(), name, name, LanguageNames.CSharp,
                filePath: filePath,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
        }
        return solution;
    }

    [Fact]
    public void Resolve_NamesDifferingOnlyInCase_ExactCaseWins_OtherwiseAmbiguous()
    {
        var root = Path.Combine(Path.GetTempPath(), "logical-case");
        var solution = Projects(
            ("Tools", Path.Combine(root, "a", "Tools.csproj")),
            ("tools", Path.Combine(root, "b", "tools.csproj")));
        var model = LogicalProjectModel.Build(solution);

        model.Resolve("tools").Projects.Select(p => p.FilePath).Should().Equal(Path.Combine(root, "b", "tools.csproj"));
        model.Resolve("Tools").Projects.Select(p => p.FilePath).Should().Equal(Path.Combine(root, "a", "Tools.csproj"));

        var ambiguous = model.Resolve("TOOLS");
        ambiguous.Status.Should().Be(ProjectResolutionStatus.Ambiguous);
        ambiguous.Projects.Should().BeEmpty();
        ambiguous.Candidates.Select(c => c.FilePath).Should().Equal(
            Path.Combine(root, "a", "Tools.csproj"), Path.Combine(root, "b", "tools.csproj"));
    }

    [Fact]
    public void Build_SameFileThroughADotSegment_IsOneLogicalProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "logical-dot");
        var solution = Projects(
            ("Multi(net8.0)", Path.Combine(root, "Multi", "Multi.csproj")),
            ("Multi(net10.0)", Path.Combine(root, "Multi", ".", "Multi.csproj")));

        var model = LogicalProjectModel.Build(solution);

        model.Projects.Should().ContainSingle();
        model.Projects[0].Name.Should().Be("Multi");
        model.Projects[0].TargetFrameworks.Should().Equal("net10.0", "net8.0");
    }

    [Fact]
    public void Build_ProjectsWithoutAFile_AreEachTheirOwnLogicalProject_InNameOrder()
    {
        var solution = Projects(("beta", null), ("Alpha", null), ("alpha", null));
        var model = LogicalProjectModel.Build(solution);

        model.Projects.Select(p => p.Name).Should().Equal(new[] { "Alpha", "alpha", "beta" },
            "logical projects are ordered by name ignoring case, then ordinally");
        model.Projects.Should().OnlyContain(p => p.FilePath == null && p.Projects.Count == 1);
    }

    [Fact]
    public void Build_EmptySolution_HasNoProjectsAndResolvesNothing()
    {
        var model = LogicalProjectModel.Build(new AdhocWorkspace().CurrentSolution);

        model.Projects.Should().BeEmpty();
        model.ValidNames.Should().BeEmpty();
        model.Resolve("Any").Status.Should().Be(ProjectResolutionStatus.NotFound);
    }
}
