namespace SharpLensMcp;

// The target framework of one Roslyn project loaded from a multi-targeted project file.
// MSBuildWorkspace loads one Project per framework and names each "<file name>(<tfm>)";
// a single-targeted project carries the bare file name and has no framework override.
internal static class ProjectTargetFramework
{
    public static string? Of(Microsoft.CodeAnalysis.Project project)
    {
        if (project.FilePath == null) return null;
        var fileName = Path.GetFileNameWithoutExtension(project.FilePath);
        var name = project.Name;
        if (name.Length > fileName.Length + 2
            && name.StartsWith(fileName + "(", StringComparison.Ordinal)
            && name.EndsWith(')'))
        {
            return name.Substring(fileName.Length + 1, name.Length - fileName.Length - 2);
        }
        return null;
    }
}
