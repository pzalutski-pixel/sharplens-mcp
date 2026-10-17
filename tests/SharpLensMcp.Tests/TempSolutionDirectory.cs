using System.Diagnostics;
using FluentAssertions;

namespace SharpLensMcp.Tests;

// A throwaway on-disk solution for tests that must go through MSBuildWorkspace (load
// fidelity, the stdio process): files are written under a unique temp directory that is
// deleted on dispose. Restore runs only when a test asks for it and needs no network for
// projects without PackageReferences (the targeting packs ship with the SDK).
public sealed class TempSolutionDirectory : IDisposable
{
    public const string MinimalCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";

    public string Root { get; }

    public TempSolutionDirectory(string prefix)
    {
        Root = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    // The absolute, platform-normalized path of a file under the root ('/' in relativePath
    // becomes this platform's separator), matching the paths the server reports.
    public string PathOf(string relativePath) => Path.GetFullPath(Path.Combine(Root, relativePath));

    public string Write(string relativePath, string content)
    {
        var path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // A .sln listing the given projects (name -> path relative to the .sln), each with a
    // distinct project GUID, under the project type GUID given (C# by default).
    public string WriteSolution(string relativePath, params (string name, string path)[] projects) =>
        WriteSolution(relativePath, projects.Select(p => (p.name, p.path, "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC")).ToArray());

    public string WriteSolution(string relativePath, (string name, string path, string typeGuid)[] projects)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Microsoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\n");
        for (var i = 0; i < projects.Length; i++)
        {
            var guid = $"{i + 1:D8}-1111-1111-1111-111111111111";
            sb.Append($"Project(\"{{{projects[i].typeGuid}}}\") = \"{projects[i].name}\", \"{projects[i].path}\", \"{{{guid}}}\"\nEndProject\n");
        }
        sb.Append("Global\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n\t\tDebug|Any CPU = Debug|Any CPU\n\tEndGlobalSection\nEndGlobal\n");
        return Write(relativePath, sb.ToString());
    }

    public static void Restore(string projectOrSolutionPath)
    {
        var (exitCode, output) = RunDotnet(Path.GetDirectoryName(projectOrSolutionPath)!,
            "restore", projectOrSolutionPath, "--nologo");
        exitCode.Should().Be(0, $"dotnet restore must succeed for {projectOrSolutionPath}; output: {output}");
    }

    public static (int exitCode, string output) RunDotnet(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(300_000).Should().BeTrue($"dotnet {string.Join(' ', arguments)} must finish");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A BuildHost process can still hold a file handle for a moment after a load;
            // the directory is under the temp root and is left for the OS to reclaim.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: a lingering handle, not a test failure.
        }
    }
}
