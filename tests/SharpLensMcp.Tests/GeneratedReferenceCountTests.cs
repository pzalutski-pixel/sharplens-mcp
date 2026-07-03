using System.Reflection;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.TestAnalyzers;
using Xunit;

namespace SharpLensMcp.Tests;

// SymbolFinder returns references/callers inside generated trees (the workspace compilation
// carries them), so a generated reference must not be counted by BOTH the main loop and the
// generated scan. CallerSourceGenerator emits GenCaller.Invoke calling Demo.Target.Hit —
// exactly one generated reference/caller — so each consumer must count it ONCE, flagged.
public class GeneratedReferenceCountTests
{
    private sealed class DirectLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }

    private const string Code = @"
namespace Demo
{
    public class Target { public int Hit() => 1; }
}";

    private static RoslynService Loaded()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Target.cs");
        var fileRef = new AnalyzerFileReference(typeof(CallerSourceGenerator).Assembly.Location, new DirectLoader());
        var solution = workspace.CurrentSolution.AddAnalyzerReference(document.Project.Id, fileRef);
        workspace.TryApplyChanges(solution);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    private static async Task<(string file, int line, int col)> LocateHit(RoslynService service)
    {
        var search = await service.SearchSymbolsAsync("Hit", kind: "Method", maxResults: 10);
        var loc = JObject.FromObject(search)["data"]!["results"]!.First()!["location"]!;
        return (loc["filePath"]!.Value<string>()!, loc["line"]!.Value<int>(), loc["column"]!.Value<int>());
    }

    [Fact]
    public async Task FindReferences_CountsGeneratedReferenceOnce()
    {
        var service = Loaded();
        var (file, line, col) = await LocateHit(service);

        var data = JObject.FromObject(await service.FindReferencesAsync(file, line, col))["data"]!;

        data["totalReferences"]!.Value<int>().Should().Be(1,
            "the one generated reference to Hit must be counted once, not by both the loop and the scan");
        var refs = (data["references"] as JArray)!;
        refs.Should().ContainSingle()
            .Which["inGeneratedCode"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task FindCallers_CountsGeneratedCallerOnce()
    {
        var service = Loaded();
        var (file, line, col) = await LocateHit(service);

        var result = JObject.FromObject(await service.FindCallersAsync(file, line, col));

        result["meta"]!["TotalCount"]!.Value<int>().Should().Be(1,
            "the one generated caller of Hit must be counted once");
        var callers = (result["data"]!["callers"] as JArray)!;
        callers.Should().ContainSingle()
            .Which["inGeneratedCode"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeMethod_CountsGeneratedCallerGroupOnce()
    {
        var service = Loaded();

        var data = JObject.FromObject(await service.AnalyzeMethodAsync("Target", "Hit", includeCallers: true))["data"]!;

        data["totalCallerGroups"]!.Value<int>().Should().Be(1,
            "the one generated caller group must be counted once, not by SymbolFinder and the scan");
        var callers = (data["callers"] as JArray)!;
        callers.Should().HaveCount(1, "the generated caller group is listed once, not duplicated");
        callers.Single()["inGeneratedCode"]!.Value<bool>().Should().BeTrue();
    }
}
