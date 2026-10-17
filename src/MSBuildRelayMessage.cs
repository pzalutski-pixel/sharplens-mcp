using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.MSBuild;

namespace SharpLensMcp;

/// <summary>
/// Recognizes the workspace diagnostics that relay an MSBuild error or warning for one
/// project. MSBuildWorkspace's build host keeps MSBuild's Error/Warning severity, but the
/// in-process DiagnosticReporter re-reports every relayed item as a Failure with the message
/// "Msbuild failed when processing the file '{0}' with message: {1}" (or the variant without
/// a message) — so the kind carries no information, and only the message names the project.
/// The format strings are read from MSBuildWorkspace's own resources for the current UI
/// culture (the culture DiagnosticReporter formats with) as well as the invariant culture,
/// so a localized message is recognized too.
/// </summary>
internal static class MSBuildRelayMessage
{
    private const string ResourceBaseName = "Microsoft.CodeAnalysis.MSBuild.WorkspaceMSBuildResources";
    private const string WithMessageKey = "Msbuild_failed_when_processing_the_file_0_with_message_1";
    private const string WithoutMessageKey = "Msbuild_failed_when_processing_the_file_0";

    // The invariant (English) formats, used when the resources cannot be read.
    private const string InvariantWithMessage = "Msbuild failed when processing the file '{0}' with message: {1}";
    private const string InvariantWithoutMessage = "Msbuild failed when processing the file '{0}'";

    private static readonly ResourceManager Resources = new(ResourceBaseName, typeof(MSBuildWorkspace).Assembly);

    /// <summary>
    /// Parses a relayed message into the project file it names and the MSBuild message it
    /// carries ("" for the variant without one). False for any other workspace diagnostic.
    /// </summary>
    public static bool TryParse(string diagnosticMessage, out string projectPath, out string innerMessage)
    {
        foreach (var pattern in Patterns(CultureInfo.CurrentUICulture))
        {
            var match = pattern.Match(diagnosticMessage);
            if (match.Success)
            {
                projectPath = match.Groups["path"].Value;
                innerMessage = match.Groups["message"].Success ? match.Groups["message"].Value : "";
                return true;
            }
        }
        projectPath = "";
        innerMessage = "";
        return false;
    }

    /// <summary>The relayed message DiagnosticReporter produces for a project and an MSBuild message.</summary>
    public static string Format(string projectPath, string innerMessage) =>
        string.Format(CultureInfo.CurrentCulture, FormatString(WithMessageKey, InvariantWithMessage, CultureInfo.CurrentUICulture),
            projectPath, innerMessage);

    private static IEnumerable<Regex> Patterns(CultureInfo culture)
    {
        var formats = new[]
        {
            FormatString(WithMessageKey, InvariantWithMessage, culture),
            InvariantWithMessage,
            FormatString(WithoutMessageKey, InvariantWithoutMessage, culture),
            InvariantWithoutMessage,
        };
        return formats.Distinct(StringComparer.Ordinal).Select(ToRegex);
    }

    private static string FormatString(string key, string invariant, CultureInfo culture)
    {
        try
        {
            return Resources.GetString(key, culture) ?? invariant;
        }
        catch (MissingManifestResourceException)
        {
            return invariant;
        }
    }

    // "{0}" captures the project path (lazily, so it stops at the first literal that follows)
    // and "{1}" the rest of the message; everything else must match literally.
    private static Regex ToRegex(string format)
    {
        var escaped = Regex.Escape(format)
            .Replace(Regex.Escape("{0}"), "(?<path>.+?)", StringComparison.Ordinal)
            .Replace(Regex.Escape("{1}"), "(?<message>.*)", StringComparison.Ordinal);
        return new Regex("^" + escaped + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }
}
