namespace SharpLensMcp;

/// <summary>
/// One message NuGet restore recorded for a project in obj/project.assets.json (its "logs"
/// array): a code such as NU1101 or NU1903, a level ("Error", "Warning", ...), and the text.
/// The assets file keeps the severity MSBuildWorkspace's relayed diagnostics lose.
/// </summary>
public sealed record RestoreLogEntry(string ProjectPath, string Code, string Level, string Message);
