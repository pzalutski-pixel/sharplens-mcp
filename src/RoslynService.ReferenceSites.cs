using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace SharpLensMcp;

// The reference set of a symbol, shared by find_references and analyze_change_impact so the
// two always report the same sites. SymbolFinder reports a site once per definition it
// cascades to (a using alias's target as well as the alias, the declaration of each
// per-target-framework compilation of a multi-targeted project) and once per project that
// compiles the file; the generated-code scan reports references inside generator output.
// All of them merge by location — one site per file, line and column, with the target
// frameworks it occurred in — and come back in the canonical location order.
public partial class RoslynService
{
    private static readonly StringComparer SitePathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private async Task<List<MergedResult<ReferenceSite>>> FindReferenceSitesAsync(
        ISymbol symbol, CancellationToken cancellationToken)
    {
        var occurrences = new List<(ReferenceSite Site, string? TargetFramework)>();
        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, _solution!, cancellationToken))
        {
            foreach (var location in reference.Locations)
            {
                if (!location.Location.IsInSource || location.Location.SourceTree is not { } tree) continue;
                var span = location.Location.SourceSpan;
                var lineSpan = tree.GetLineSpan(span, cancellationToken);
                var text = await tree.GetTextAsync(cancellationToken);
                var root = await tree.GetRootAsync(cancellationToken);
                occurrences.Add((
                    new ReferenceSite(
                        FormatPath(tree.FilePath),
                        lineSpan.StartLinePosition.Line,
                        lineSpan.StartLinePosition.Character,
                        text.Lines[lineSpan.StartLinePosition.Line].ToString().Trim(),
                        ClassifyReferenceKind(root.FindNode(span)),
                        InGeneratedCode: !IsSolutionSourceTree(tree)),
                    LogicalProject.TargetFrameworkOf(location.Document.Project)));
            }
        }
        foreach (var hit in await ScanGeneratedReferencesAsync(symbol, cancellationToken))
        {
            occurrences.Add((
                new ReferenceSite(FormatPath(hit.HintPath), hit.Line, hit.Column, hit.LineText, hit.Kind, InGeneratedCode: true),
                null));
        }

        var merged = TargetFrameworkDeduplication.By(
            occurrences,
            o => $"{o.Site.FilePath}\n{o.Site.Line}\n{o.Site.Column}",
            SitePathComparer,
            o => o.TargetFramework);
        return CanonicalOrder.ByLocation(
            merged.Select(m => new MergedResult<ReferenceSite>(m.Item.Site, m.TargetFrameworks, m.Occurrences)),
            m => new ResultLocation(m.Item.FilePath, m.Item.Line, m.Item.Column));
    }

    // A reference site as find_references reports it: { filePath, line, column, lineText,
    // kind }, inGeneratedCode when it is inside generator output, and targetFrameworks when
    // it occurred in a multi-targeted project.
    private static Dictionary<string, object?> FormatReferenceSite(MergedResult<ReferenceSite> merged)
    {
        var site = merged.Item;
        var entry = new Dictionary<string, object?>
        {
            ["filePath"] = site.FilePath,
            ["line"] = site.Line,
            ["column"] = site.Column,
            ["lineText"] = site.LineText,
            ["kind"] = site.Kind
        };
        if (site.InGeneratedCode) entry["inGeneratedCode"] = true;
        return WithTargetFrameworks(entry, merged.TargetFrameworks);
    }
}
