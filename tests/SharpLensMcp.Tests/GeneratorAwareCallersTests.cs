using System.Reflection;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.TestAnalyzers;
using Xunit;

namespace SharpLensMcp.Tests;

// find_callers surfaces callers inside generator output; get_call_graph and
// analyze_method must do the same. CallerSourceGenerator (loaded from the TestAnalyzers
// DLL via AnalyzerFileReference, which SymbolFinder recognizes) emits a method that
// calls Demo.Target.Hit, giving Hit exactly one generated caller.
public class GeneratorAwareCallersTests
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
        var dllPath = typeof(CallerSourceGenerator).Assembly.Location;
        var fileRef = new AnalyzerFileReference(dllPath, new DirectLoader());
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
    public async Task FindCallers_SurfacesGeneratedCaller()
    {
        var service = Loaded();
        var (file, line, col) = await LocateHit(service);

        var callers = (JObject.FromObject(await service.FindCallersAsync(file, line, col))["data"]!["callers"] as JArray)!;
        callers.Should().Contain(c => c["inGeneratedCode"] != null && c["inGeneratedCode"]!.Value<bool>(),
            "the generated GenCaller.Invoke calls Target.Hit");
    }

    [Fact]
    public async Task GetCallGraph_Callers_IncludesGeneratedCaller()
    {
        var service = Loaded();
        var (file, line, col) = await LocateHit(service);

        var data = JObject.FromObject(await service.GetCallGraphAsync(
            file, line, col, direction: "callers", maxDepth: 2, maxNodes: 50))["data"]!;
        (data["nodes"] as JArray)!.Select(n => n["fullName"]!.Value<string>())
            .Should().Contain(n => n!.Contains("GenCaller.Invoke"),
                "the call graph must include the caller that lives in generated code");
    }

    [Fact]
    public async Task AnalyzeMethod_Callers_IncludeGeneratedCaller()
    {
        var service = Loaded();

        var data = JObject.FromObject(await service.AnalyzeMethodAsync("Target", "Hit", includeCallers: true))["data"]!;
        (data["callers"] as JArray)!.Should().Contain(
            c => c["inGeneratedCode"] != null && c["inGeneratedCode"]!.Value<bool>()
                && c["callingMethod"]!.Value<string>()!.Contains("GenCaller.Invoke"),
            "analyze_method must surface the generated caller, like find_callers");
    }
}
