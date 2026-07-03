using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_extension_methods over an in-memory workspace whose probe declares BOTH
// styles for the same target — a classic `this` method and a C# 14 extension
// block member — plus a second target whose extensions must not bleed over.
// String fixture for the same reason as the C# 14 support tests: the NuGet
// Roslyn parses extension blocks regardless of the installed SDK's compiler.
public class ExtensionMethodsTests
{
    private const string ProbeSource = """
        namespace ExtProbe;

        public class Target { public int Value; }

        public class Unrelated { public int Other; }

        public static class TargetExtensions
        {
            public static int Doubled(this Target target) => target.Value * 2;

            extension(Target t)
            {
                public int Tripled => t.Value * 3;
            }
        }

        public static class UnrelatedExtensions
        {
            public static int Shifted(this Unrelated u) => u.Other << 1;
        }
        """;

    private static RoslynService CreateService()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(ProbeSource, "ExtProbe.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    [Fact]
    public async Task BothStyles_ReturnedForTheTarget_GroupedByClass()
    {
        var result = await CreateService().GetExtensionMethodsAsync("Target");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        data["typeName"]!.Value<string>().Should().Be("ExtProbe.Target");

        var groups = (data["groups"] as JArray)!;
        groups.Count.Should().Be(1, "only TargetExtensions extends Target");
        groups[0]["containingClass"]!.Value<string>().Should().Be("ExtProbe.TargetExtensions");

        var members = (groups[0]["members"] as JArray)!;
        var byName = members.ToDictionary(
            m => m["name"]!.Value<string>()!, m => m["style"]!.Value<string>());
        byName.Should().HaveCount(2);
        byName["Doubled"].Should().Be("classic");
        byName["Tripled"].Should().Be("extensionBlock",
            "the C# 14 extension block member must surface alongside the classic method");
        members.Should().OnlyContain(m => m["location"]!["filePath"] != null,
            "every entry carries its declaration location");
    }

    [Fact]
    public async Task NonMatchingReceiver_GetsNeitherOfTargetsExtensions()
    {
        var result = await CreateService().GetExtensionMethodsAsync("Unrelated");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var groups = (json["data"]!["groups"] as JArray)!;
        groups.Count.Should().Be(1);
        groups[0]["containingClass"]!.Value<string>().Should().Be("ExtProbe.UnrelatedExtensions");
        var names = (groups[0]["members"] as JArray)!.Select(m => m["name"]!.Value<string>()).ToList();
        names.Should().BeEquivalentTo(new[] { "Shifted" },
            "Target's classic and block extensions must not apply to Unrelated");
    }

    [Fact]
    public async Task GenericTargetInAnotherAssembly_ResolvesByMetadataName()
    {
        // Box<T> lives in a referenced assembly, so the same-assembly fallback does
        // not save it — only the correct metadata-name lookup (Box`1) finds it.
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location),
        };
        var workspace = new AdhocWorkspace();
        var libId = ProjectId.CreateNewId();
        var appId = ProjectId.CreateNewId();

        ProjectInfo Proj(ProjectId id, string name, ProjectReference[]? prs = null) => ProjectInfo.Create(
            id, VersionStamp.Create(), name, name, LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: refs, projectReferences: prs);

        var solution = workspace.CurrentSolution
            .AddProject(Proj(libId, "Lib"))
            .AddDocument(DocumentId.CreateNewId(libId), "Lib.cs",
                SourceText.From("namespace L { public class Box<T> { public T Value = default!; } }"))
            .AddProject(Proj(appId, "App", new[] { new ProjectReference(libId) }))
            .AddDocument(DocumentId.CreateNewId(appId), "App.cs",
                SourceText.From("namespace A { using L; public static class BoxExtensions { public static T Unwrap<T>(this Box<T> box) => box.Value; } }"));
        workspace.TryApplyChanges(solution).Should().BeTrue();

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.GetExtensionMethodsAsync("Box"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var names = (json["data"]!["groups"] as JArray)!
            .SelectMany(g => (g["members"] as JArray)!)
            .Select(m => m["name"]!.Value<string>())
            .ToList();
        names.Should().Contain("Unwrap",
            "the extension on Box<T> in the referenced assembly must be found via the metadata-name lookup");
    }

    private const string ConversionProbe = """
        namespace ConvProbe;

        public struct Celsius { public double Degrees; }

        public interface ISeq<T> { }
        public class IntSeq : ISeq<int> { }

        public static class ConvExtensions
        {
            extension(object o)
            {
                public string Describe() => o.ToString();
            }

            extension<T>(ISeq<T> seq)
            {
                public int Counted => 0;
            }
        }
        """;

    [Fact]
    public async Task ExtensionBlock_OnObject_AppliesToValueTypeTargetByBoxing()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(ConversionProbe, "Conv.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.GetExtensionMethodsAsync("Celsius"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var names = (json["data"]!["groups"] as JArray)!
            .SelectMany(g => (g["members"] as JArray)!).Select(m => m["name"]!.Value<string>()).ToList();
        names.Should().Contain("Describe",
            "an extension block on object applies to a value-type target via boxing");
    }

    [Fact]
    public async Task ExtensionBlock_OnGenericInterface_AppliesToImplementingTarget()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(ConversionProbe, "Conv.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.GetExtensionMethodsAsync("IntSeq"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var names = (json["data"]!["groups"] as JArray)!
            .SelectMany(g => (g["members"] as JArray)!).Select(m => m["name"]!.Value<string>()).ToList();
        names.Should().Contain("Counted",
            "an extension block on ISeq<T> applies to a target implementing ISeq<int>");
    }

    [Fact]
    public async Task MaxResultsOutOfRange_ReturnsInvalidParameter()
    {
        var service = CreateService();

        var low = JObject.FromObject(await service.GetExtensionMethodsAsync("Target", maxResults: 0));
        low["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);

        var high = JObject.FromObject(await service.GetExtensionMethodsAsync("Target", maxResults: 501));
        high["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task LowMaxResults_CapsResults_AndReportsTruncation()
    {
        // Target has two extensions (Doubled + Tripled); maxResults=1 caps to one.
        var json = JObject.FromObject(await CreateService().GetExtensionMethodsAsync("Target", maxResults: 1));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var returned = (json["data"]!["groups"] as JArray)!
            .Sum(g => (g["members"] as JArray)!.Count);
        returned.Should().Be(1, "maxResults=1 caps the returned members to one");
        json["meta"]!["TotalCount"]!.Value<int>().Should().Be(2,
            "both extensions are counted even though only one is returned");
        json["meta"]!["Truncated"]!.Value<bool>().Should().BeTrue(
            "returnedCount < totalCount must surface Truncated");
    }

    [Fact]
    public async Task UnknownType_ReturnsTypeNotFound()
    {
        var result = await CreateService().GetExtensionMethodsAsync("NoSuchTypeAnywhere");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.TypeNotFound);
    }
}
