using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace SharpLensMcp.Tests;

internal static class TestInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }
}
