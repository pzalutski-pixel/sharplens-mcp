using System.Reflection;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.TestAnalyzers;
using Xunit;

namespace SharpLensMcp.Tests;

// A call inside generator output is attributed to the generated member that contains it,
// like a call in hand-written source: LambdaCallerSourceGenerator emits
// GenLambdaCaller.Make, whose lambda calls Demo.LambdaTarget.Hit, so find_callers and the
// call graph report Make (flagged inGeneratedCode), never the lambda. Both levels
// (ToolPath).
public class GeneratedCallerAttributionTests
{
    private sealed class DirectLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }

    private const string Code = """
        namespace Demo
        {
            public class Target { public int Hit() => 1; }
            public class LambdaTarget { public int Hit() => 2; }
        }
        """;

    private static (ToolPathRunner runner, string path, int line, int column) Load(ToolPath toolPath)
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Targets.cs");
        var reference = new AnalyzerFileReference(typeof(LambdaCallerSourceGenerator).Assembly.Location, new DirectLoader());
        workspace.TryApplyChanges(workspace.CurrentSolution.AddAnalyzerReference(document.Project.Id, reference));
        var (line, column) = RetargetedSources.Locate(Code, "Hit() => 2");
        return (new ToolPathRunner(toolPath, workspace), document.FilePath!, line, column);
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task FindCallers_CallInAGeneratedLambda_IsTheContainingGeneratedMember(ToolPath toolPath)
    {
        var (runner, path, line, column) = Load(toolPath);

        var data = await runner.DataAsync("find_callers", new { filePath = path, line, column },
            s => s.FindCallersAsync(path, line, column));

        var caller = ((JArray)data["callers"]!).Single();
        caller["callingSymbol"]!["name"]!.Value<string>().Should().Be("Make");
        caller["callingSymbol"]!["signature"]!.Value<string>().Should().Be("Demo.GenLambdaCaller.Make()");
        caller["inGeneratedCode"]!.Value<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task CallGraphCallers_CallInAGeneratedLambda_IsTheContainingGeneratedMember(ToolPath toolPath)
    {
        var (runner, path, line, column) = Load(toolPath);

        var data = await runner.DataAsync("get_call_graph",
            new { filePath = path, line, column, direction = "callers", maxDepth = 2, maxNodes = 50 },
            s => s.GetCallGraphAsync(path, line, column, "callers", 2, 50));

        ((JArray)data["nodes"]!).Select(n => n["fullName"]!.Value<string>()).Should().Equal(
            "Demo.LambdaTarget.Hit()", "Demo.GenLambdaCaller.Make()");
    }
}
