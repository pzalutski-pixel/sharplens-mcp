using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// find_unused_code never reports code something outside the source calls: the
// compilation's entry point (Main) and its type, test methods by each framework's
// discovery contract (xUnit [Fact], MSTest [TestMethod]) and the classes holding them,
// and members a framework sets (MSTest's TestContext property). A reference from inside
// the symbol's own declaration (a self-recursive call) does not make it used. The
// projectName is resolved like every project-scoped tool: case-insensitively, and an
// unknown name is INVALID_PARAMETER rather than an empty "clean" result.
public class UnusedCodeEntryPointTests
{
    internal const string Code = """
        namespace Xunit
        {
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public class FactAttribute : System.Attribute { }
        }

        namespace Microsoft.VisualStudio.TestTools.UnitTesting
        {
            public class TestClassAttribute : System.Attribute { }
            public class TestMethodAttribute : System.Attribute { }
            public class TestContext { }
        }

        namespace App
        {
            public static class Program
            {
                public static void Main() { }
            }

            public class XTests
            {
                [Xunit.Fact] public void Adds() { }
            }

            [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
            public class MsTests
            {
                public Microsoft.VisualStudio.TestTools.UnitTesting.TestContext TestContext { get; set; } = null!;
                [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod] public void Works() { }
            }

            public class Recursion
            {
                public int Countdown(int n) => n <= 0 ? 0 : Countdown(n - 1);
                public int Used() => 1;
                public int Caller() => Used();
            }
        }
        """;

    // An executable project named App, so the compilation has an entry point.
    internal static AdhocWorkspace CreateWorkspace()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location)
        };
        var project = ProjectInfo.Create(projectId, VersionStamp.Create(), "App", "App", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                .WithNullableContextOptions(NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: references);
        var document = DocumentInfo.Create(DocumentId.CreateNewId(projectId), "App.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(Code), VersionStamp.Create())),
            filePath: Path.Combine(Path.GetTempPath(), $"adhoc-{Guid.NewGuid():N}", "App.cs"));
        workspace.TryApplyChanges(workspace.CurrentSolution.AddProject(project).AddDocument(document));
        return workspace;
    }

    private static List<string> Unused(JToken data) =>
        ((JArray)data["unusedSymbols"]!).Select(s => $"{s["fullyQualifiedName"]}|{s["kind"]}").ToList();

    [Fact]
    public async Task EntryPointsTestsAndFrameworkSetMembers_AreUsed_SelfCallsAreNot()
    {
        var service = ToolResult.Load(CreateWorkspace());

        var data = ToolResult.Data(await service.FindUnusedCodeAsync("App", includePrivate: true, includeInternal: true));

        Unused(data).Should().Equal(
            "App.Recursion|Class",
            "App.Recursion.Countdown(int)|Method",
            "App.Recursion.Caller()|Method");
    }

    [Theory]
    [InlineData("app")]
    [InlineData("APP")]
    public async Task ProjectName_IsResolvedCaseInsensitively(string projectName)
    {
        var service = ToolResult.Load(CreateWorkspace());

        var data = ToolResult.Data(await service.FindUnusedCodeAsync(projectName, includePrivate: true, includeInternal: true, symbolKindFilter: "Method"));

        Unused(data).Should().Equal("App.Recursion.Countdown(int)|Method", "App.Recursion.Caller()|Method");
    }

    [Fact]
    public async Task UnknownProjectName_IsInvalidParameter_AndMaxResultsIsValidated()
    {
        var service = ToolResult.Load(CreateWorkspace());

        var unknown = ToolResult.Error(await service.FindUnusedCodeAsync("Nope", includePrivate: true, includeInternal: false), ErrorCodes.InvalidParameter);
        var limit = ToolResult.Error(await service.FindUnusedCodeAsync(null, includePrivate: true, includeInternal: false, maxResults: 0), ErrorCodes.InvalidParameter);

        unknown["Message"]!.Value<string>().Should().Be("Project not found: Nope");
        limit["Context"]!["parameter"]!.Value<string>().Should().Be("maxResults");
    }

    [Fact]
    public async Task EntryPoints_ViaMcp()
    {
        var host = new InMemoryToolHost(CreateWorkspace());

        var data = await host.DataAsync("find_unused_code", new { projectName = "App", includeInternal = true });
        var error = await host.ErrorAsync("find_unused_code", new { projectName = "Nope" }, ErrorCodes.InvalidParameter);

        Unused(data).Should().Equal(
            "App.Recursion|Class",
            "App.Recursion.Countdown(int)|Method",
            "App.Recursion.Caller()|Method");
        error["message"]!.Value<string>().Should().Be("Project not found: Nope");
    }
}
