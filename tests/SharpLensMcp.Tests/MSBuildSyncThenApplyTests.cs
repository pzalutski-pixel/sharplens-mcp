using System.Diagnostics;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// The MSBuildWorkspace counterpart to SyncThenApplyTests. AdhocWorkspace is lenient about
// solution lineage and cannot reproduce a real TryApplyChanges rejection, so this loads a
// genuine temp solution through MSBuildWorkspace and verifies the analysis conclusion:
// after sync_documents reads a changed file from disk, a subsequent refactoring apply
// still succeeds, sync never writes back to the .csproj (the historical regression added
// <Compile Include> entries via TryApplyChanges), and the apply itself reaches disk.
public class MSBuildSyncThenApplyTests : IDisposable
{
    private readonly string _tempDir;

    public MSBuildSyncThenApplyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"msbuildsync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task SyncThenChangeSignature_OnRealSolution_AppliesAndLeavesProjectFileUntouched()
    {
        var csproj = Path.Combine(_tempDir, "Lib.csproj");
        File.WriteAllText(csproj,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net8.0</TargetFramework>\n" +
            "    <Nullable>enable</Nullable>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n");
        var csFile = Path.Combine(_tempDir, "C.cs");
        File.WriteAllText(csFile, "namespace Demo { public class C { public int Add(int amount) => amount + 1; } }");
        var sln = Path.Combine(_tempDir, "Lib.sln");
        File.WriteAllText(sln, SolutionText("Lib.csproj"));

        // MSBuildWorkspace design-time build needs restore artifacts (obj/project.assets.json)
        // for the framework references to resolve; otherwise every type is unresolved and the
        // post-apply diagnostic check would see spurious errors.
        Restore(csproj);
        var projectTextBefore = File.ReadAllText(csproj);

        var service = new RoslynService();
        JObject.FromObject(await service.LoadSolutionAsync(sln))["success"]!.Value<bool>()
            .Should().BeTrue("the temp solution must load via MSBuildWorkspace");

        // Change the file on disk, then sync it into the loaded solution.
        var diskText = "namespace Demo { public class C { public int Add(int amount) => amount + 2; } }";
        File.WriteAllText(csFile, diskText);
        JObject.FromObject(await service.SyncDocumentsAsync(new List<string> { csFile }))["success"]!.Value<bool>()
            .Should().BeTrue();

        // A subsequent apply must succeed despite the sync having advanced the solution.
        var apply = JObject.FromObject(await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null,
            new List<SignatureChange> { new() { Action = "add", Name = "extra", Type = "int", DefaultValue = "0" } },
            preview: false, typeName: "C", memberName: "Add"));
        apply["success"]!.Value<bool>().Should().BeTrue(
            "an apply after sync_documents on a real MSBuild solution must not be rejected: " + apply);

        File.ReadAllText(csproj).Should().Be(projectTextBefore,
            "sync reads FROM disk; it must never add <Compile Include> entries to the .csproj");
        File.ReadAllText(csFile).Should().Contain("int extra",
            "the change_signature apply wrote the new parameter to the source on disk");
    }

    private static void Restore(string csproj)
    {
        var psi = new ProcessStartInfo("dotnet", $"restore \"{csproj}\" --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(180_000).Should().BeTrue("dotnet restore must finish");
        process.ExitCode.Should().Be(0,
            $"the temp project must restore so MSBuildWorkspace can resolve references:\n{stdout}\n{stderr}");
    }

    private static string SolutionText(string projectFile) =>
        "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
        "# Visual Studio Version 17\n" +
        "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Lib\", \"" + projectFile +
        "\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
        "EndProject\n" +
        "Global\n" +
        "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n" +
        "\t\tDebug|Any CPU = Debug|Any CPU\n" +
        "\tEndGlobalSection\n" +
        "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\n" +
        "\t\t{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\n" +
        "\t\t{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU\n" +
        "\tEndGlobalSection\n" +
        "EndGlobal\n";
}
