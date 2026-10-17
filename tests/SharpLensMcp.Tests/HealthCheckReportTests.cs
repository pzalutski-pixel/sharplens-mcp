using System.Globalization;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// health_check's report over in-memory workspaces: exact, deterministic values.
public class HealthCheckReportTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "healthcheck-fixture");

    // A workspace modeling what MSBuildWorkspace loads: one workspace project per
    // (project file, target framework), each with one document at a shared path.
    private static AdhocWorkspace CreateWorkspace(params (string projectFile, string[] frameworks, string source)[] projects)
    {
        var workspace = new AdhocWorkspace();
        var references = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        foreach (var (projectFile, frameworks, source) in projects)
        {
            var projectPath = Path.Combine(Root, projectFile);
            var name = Path.GetFileNameWithoutExtension(projectFile);
            foreach (var framework in frameworks)
            {
                var projectId = ProjectId.CreateNewId();
                workspace.AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(),
                    frameworks.Length > 1 ? $"{name}({framework})" : name, name, LanguageNames.CSharp,
                    filePath: projectPath,
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    metadataReferences: references));
                workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(projectId), "C.cs",
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())),
                    filePath: Path.Combine(Path.GetDirectoryName(projectPath)!, "C.cs")));
            }
        }
        return workspace;
    }

    private const string OneError = "public class C { int x = \"not an int\"; public int X => x; }";

    [Fact]
    public async Task IncludeDiagnostics_CountsAnErrorOnce_AcrossTheTargetFrameworksOfAProject()
    {
        // A multi-targeted project compiles once per framework; the same error in
        // the same file is one error, not one per framework.
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(CreateWorkspace(("Multi/Multi.csproj", new[] { "net8.0", "net9.0" }, OneError)));

        var solution = JObject.FromObject(await service.GetHealthCheckAsync(includeDiagnostics: true))["data"]!["solution"]!;

        solution["projects"]!.Value<int>().Should().Be(2, "two workspace projects, one per framework");
        solution["errors"]!.Value<int>().Should().Be(1);
        solution["warnings"]!.Value<int>().Should().Be(0);
        solution["diagnosticsSampledProjects"]!.Value<int>().Should().Be(1, "one project file");
        solution["diagnosticsTotalProjects"]!.Value<int>().Should().Be(1);
    }

    [Fact]
    public async Task IncludeDiagnostics_SamplesFiveProjectFiles_AndSaysSo()
    {
        // The sample is five project FILES (each with all its frameworks), and the report
        // states how many were sampled out of how many, so a partial sweep is not read as
        // the whole solution.
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(CreateWorkspace(
            ("P1/P1.csproj", new[] { "net8.0", "net9.0" }, OneError),
            ("P2/P2.csproj", new[] { "net8.0" }, OneError),
            ("P3/P3.csproj", new[] { "net8.0" }, OneError),
            ("P4/P4.csproj", new[] { "net8.0" }, OneError),
            ("P5/P5.csproj", new[] { "net8.0" }, OneError),
            ("P6/P6.csproj", new[] { "net8.0" }, OneError)));

        var solution = JObject.FromObject(await service.GetHealthCheckAsync(includeDiagnostics: true))["data"]!["solution"]!;

        solution["errors"]!.Value<int>().Should().Be(5, "one distinct error in each of the first five project files");
        solution["diagnosticsSampledProjects"]!.Value<int>().Should().Be(5);
        solution["diagnosticsTotalProjects"]!.Value<int>().Should().Be(6);
    }

    [Fact]
    public async Task IncludeDiagnostics_OverTheWire_CountsTheMultiTargetErrorOnce()
    {
        var server = new McpServer();
        server.LoadFromWorkspaceForTesting(CreateWorkspace(("Multi/Multi.csproj", new[] { "net8.0", "net9.0" }, OneError)));
        var harness = new Mcp.McpHarnessOps(server);

        var health = await harness.CallToolAsync("health_check", new { includeDiagnostics = true });

        var solution = health["data"]!["solution"]!;
        solution["errors"]!.Value<int>().Should().Be(1);
        solution["diagnosticsSampledProjects"]!.Value<int>().Should().Be(1);
        solution["diagnosticsTotalProjects"]!.Value<int>().Should().Be(1);
        ((JArray)solution["loadWarnings"]!).Should().BeEmpty("an in-memory workspace has no load step");
    }

    [Fact]
    public async Task DefaultHealthCheck_LeavesTheDiagnosticsSampleFieldsNull()
    {
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(CreateWorkspace(("Multi/Multi.csproj", new[] { "net8.0" }, OneError)));

        var solution = JObject.FromObject(await service.GetHealthCheckAsync())["data"]!["solution"]!;

        solution["diagnosticsSampledProjects"]!.Type.Should().Be(JTokenType.Null);
        solution["diagnosticsTotalProjects"]!.Type.Should().Be(JTokenType.Null);
        solution["errors"]!.Type.Should().Be(JTokenType.Null);
    }
    [Fact]
    public async Task LoadedAt_IsInvariantIso8601Utc_UnderACultureWithAnotherCalendar()
    {
        // th-TH formats dates in the Thai Buddhist calendar (year + 543); a culture-
        // sensitive format would report a year far in the future.
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode("namespace Demo { public class C { } }", "C.cs");
        var service = new RoslynService();
        var before = DateTime.UtcNow;
        service.LoadFromWorkspaceForTesting(workspace);

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var health = JObject.FromObject(await service.GetHealthCheckAsync());

            var loadedAt = health["data"]!["solution"]!["loadedAt"]!.Value<string>()!;
            loadedAt.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$");
            var parsed = DateTime.ParseExact(loadedAt, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            parsed.Year.Should().Be(before.Year, "the timestamp is Gregorian regardless of the current culture");
            (parsed - before).Should().BeLessThan(TimeSpan.FromMinutes(5));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
