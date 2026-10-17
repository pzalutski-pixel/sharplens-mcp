using System.Diagnostics;
using System.Text;
using FluentAssertions;

namespace SharpLensMcp.Tests;

// An on-disk temp solution for tests that exercise the real MSBuild load and the
// disk-writing apply / sync paths. The test writes the project and source files
// (exact bytes when encoding or line endings matter), then calls Restore so the
// design-time build resolves framework references. Paths are given relative to the
// solution root with forward slashes. Dispose deletes the directory.
public sealed class DiskSolution : IDisposable
{
    public string Root { get; }
    public string SolutionPath { get; }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // projectPaths: each project's .csproj path relative to the root ("Lib/Lib.csproj").
    public DiskSolution(params string[] projectPaths)
    {
        Root = Path.Combine(Path.GetTempPath(), $"disksln-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        SolutionPath = Path.Combine(Root, "S.sln");
        File.WriteAllText(SolutionPath, SolutionText(projectPaths), Utf8NoBom);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    public string PathOf(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    // Writes text as UTF-8 WITHOUT a byte-order mark, line endings exactly as given.
    public void WriteText(string relativePath, string text) =>
        WriteBytes(relativePath, Utf8NoBom.GetBytes(text));

    public void WriteBytes(string relativePath, byte[] bytes)
    {
        var path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public byte[] ReadBytes(string relativePath) => File.ReadAllBytes(PathOf(relativePath));

    // Decodes as UTF-8 (a BOM, if present, stays visible as U+FEFF).
    public string ReadText(string relativePath) => Utf8NoBom.GetString(ReadBytes(relativePath));

    // An SDK-style project file: one <TargetFramework> or several <TargetFrameworks>,
    // optional project references (paths relative to the project) and extra XML inside
    // <Project>. LF line endings, no BOM.
    public static string SdkProject(string targetFrameworks, string[]? projectReferences = null, string extraXml = "")
    {
        var tfmElement = targetFrameworks.Contains(';')
            ? $"    <TargetFrameworks>{targetFrameworks}</TargetFrameworks>\n"
            : $"    <TargetFramework>{targetFrameworks}</TargetFramework>\n";
        var sb = new StringBuilder();
        sb.Append("<Project Sdk=\"Microsoft.NET.Sdk\">\n\n");
        sb.Append("  <PropertyGroup>\n").Append(tfmElement).Append("  </PropertyGroup>\n");
        if (projectReferences is { Length: > 0 })
        {
            sb.Append("\n  <ItemGroup>\n");
            foreach (var reference in projectReferences)
                sb.Append($"    <ProjectReference Include=\"{reference}\" />\n");
            sb.Append("  </ItemGroup>\n");
        }
        sb.Append(extraXml);
        sb.Append("\n</Project>\n");
        return sb.ToString();
    }

    // Restores in a single in-process MSBuild node (-m:1 -nodeReuse:false): a multi-project
    // restore otherwise starts worker nodes that outlive the restore while holding its
    // redirected output open. The output is collected asynchronously and the wait is on
    // the process itself, never on the output reaching its end.
    public void Restore()
    {
        var psi = new ProcessStartInfo("dotnet", $"restore \"{SolutionPath}\" --nologo -m:1 -nodeReuse:false")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit(180_000).Should().BeTrue("dotnet restore must finish");
        string log;
        lock (output) log = output.ToString();
        process.ExitCode.Should().Be(0, $"the temp solution must restore:\n{log}");
    }

    private static string SolutionText(string[] projectPaths)
    {
        var sb = new StringBuilder();
        sb.Append("Microsoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\n");
        var guids = new List<string>();
        for (var i = 0; i < projectPaths.Length; i++)
        {
            var guid = $"{i + 1:D8}-1111-1111-1111-111111111111";
            guids.Add(guid);
            var name = Path.GetFileNameWithoutExtension(projectPaths[i]);
            var path = projectPaths[i].Replace('/', '\\');
            sb.Append($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", \"{path}\", \"{{{guid}}}\"\nEndProject\n");
        }
        sb.Append("Global\n");
        sb.Append("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n\t\tDebug|Any CPU = Debug|Any CPU\n\tEndGlobalSection\n");
        sb.Append("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\n");
        foreach (var guid in guids)
        {
            sb.Append($"\t\t{{{guid}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\n");
            sb.Append($"\t\t{{{guid}}}.Debug|Any CPU.Build.0 = Debug|Any CPU\n");
        }
        sb.Append("\tEndGlobalSection\nEndGlobal\n");
        return sb.ToString();
    }
}
