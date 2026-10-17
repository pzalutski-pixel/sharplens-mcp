using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_references / find_callers / find_implementations now also surface hits
// inside generator output. The real solution's SourceGeneratorFixture drives
// the System.Text.Json generator, which references FixtureRecord pervasively
// in its generated metadata; an in-memory generator-less solution proves the
// feature stays silent (no inGeneratedCode entries) on the common path.
public class GeneratedReferencesTests : RoslynServiceTestBase
{
    private async Task<JToken> SymbolLocation(string name)
    {
        // The record itself, not the generator's same-named JsonTypeInfo property.
        var search = await Service.SearchSymbolsAsync(name, kind: "Record", maxResults: 20);
        var results = GetData(search)["results"] as JArray;
        results.Should().NotBeNullOrEmpty($"{name} must be findable");
        return results!.First(r => r["name"]!.Value<string>() == name)["location"]!;
    }

    [Fact]
    public async Task FindReferences_OnGeneratorReferencedType_IncludesGeneratedHits()
    {
        var loc = await SymbolLocation("FixtureRecord");

        var result = await Service.FindReferencesAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            maxResults: 500);

        var data = GetData(result);
        var references = (data["references"] as JArray)!;
        var generated = references.Where(r => r["inGeneratedCode"]?.Value<bool>() == true).ToList();

        generated.Should().NotBeEmpty(
            "the JSON source generator references FixtureRecord in its generated metadata");
        generated.Should().OnlyContain(r => r["filePath"]!.Value<string>()!.EndsWith(".g.cs"),
            "generated hits carry the generator's hint path, not an on-disk path");
        generated.Should().OnlyContain(r => !r["filePath"]!.Value<string>()!.Contains('\\'),
            "generated hint paths go through FormatPath like every other location — forward slashes, not raw backslashes");
        data["totalReferences"]!.Value<int>().Should().BeGreaterThanOrEqualTo(generated.Count,
            "totalReferences must include the generated hits");
    }

    [Fact]
    public async Task SourceReferences_AreNotFlaggedGenerated()
    {
        // A symbol referenced only in hand-written source must produce no
        // inGeneratedCode entries — the flag is exclusive to generator output.
        var loc = await SymbolLocation("FixtureRecord");
        var result = await Service.FindReferencesAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            maxResults: 500);

        var references = (GetData(result)["references"] as JArray)!;
        references.Where(r => r["filePath"]!.Value<string>()!.EndsWith("SourceGeneratorFixture.cs"))
            .Should().OnlyContain(r => r["inGeneratedCode"] == null,
                "hand-written source references must not carry the generated flag");
    }

    [Fact]
    public async Task GeneratorLessInMemorySolution_ProducesNoGeneratedHits()
    {
        const string code = """
            namespace Plain;
            public class Service { public int Compute() => 1; }
            public class Caller
            {
                public int Use() => new Service().Compute();
            }
            """;
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "Plain.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var search = await service.SearchSymbolsAsync("Compute", kind: "Method", maxResults: 10);
        var loc = (JObject.FromObject(search)["data"]!["results"] as JArray)!
            .First()["location"]!;

        var result = await service.FindReferencesAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());

        var references = (JObject.FromObject(result)["data"]!["references"] as JArray)!;
        references.Should().NotBeEmpty("Compute is called from Use");
        references.Should().OnlyContain(r => r["inGeneratedCode"] == null,
            "a generator-less solution must produce no generated reference entries");
    }
}
