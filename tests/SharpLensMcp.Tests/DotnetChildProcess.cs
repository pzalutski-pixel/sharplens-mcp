using System.Diagnostics;

namespace SharpLensMcp.Tests;

// A dotnet child process that resolves its own SDK. MSBuildLocator, registered in this test
// process, sets MSBUILD_EXE_PATH, MSBuildExtensionsPath and MSBuildSDKsPath to the SDK it
// chose; a child inheriting them runs its own SDK's MSBuild against that SDK's targets and
// tasks, which fails when a global.json (or the machine's SDK set) selects a different SDK.
// Every test that starts dotnet removes them, so the child behaves as a dotnet a user starts.
internal static class DotnetChildProcess
{
    private static readonly string[] LocatorVariables = { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath" };

    public static ProcessStartInfo StartInfo(string arguments) => Isolated(new ProcessStartInfo("dotnet", arguments));

    public static ProcessStartInfo Isolated(ProcessStartInfo startInfo)
    {
        foreach (var variable in LocatorVariables)
        {
            startInfo.Environment.Remove(variable);
        }
        return startInfo;
    }
}
