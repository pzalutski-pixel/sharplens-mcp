using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// add_missing_imports over an IN-MEMORY workspace: a fixture with unresolved
// names would be a compile error on disk and break the solution-wide
// zero-errors canary. The probe source covers all three buckets at once - a
// metadata type missing its using, a name declared in two source namespaces
// (deterministic ambiguity), and a name that exists nowhere.
public class MissingImportsTests
{
    private const string ProbeSource = """
        using System;

        namespace First.Widgets
        {
            public class DupWidget { }
        }

        namespace Second.Widgets
        {
            public class DupWidget { }
        }

        namespace Probe
        {
            public class ImportHost
            {
                public string Build()
                {
                    var builder = new StringBuilder();
                    builder.Append(DateTime.UtcNow);
                    return builder.ToString();
                }

                public object? Make() => new DupWidget();

                public object? Mystery() => new TotallyUnknownTypeXyz();
            }
        }
        """;

    private static (RoslynService service, string filePath) CreateService()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(ProbeSource, "ImportHost.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, document.FilePath!);
    }

    [Fact]
    public async Task ClassifiesAllThreeBuckets_AndComputesTheUsing()
    {
        var (service, filePath) = CreateService();

        var result = await service.AddMissingImportsAsync(filePath);

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        data["appliesEditsAutomatically"]!.Value<bool>().Should().BeFalse();

        var added = (data["added"] as JArray)!;
        added.Count.Should().Be(1);
        added[0]["name"]!.Value<string>().Should().Be("StringBuilder");
        added[0]["namespace"]!.Value<string>().Should().Be("System.Text");
        added[0]["using"]!.Value<string>().Should().Be("using System.Text;");

        var ambiguous = (data["ambiguous"] as JArray)!;
        ambiguous.Count.Should().Be(1);
        ambiguous[0]["name"]!.Value<string>().Should().Be("DupWidget");
        ambiguous[0]["candidates"]!.Select(c => c.Value<string>()).Should().BeEquivalentTo(
            "First.Widgets", "Second.Widgets");

        var unresolvable = (data["unresolvable"] as JArray)!;
        unresolvable.Select(u => u.Value<string>()).Should().BeEquivalentTo("TotallyUnknownTypeXyz");

        var content = data["updatedContent"]!.Value<string>()!;
        content.Should().Contain("using System;");
        content.Should().Contain("using System.Text;");
        content.IndexOf("using System;").Should().BeLessThan(content.IndexOf("using System.Text;"),
            "the new using inserts at its sorted position after the existing System using");
        content.Should().NotContain("using First.Widgets;",
            "an ambiguous name must not produce any using");
    }

    [Fact]
    public async Task UpdatedContent_ResolvesTheDiagnosticItTargets()
    {
        var (service, filePath) = CreateService();
        var data = JObject.FromObject(await service.AddMissingImportsAsync(filePath))["data"]!;
        var content = data["updatedContent"]!.Value<string>()!;

        // Re-load the UPDATED content in a fresh workspace: the StringBuilder
        // diagnostics must be gone; only the deliberate ambiguous/unknown
        // names may remain unresolved.
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(content, "ImportHostFixed.cs");
        var fixedService = new RoslynService();
        fixedService.LoadFromWorkspaceForTesting(workspace);
        var second = JObject.FromObject(await fixedService.AddMissingImportsAsync(document.FilePath!))["data"]!;

        (second["added"] as JArray)!.Should().BeEmpty(
            "every unambiguous import was already added in the first pass");
        (second["ambiguous"] as JArray)!.Count.Should().Be(1);
        (second["unresolvable"] as JArray)!.Count.Should().Be(1);
    }

    [Fact]
    public async Task ExtensionMethodCall_SuggestsItsDeclaringNamespace()
    {
        const string code = """
            namespace Helpers
            {
                public static class StringExtensions
                {
                    public static string Shout(this string s) => s + "!";
                }
            }

            namespace App
            {
                public class User
                {
                    public string Use(string x) => x.Shout();
                }
            }
            """;
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "ExtHost.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.AddMissingImportsAsync(document.FilePath!))["data"]!;
        var added = (data["added"] as JArray)!;
        added.Should().Contain(
            a => a["name"]!.Value<string>() == "Shout" && a["namespace"]!.Value<string>() == "Helpers",
            "a missing extension-method call (CS1061) must suggest the namespace that declares the extension");
    }

    [Fact]
    public async Task CleanFile_ReportsNothing()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            "namespace Clean; public class Fine { public int X; }", "Fine.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.AddMissingImportsAsync(document.FilePath!);

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue();
        var data = json["data"]!;
        (data["added"] as JArray)!.Should().BeEmpty();
        (data["ambiguous"] as JArray)!.Should().BeEmpty();
        (data["unresolvable"] as JArray)!.Should().BeEmpty();
        data["updatedContent"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task DoesNotOfferInternalTypesFromOtherAssemblies_ButOffersPublicOnes()
    {
        // Two assemblies: Lib declares an internal and a public type; App (which
        // references Lib) uses both by simple name. A using can reach the public
        // type across assemblies but NOT the internal one.
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location),
        };
        var workspace = new AdhocWorkspace();
        var libId = ProjectId.CreateNewId();
        var appId = ProjectId.CreateNewId();
        var libDocId = DocumentId.CreateNewId(libId);
        var appDocId = DocumentId.CreateNewId(appId);
        var tempDir = Path.Combine(Path.GetTempPath(), $"adhoc-{Guid.NewGuid():N}");

        var libInfo = ProjectInfo.Create(libId, VersionStamp.Create(), "Lib", "Lib", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest), metadataReferences: refs);
        var appInfo = ProjectInfo.Create(appId, VersionStamp.Create(), "App", "App", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest), metadataReferences: refs,
            projectReferences: new[] { new ProjectReference(libId) });

        DocumentInfo Doc(DocumentId id, string name, string code) => DocumentInfo.Create(id, name,
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(code), VersionStamp.Create())),
            filePath: Path.Combine(tempDir, name));

        var solution = workspace.CurrentSolution
            .AddProject(libInfo)
            .AddDocument(Doc(libDocId, "Lib.cs",
                "namespace LibNs { internal class SecretType { } } namespace PubNs { public class PublicType { } }"))
            .AddProject(appInfo)
            .AddDocument(Doc(appDocId, "App.cs",
                "namespace AppNs { public class User { public object? A() => new PublicType(); public object? B() => new SecretType(); } }"));
        workspace.TryApplyChanges(solution).Should().BeTrue();

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.AddMissingImportsAsync(
            workspace.CurrentSolution.GetDocument(appDocId)!.FilePath!))["data"]!;

        var addedNames = (data["added"] as JArray)!.Select(a => a["name"]!.Value<string>()).ToList();
        addedNames.Should().Contain("PublicType",
            "a public cross-assembly type is reachable by adding its using");

        var unresolvable = (data["unresolvable"] as JArray)!.Select(u => u.Value<string>()).ToList();
        unresolvable.Should().Contain("SecretType",
            "an internal type in another assembly cannot be made reachable by a using");
        addedNames.Should().NotContain("SecretType",
            "the inaccessible internal type must not be offered as an import");
    }

    [Fact]
    public async Task UnknownFile_ReturnsFileNotInSolution()
    {
        var (service, _) = CreateService();

        var result = await service.AddMissingImportsAsync(@"C:\nowhere\Missing.cs");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.FileNotInSolution);
    }
}
