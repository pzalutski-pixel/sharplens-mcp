using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks name-based type resolution against silent first-match selection: a
// name matching several distinct types must return AMBIGUOUS_TYPE with the
// fully-qualified candidates instead of picking one arbitrarily — name-based
// tools feed refactorings, and the wrong pick mutates the wrong type.
// Exercised through get_type_members, which resolves via FindTypeByNameAsync.
public class AmbiguousTypeResolutionTests
{
    private const string DistinctNamesCode = @"
namespace Production
{
    public class Widget
    {
        public void Spin() { }
    }
}

namespace Legacy
{
    public class WidgetFactory { }
}";

    private const string DuplicateSimpleNamesCode = @"
namespace Production
{
    public class Item
    {
        public void Use() { }
    }
}

namespace Legacy
{
    public class Item
    {
        public void Archive() { }
    }
}";

    private static RoslynService CreateService(string code)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    [Fact]
    public async Task ExactSimpleName_BeatsPartialMatches()
    {
        // "Widget" matches Widget exactly and WidgetFactory partially — the
        // exact tier must resolve without consulting the partial tier.
        var service = CreateService(DistinctNamesCode);
        var result = await service.GetTypeMembersAsync("Widget");
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["typeName"]!.Value<string>().Should().Be("Production.Widget");
    }

    [Fact]
    public async Task PartialNameMatchingTwoTypes_ReturnsAmbiguousTypeWithCandidates()
    {
        var service = CreateService(DistinctNamesCode);
        var result = await service.GetTypeMembersAsync("Widg");
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse(
            "a partial name matching two types must not silently pick one");
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousType);
        var candidates = (json["error"]!["Context"]!["candidates"] as JArray)!
            .Select(c => c.Value<string>())
            .ToList();
        candidates.Should().BeEquivalentTo(new[] { "Legacy.WidgetFactory", "Production.Widget" });
    }

    [Fact]
    public async Task SameSimpleNameInTwoNamespaces_ReturnsAmbiguousType()
    {
        var service = CreateService(DuplicateSimpleNamesCode);
        var result = await service.GetTypeMembersAsync("Item");
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse(
            "two exact-name matches are just as ambiguous as two partial matches");
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousType);
        var candidates = (json["error"]!["Context"]!["candidates"] as JArray)!
            .Select(c => c.Value<string>())
            .ToList();
        candidates.Should().BeEquivalentTo(new[] { "Legacy.Item", "Production.Item" });
    }

    [Fact]
    public async Task FullyQualifiedName_ResolvesDespiteDuplicateSimpleNames()
    {
        var service = CreateService(DuplicateSimpleNamesCode);
        var result = await service.GetTypeMembersAsync("Production.Item");
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["typeName"]!.Value<string>().Should().Be("Production.Item");
    }

    [Fact]
    public async Task SameSimpleNameAcrossTwoProjects_ReturnsAmbiguousType()
    {
        // The old resolution looped projects and returned the first project's
        // match without ever seeing the second project's type.
        var workspace = new AdhocWorkspace();
        var firstProjectId = ProjectId.CreateNewId();
        var secondProjectId = ProjectId.CreateNewId();
        var coreRefs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location)
        };

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                firstProjectId, VersionStamp.Create(), "FirstLib", "FirstLib", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: coreRefs))
            .AddDocument(DocumentId.CreateNewId(firstProjectId), "First.cs",
                SourceText.From("namespace First { public class Duplicated { } }"))
            .AddProject(ProjectInfo.Create(
                secondProjectId, VersionStamp.Create(), "SecondLib", "SecondLib", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: coreRefs))
            .AddDocument(DocumentId.CreateNewId(secondProjectId), "Second.cs",
                SourceText.From("namespace Second { public class Duplicated { } }"));

        workspace.TryApplyChanges(solution);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.GetTypeMembersAsync("Duplicated");
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse(
            "the candidate collection must span every project, not stop at the first hit");
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousType);
        var candidates = (json["error"]!["Context"]!["candidates"] as JArray)!
            .Select(c => c.Value<string>())
            .ToList();
        candidates.Should().BeEquivalentTo(new[] { "First.Duplicated", "Second.Duplicated" });
    }

    [Fact]
    public async Task ManyAmbiguousCandidates_ReportsTrueTotalAndCapsTheList()
    {
        // Eleven types named Dup: the message must report the true total while the
        // candidate list is capped, and the two must not read as disagreeing.
        var code = string.Concat(Enumerable.Range(0, 11)
            .Select(i => $"namespace N{i} {{ public class Dup {{ }} }}\n"));
        var service = CreateService(code);

        var json = JObject.FromObject(await service.GetTypeMembersAsync("Dup"));
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousType);
        json["error"]!["Message"]!.Value<string>().Should().Contain("matches 11 types");

        var ctx = json["error"]!["Context"]!;
        ctx["totalCandidates"]!.Value<int>().Should().Be(11, "the message's count must be backed by totalCandidates");
        ctx["candidatesShown"]!.Value<int>().Should().Be(10);
        ctx["truncated"]!.Value<bool>().Should().BeTrue();
        (ctx["candidates"] as JArray)!.Count.Should().Be(10,
            "the list is capped at ten while the total is disclosed separately");
    }
}
