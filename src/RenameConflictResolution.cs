using System.Collections;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// A symbol rename through Roslyn's own rename engine WITH its conflict report. The
// public Renamer.RenameSymbolAsync returns only the renamed solution; the conflict
// engine behind it (the one an editor's inline rename uses) also reports, per location,
// whether the rename kept its meaning (NoConflict), Roslyn rewrote it to keep its
// meaning (ResolvedReferenceConflict / ResolvedNonReferenceConflict, e.g. qualifying a
// field with `this.`), or the new name breaks it (UnresolvedConflict,
// UnresolvableConflict, PossiblyResolvableConflict). That engine's entry point,
// Renamer.RenameSymbolAsync(Solution, ISymbol, string, SymbolRenameOptions,
// CancellationToken) returning ConflictResolution, is internal to
// Microsoft.CodeAnalysis.Workspaces and is reached by reflection; the pinned Roslyn
// version provides it, and a version without it fails loudly here instead of renaming
// without a conflict check.
internal static class RenameConflictResolution
{
    private static readonly Lazy<MethodInfo> RenameWithConflicts = new(() =>
        typeof(Renamer).GetMethod(
            "RenameSymbolAsync",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            new[] { typeof(Solution), typeof(ISymbol), typeof(string), typeof(SymbolRenameOptions), typeof(CancellationToken) },
            modifiers: null)
        ?? throw new InvalidOperationException(
            "This Roslyn version has no conflict-reporting rename entry point (Renamer.RenameSymbolAsync returning ConflictResolution)."));

    private static readonly HashSet<string> ConflictKinds = new(StringComparer.Ordinal)
    {
        "UnresolvedConflict", "UnresolvableConflict", "PossiblyResolvableConflict"
    };

    // The renamed solution and the locations the new name breaks, or the rename
    // engine's own refusal message (for example a name the language does not allow).
    public static async Task<(Solution? NewSolution, IReadOnlyList<RenameConflict> Conflicts, string? Refusal)> RenameAsync(
        Solution solution, ISymbol symbol, string newName, SymbolRenameOptions options, CancellationToken cancellationToken)
    {
        var task = (Task)RenameWithConflicts.Value.Invoke(null, new object[] { solution, symbol, newName, options, cancellationToken })!;
        await task.ConfigureAwait(false);
        var resolution = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var resolutionType = resolution.GetType();

        if (resolutionType.GetProperty("IsSuccessful")!.GetValue(resolution) is not true)
        {
            var message = resolutionType.GetField("ErrorMessage")!.GetValue(resolution) as string;
            return (null, Array.Empty<RenameConflict>(), message ?? "The rename engine refused the rename");
        }

        var newSolution = (Solution)resolutionType.GetField("NewSolution")!.GetValue(resolution)!;
        var conflicts = new List<RenameConflict>();
        foreach (var related in (IEnumerable)resolutionType.GetField("RelatedLocations")!.GetValue(resolution)!)
        {
            var relatedType = related.GetType();
            var kind = relatedType.GetProperty("Type")!.GetValue(related)!.ToString()!;
            if (!ConflictKinds.Contains(kind)) continue;
            conflicts.Add(new RenameConflict(
                (DocumentId)relatedType.GetProperty("DocumentId")!.GetValue(related)!,
                (TextSpan)relatedType.GetProperty("ConflictCheckSpan")!.GetValue(related)!,
                KindName(kind)));
        }
        return (newSolution, conflicts, null);
    }

    private static string KindName(string kind) => kind switch
    {
        "UnresolvedConflict" => "unresolved",
        "UnresolvableConflict" => "unresolvable",
        _ => "possiblyResolvable"
    };
}
