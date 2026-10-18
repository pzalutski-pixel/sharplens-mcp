using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// A solution is evaluated with the SDK its own global.json selects, not the one the server
// process registered from its working directory: MSBuildWorkspace evaluates projects in a
// build host process that locates MSBuild from the project's directory. The pinned SDK is
// the lowest installed one that can build the net8.0 project (major version 8 or later), so
// on a machine with several SDKs it differs from the default the server (and this test
// process) registered; the project records the SDK version that evaluated it as a
// preprocessor symbol.
public class SdkSelectionTests : IDisposable
{
    private readonly TempSolutionDirectory _dir = new("sdkselect");

    public void Dispose() => _dir.Dispose();

    private (string solutionPath, string pinnedSdk) WritePinnedSolution()
    {
        var (exitCode, output) = TempSolutionDirectory.RunDotnet(_dir.Root, "--list-sdks");
        exitCode.Should().Be(0, output);
        var pinnedSdk = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split(' ')[0])
            .Where(version => Version.TryParse(version.Split('-')[0], out var parsed) && parsed.Major >= 8)
            .OrderBy(version => Version.Parse(version.Split('-')[0]))
            .First();

        _dir.Write("global.json", $"{{ \"sdk\": {{ \"version\": \"{pinnedSdk}\", \"rollForward\": \"disable\" }} }}\n");
        _dir.Write("Pin/Pin.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n" +
            "    <DefineConstants>$(DefineConstants);SDK_$(NETCoreSdkVersion.Replace('.', '_').Replace('-', '_'))</DefineConstants>\n" +
            "  </PropertyGroup>\n</Project>\n");
        _dir.Write("Pin/C.cs", "public class Probe { }\n");
        var sln = _dir.WriteSolution("Pin.sln", ("Pin", "Pin\\Pin.csproj"));
        TempSolutionDirectory.Restore(_dir.PathOf("Pin/Pin.csproj"));
        return (sln, pinnedSdk);
    }

    private static string SymbolFor(string sdkVersion) => "SDK_" + sdkVersion.Replace('.', '_').Replace('-', '_');

    [Fact]
    public async Task LoadedProject_IsEvaluatedWithTheSdkItsGlobalJsonPins()
    {
        var (sln, pinnedSdk) = WritePinnedSolution();
        var service = new RoslynService();

        JObject.FromObject(await service.LoadSolutionAsync(sln))["data"]!["partial"]!.Value<bool>().Should().BeFalse();

        var project = service.GetSolutionForTesting()!.Projects.Single();
        project.ParseOptions!.PreprocessorSymbolNames.Should().Contain(SymbolFor(pinnedSdk),
            $"global.json pins SDK {pinnedSdk}");
    }

    [Fact]
    public async Task LoadedProject_OverTheWire_SeesTheSymbolOfThePinnedSdk()
    {
        var (sln, pinnedSdk) = WritePinnedSolution();
        _dir.Write("Pin/Sdk.cs", $"#if {SymbolFor(pinnedSdk)}\npublic class BuiltWithPinnedSdk {{ }}\n#endif\n");
        var harness = new Mcp.McpHarnessOps(new McpServer());

        (await harness.CallToolAsync("load_solution", new { solutionPath = sln }))["success"]!.Value<bool>().Should().BeTrue();
        var search = await harness.CallToolAsync("search_symbols", new { query = "BuiltWithPinnedSdk" });

        ((JArray)search["data"]!["results"]!).Select(r => r["name"]!.Value<string>())
            .Should().Equal(new[] { "BuiltWithPinnedSdk" }, $"the class compiles only under SDK {pinnedSdk}");
    }
}
