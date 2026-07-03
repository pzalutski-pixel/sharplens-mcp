using System.Reflection;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.TestAnalyzers;
using Xunit;

namespace SharpLensMcp.Tests;

// find_implementations must surface implementers that live in generator output, flagged
// inGeneratedCode. ImplementerSourceGenerator emits Demo.GenImpl implementing the source
// interface Demo.IGenMarker; SymbolFinder returns the generated implementer (the workspace
// compilation carries generated trees), and find_implementations flags it via the
// IsDeclaredInGeneratedTree check in its primary loop (the ScanGenerated fallback then only
// adds implementers SymbolFinder did not return). This test fails if that flagging is
// removed.
public class GeneratorAwareImplementationsTests
{
    private sealed class DirectLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }

    private const string Code = @"
namespace Demo
{
    public interface IGenMarker { }
    public class Target { public int Hit() => 1; }
}";

    [Fact]
    public async Task FindImplementations_SurfacesGeneratedImplementer()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Marker.cs");
        var fileRef = new AnalyzerFileReference(
            typeof(ImplementerSourceGenerator).Assembly.Location, new DirectLoader());
        var solution = workspace.CurrentSolution.AddAnalyzerReference(document.Project.Id, fileRef);
        workspace.TryApplyChanges(solution);

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var lines = Code.Replace("\r\n", "\n").Split('\n');
        var line = Array.FindIndex(lines, l => l.Contains("interface IGenMarker"));
        var col = lines[line].IndexOf("IGenMarker", StringComparison.Ordinal);

        var data = JObject.FromObject(await service.FindImplementationsAsync(
            document.FilePath!, line, col))["data"]!;

        data["totalImplementations"]!.Value<int>().Should().Be(1,
            "the generated GenImpl is the only implementer of IGenMarker: " + data);
        var impl = (data["implementations"] as JArray)!.Single();
        impl["name"]!.Value<string>().Should().Be("Demo.GenImpl");
        impl["inGeneratedCode"]!.Value<bool>().Should().BeTrue(
            "an implementer declared in generator output is flagged inGeneratedCode: " + data);
        impl["locations"]!.First()!["filePath"]!.Value<string>()!.Should().EndWith("GenImpl.g.cs",
            "the implementer lives in generator output");
    }
}
