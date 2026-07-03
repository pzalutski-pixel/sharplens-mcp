using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks the source-assembly restriction in find_attribute_usages and
// find_untested_code: GetAllNamedTypes walks the merged global namespace,
// which includes every referenced assembly. Without the
// ContainingAssembly == compilation.Assembly filter, find_attribute_usages
// returned BCL symbols for common attributes like [Obsolete], and
// find_untested_code counted a test method again for every project that
// references its declaring project.
public class SourceAssemblyScanFilterTests
{
    private const string ObsoleteUsageCode = @"
namespace Production
{
    [System.Obsolete]
    public class OldType
    {
        [System.Obsolete(""use something newer"")]
        public void OldMethod() { }

        public void FineMethod() { }
    }
}";

    [Fact]
    public async Task FindAttributeUsages_Obsolete_ReturnsOnlySourceSymbols()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(ObsoleteUsageCode);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindAttributeUsagesAsync("Obsolete");
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        // The BCL carries hundreds of [Obsolete] symbols — only the two source
        // usages may be found and counted.
        json["meta"]!["TotalCount"]!.Value<int>().Should().Be(2,
            "exactly OldType and OldType.OldMethod carry [Obsolete] in source");
        var usages = (json["data"]!["usages"] as JArray)!;
        usages.Select(u => u["symbolName"]!.Value<string>()).Should().BeEquivalentTo(
            new[] { "OldType", "OldType.OldMethod" });
    }

    [Fact]
    public async Task FindUntestedCode_TestMethodInReferencedProject_IsCountedOnce()
    {
        // LibTests declares a fake Xunit.FactAttribute and one test method;
        // AppTests references LibTests and declares its own test method using
        // the same attribute. Scanning AppTests' compilation also sees
        // LibTests' types through the project reference — its test method must
        // not be counted a second time there.
        var workspace = new AdhocWorkspace();
        var libProjectId = ProjectId.CreateNewId();
        var appProjectId = ProjectId.CreateNewId();
        var coreRefs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location)
        };

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                libProjectId, VersionStamp.Create(), "LibTests", "LibTests", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: coreRefs))
            .AddDocument(DocumentId.CreateNewId(libProjectId), "LibTests.cs",
                SourceText.From(@"
namespace Xunit
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class FactAttribute : System.Attribute { }
}

namespace Lib
{
    public class HelperTests
    {
        [Xunit.Fact]
        public void HelperTest() { }
    }
}"))
            .AddProject(ProjectInfo.Create(
                appProjectId, VersionStamp.Create(), "AppTests", "AppTests", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: coreRefs,
                projectReferences: new[] { new ProjectReference(libProjectId) }))
            .AddDocument(DocumentId.CreateNewId(appProjectId), "AppTests.cs",
                SourceText.From(@"
namespace App
{
    public class AppFacts
    {
        [Xunit.Fact]
        public void AppTest() { }
    }
}"));

        workspace.TryApplyChanges(solution);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindUntestedCodeAsync(
            projectName: "AppTests",
            includeProperties: true,
            includeInternal: false,
            maxResults: 50);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        json["data"]!["testMethodCount"]!.Value<int>().Should().Be(2,
            "HelperTest and AppTest are the only test methods; HelperTest must not " +
            "be counted again via AppTests' project reference");
    }
}
