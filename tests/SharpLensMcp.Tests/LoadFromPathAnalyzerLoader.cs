using Microsoft.CodeAnalysis;

namespace SharpLensMcp.Tests;

// The minimal analyzer-assembly loader a test AnalyzerFileReference needs: loads the
// assembly at its path into the default load context.
internal sealed class LoadFromPathAnalyzerLoader : IAnalyzerAssemblyLoader
{
    public void AddDependencyLocation(string fullPath) { }

    public System.Reflection.Assembly LoadFromPath(string fullPath) => System.Reflection.Assembly.LoadFrom(fullPath);
}
