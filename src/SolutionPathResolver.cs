namespace SharpLensMcp;

/// <summary>
/// Resolves DOTNET_SOLUTION_PATH to the solution file the server auto-loads at startup: a
/// file is taken as given (load_solution validates it); a directory must hold exactly one
/// .sln or .slnx file. A directory with several is an error naming them, never an arbitrary
/// pick, mirroring how the dotnet CLI refuses an ambiguous project or solution directory.
/// </summary>
internal static class SolutionPathResolver
{
    public static SolutionPathResolution Resolve(string configuredPath)
    {
        if (File.Exists(configuredPath))
        {
            return new SolutionPathResolution(configuredPath, null);
        }

        if (!Directory.Exists(configuredPath))
        {
            return new SolutionPathResolution(null, $"DOTNET_SOLUTION_PATH does not exist: {configuredPath}");
        }

        var solutionFiles = Directory.GetFiles(configuredPath, "*.sln")
            .Concat(Directory.GetFiles(configuredPath, "*.slnx"))
            // GetFiles("*.sln") also matches longer extensions on Windows (8.3 name rules);
            // keep exactly the two solution extensions.
            .Where(path => Path.GetExtension(path).Equals(".sln", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        return solutionFiles.Count switch
        {
            1 => new SolutionPathResolution(solutionFiles[0], null),
            0 => new SolutionPathResolution(null,
                $"DOTNET_SOLUTION_PATH is a directory with no .sln or .slnx file: {configuredPath}"),
            _ => new SolutionPathResolution(null,
                $"DOTNET_SOLUTION_PATH is a directory with {solutionFiles.Count} solution files " +
                $"({string.Join(", ", solutionFiles.Select(Path.GetFileName))}); set it to one of them: {configuredPath}")
        };
    }
}
