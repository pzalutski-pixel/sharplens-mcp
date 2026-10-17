using System.Text;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// Adds or removes one Compile item in a project file and keeps the rest of the file as it
// was: MSBuild's construction model edits the XML with its formatting preserved, the XML
// declaration (or its absence) is kept as the file had it, and the file is written back in
// its own encoding, BOM presence and line-break style.
internal static class ProjectFileEditor
{
    private const string CompileItemType = "Compile";

    public static void AddCompileItem(string projectFile, string include) =>
        Edit(projectFile, root =>
        {
            root.AddItem(CompileItemType, include);
            return true;
        });

    // Removes the Compile items whose Include is exactly the given path (compared as a
    // path) and any item group the removal leaves empty. The file is not written when no
    // item matches.
    public static void RemoveCompileItem(string projectFile, string include) =>
        Edit(projectFile, root =>
        {
            var matches = root.Items
                .Where(item => item.ItemType == CompileItemType && SamePath(item.Include, include))
                .ToList();
            foreach (var item in matches)
            {
                var group = item.Parent;
                group.RemoveChild(item);
                if (group.Count == 0) group.Parent.RemoveChild(group);
            }
            return matches.Count > 0;
        });

    // Applies the edit (which reports whether it changed anything) and writes the file back.
    private static void Edit(string projectFile, Func<ProjectRootElement, bool> edit)
    {
        string originalText;
        Encoding encoding;
        using (var reader = new StreamReader(projectFile, SourceFileIO.Utf8NoBom, detectEncodingFromByteOrderMarks: true))
        {
            originalText = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
        }

        string xml;
        using (var collection = new ProjectCollection())
        {
            var root = ProjectRootElement.Open(projectFile, collection, preserveFormatting: true);
            if (!edit(root)) return;
            using var writer = new StringWriter();
            root.Save(writer);
            xml = writer.ToString();
        }

        // Saving through a StringWriter always emits a declaration naming the writer's
        // encoding; put back the file's own declaration, or none if it had none.
        xml = Declaration(originalText) + xml[Declaration(xml).Length..];
        var original = SourceText.From(originalText);
        var lineBreak = LineEndings.UniformStyle(original) ?? LineEndings.DominantStyle(original);
        if (lineBreak != null) xml = LineEndings.Convert(xml, lineBreak);
        SourceFileIO.Write(projectFile, xml, encoding);
    }

    private static string Declaration(string xml)
    {
        if (!xml.StartsWith("<?xml", StringComparison.Ordinal)) return "";
        var end = xml.IndexOf("?>", StringComparison.Ordinal);
        return end < 0 ? "" : xml[..(end + 2)];
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            left.Replace('/', '\\'),
            right.Replace('/', '\\'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
