using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks check_type_compatibility across project boundaries: ClassifyConversion
// must run in a compilation that can see BOTH types. The old implementation
// resolved each type from whichever project had it and classified in the first
// project's compilation, misreporting cross-project inheritance as "None".
public class CheckTypeCompatibilityCrossProjectTests
{
    private static RoslynService CreateTwoProjectService()
    {
        var workspace = new AdhocWorkspace();
        var baseProjectId = ProjectId.CreateNewId();
        var derivedProjectId = ProjectId.CreateNewId();
        var coreRefs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location)
        };

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                baseProjectId, VersionStamp.Create(), "BaseProject", "BaseProject", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: coreRefs))
            .AddDocument(DocumentId.CreateNewId(baseProjectId), "Base.cs",
                SourceText.From("namespace Shared { public class BaseThing { } }"))
            .AddProject(ProjectInfo.Create(
                derivedProjectId, VersionStamp.Create(), "DerivedProject", "DerivedProject", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: coreRefs,
                projectReferences: new[] { new ProjectReference(baseProjectId) }))
            .AddDocument(DocumentId.CreateNewId(derivedProjectId), "Derived.cs",
                SourceText.From("namespace Consumer { public class DerivedThing : Shared.BaseThing { } }"));

        workspace.TryApplyChanges(solution);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    [Fact]
    public async Task DerivedInSecondProject_IsCompatibleWithBaseInFirstProject()
    {
        var service = CreateTwoProjectService();

        var result = await service.CheckTypeCompatibilityAsync("DerivedThing", "BaseThing");
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        json["data"]!["compatible"]!.Value<bool>().Should().BeTrue(
            "DerivedThing : BaseThing across a project reference is an implicit reference conversion — " +
            "the old first-project classification reported None here");
        json["data"]!["conversionKind"]!.Value<string>().Should().Be("ImplicitReference");
        json["data"]!["requiresCast"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task AmbiguousTypeName_ErrorsInsteadOfPickingAnArbitraryMatch()
    {
        // Two distinct types named Dup. Resolution now routes through the
        // ambiguity-aware resolver, so the conversion check refuses to guess.
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(
            "namespace A { public class Dup { } } namespace B { public class Dup { } }", "Dup.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.CheckTypeCompatibilityAsync("Dup", "object"));
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousType,
            "a name matching two distinct types must error, not silently resolve to the first");
    }
}
