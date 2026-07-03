using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// When two distinct source assemblies declare the same fully-qualified type name,
// resolving that FQN must report ambiguity instead of returning whichever
// compilation was iterated first.
public class ResolveTypeFqnAmbiguityTests
{
    private static RoslynService TwoAssembliesDeclaringSameFqn()
    {
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location),
        };
        var workspace = new AdhocWorkspace();
        var aId = ProjectId.CreateNewId();
        var bId = ProjectId.CreateNewId();

        ProjectInfo Proj(ProjectId id, string name) => ProjectInfo.Create(
            id, VersionStamp.Create(), name, name, LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: refs);

        var solution = workspace.CurrentSolution
            .AddProject(Proj(aId, "AsmA"))
            .AddDocument(DocumentId.CreateNewId(aId), "A.cs",
                SourceText.From("namespace Shared { public class Config { } }"))
            .AddProject(Proj(bId, "AsmB"))
            .AddDocument(DocumentId.CreateNewId(bId), "B.cs",
                SourceText.From("namespace Shared { public class Config { } }"));
        workspace.TryApplyChanges(solution).Should().BeTrue();

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    [Fact]
    public async Task SameFqnInTwoAssemblies_ResolvesAsAmbiguous()
    {
        var service = TwoAssembliesDeclaringSameFqn();

        var json = JObject.FromObject(await service.GetTypeMembersAsync("Shared.Config"));
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousType,
            "two assemblies declare Shared.Config — the FQN match must not silently pick one");
    }
}
