using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Standalone validate_code parses at the loaded solution's language version, so a
// snippet is held to the same syntax the real projects accept rather than the
// newest the compiler understands.
public class ValidateCodeLanguageVersionTests
{
    [Fact]
    public async Task Standalone_RejectsSyntaxNewerThanTheSolutionLanguageVersion()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
            projectId, VersionStamp.Create(), "OldLangProject", "OldLangProject",
            LanguageNames.CSharp,
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp7_3),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            });
        workspace.AddProject(projectInfo);

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        // Target-typed `new()` is a C# 9 feature, invalid at the project's C# 7.3.
        var result = await service.ValidateCodeAsync(
            "public class C { public C Make() => new(); }", standalone: true);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var data = json["data"]!;
        var errors = (data["errors"] as JArray)!;
        data["compiles"]!.Value<bool>().Should().BeFalse(
            $"target-typed new() is newer than C# 7.3 and must be rejected; errors: {errors}");
        errors.Should().Contain(
            e => e["message"]!.Value<string>()!.Contains("7.3"),
            $"the rejection must cite the language version; errors: {errors}");
    }
}
