using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// check_architecture at level="project" flags a forbidden dependency between two
// real projects — the positive case (only the namespace-level positive was
// previously covered).
public class ArchitectureProjectLevelTests
{
    private static RoslynService TwoProjectsAppDependsOnInfra()
    {
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location),
        };
        var workspace = new AdhocWorkspace();
        var infraId = ProjectId.CreateNewId();
        var appId = ProjectId.CreateNewId();

        ProjectInfo Proj(ProjectId id, string name, ProjectReference[]? prs = null) => ProjectInfo.Create(
            id, VersionStamp.Create(), name, name, LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: refs, projectReferences: prs);

        var solution = workspace.CurrentSolution
            .AddProject(Proj(infraId, "Infra"))
            .AddDocument(DocumentId.CreateNewId(infraId), "Repo.cs",
                SourceText.From("namespace Infra { public class Repo { public void Save() { } } }"))
            .AddProject(Proj(appId, "App", new[] { new ProjectReference(infraId) }))
            .AddDocument(DocumentId.CreateNewId(appId), "Service.cs",
                SourceText.From("namespace App { public class Service { public void Run(Infra.Repo r) { r.Save(); } } }"));
        workspace.TryApplyChanges(solution).Should().BeTrue();

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    [Fact]
    public async Task ProjectLevelRule_FlagsForbiddenCrossProjectDependency()
    {
        var service = TwoProjectsAppDependsOnInfra();
        var rules = new List<ArchitectureRule> { new() { From = "App", Deny = "Infra" } };

        var json = JObject.FromObject(await service.CheckArchitectureAsync(rules, level: "project"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        data["level"]!.Value<string>().Should().Be("project");

        var violations = (data["violations"] as JArray)!;
        violations.Should().NotBeEmpty("App depends on Infra, which the rule forbids");
        violations.Should().Contain(v =>
            v["fromType"]!.Value<string>()!.Contains("Service") &&
            v["toType"]!.Value<string>()!.Contains("Repo"));
    }

    [Fact]
    public async Task ProjectLevelRule_AllowedDirection_HasNoViolation()
    {
        var service = TwoProjectsAppDependsOnInfra();
        // Infra does not depend on App, so this rule holds.
        var rules = new List<ArchitectureRule> { new() { From = "Infra", Deny = "App" } };

        var json = JObject.FromObject(await service.CheckArchitectureAsync(rules, level: "project"));
        (json["data"]!["violations"] as JArray)!.Should().BeEmpty(
            "Infra has no dependency on App");
    }
}
