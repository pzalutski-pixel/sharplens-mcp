using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Name-based addressing never lands on a source generator's part: a partial member
// whose body is generated ([GeneratedRegex], [LoggerMessage]) addresses its
// user-authored declaration, and a partial type with a generated declaration that
// sorts first addresses its declaration in the solution document.
public class GeneratedPartAddressingTests
{
    private static (RoslynService service, string userFilePath) CreateService()
    {
        var (workspace, userFilePath) = GeneratedPartAddressingFixture.Create();
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, userFilePath);
    }

    [Fact]
    public async Task Fixture_GeneratedDeclarationSortsBeforeTheUserFile()
    {
        var (service, userFilePath) = CreateService();
        var project = service.GetSolutionForTesting()!.Projects.Single();
        var compilation = (await project.GetCompilationAsync())!;
        var host = compilation.GetTypeByMetadataName("Demo.GenPartialHost")!;
        var paths = host.Locations.Select(l => l.SourceTree!.FilePath).ToList();
        paths.Should().HaveCount(2);
        var generated = paths.Single(p => p != userFilePath);
        generated.Should().StartWith(Path.Combine(Path.GetDirectoryName(userFilePath)!, "obj", "gen"));
        string.CompareOrdinal(generated, userFilePath).Should().BeNegative(
            "the premise: an unfiltered earliest-path pick lands on the generated file");
    }

    [Fact]
    public async Task ResolveAddressing_GeneratedBodyMember_AddressesTheUserDeclaration()
    {
        var (service, userFilePath) = CreateService();
        var (address, error) = await service.ResolveAddressingAsync(
            null, null, null, "GenPartialHost", "Compute", null);
        error.Should().BeNull();
        address!.FilePath.Should().Be(userFilePath);
        (address.Line, address.Column).Should().Be((4, 27));
        address.Symbol!.ToDisplayString().Should().Be("Demo.GenPartialHost.Compute(int)");
    }

    [Fact]
    public async Task SuperMethodByName_GeneratedBodyMember_Succeeds()
    {
        var (service, _) = CreateService();
        var json = JObject.FromObject(await service.GetSuperMethodAsync(typeName: "GenPartialHost", memberName: "Compute"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["symbol"]!.Value<string>().Should().Be("Demo.GenPartialHost.Compute(int)");
    }

    [Fact]
    public async Task RenameByName_TypeWithGeneratedDeclaration_RenamesInTheUserFile()
    {
        var (service, _) = CreateService();
        var json = JObject.FromObject(await service.RenameSymbolAsync(
            null, null, null, newName: "GenPartialHost2", preview: true, typeName: "GenPartialHost"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["symbolName"]!.Value<string>().Should().Be("GenPartialHost");
        ((JArray)json["data"]!["changes"]!).Select(c => Path.GetFileName(c["filePath"]!.Value<string>()))
            .Should().Equal("rx.cs");
    }
}
