using System.Xml;
using System.Xml.Linq;

namespace SharpLensMcp;

/// <summary>
/// Whether a project file is SDK-style: its root Project element names an Sdk, or it declares
/// an Sdk element or an Import of an Sdk. An SDK-style project always restores through
/// PackageReference, so a restored one has a project.assets.json; without it the project has
/// not been restored. A project file that cannot be read is not reported as SDK-style.
/// </summary>
internal static class ProjectFileStyle
{
    public static bool IsSdkStyle(string projectPath)
    {
        try
        {
            var root = XDocument.Load(projectPath).Root;
            if (root == null) return false;
            if (root.Attribute("Sdk") != null) return true;
            return root.Elements().Any(e =>
                e.Name.LocalName == "Sdk"
                || (e.Name.LocalName == "Import" && e.Attribute("Sdk") != null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
    }
}
