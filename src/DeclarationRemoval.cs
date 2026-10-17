using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLensMcp;

// Removes declarations (types, members, statements, attribute lists, usings) from a
// syntax tree without losing what their leading trivia carries that is not theirs:
//   - preprocessor directives and inactive (#if-ed out) text before the declaration —
//     a #region, #if, #else, #endif or #nullable line belongs to the file's structure,
//     and the code an inactive branch holds belongs to another configuration;
//   - the file header (the comments that start the file, up to the blank line after
//     them) when the declaration is the first thing in the file.
// That prefix moves onto the token that follows the declaration. The declaration's own
// doc comment, blank lines and indentation go with it, and a directive inside the
// declaration stays only when removing it would unbalance the file
// (SyntaxRemoveOptions.KeepUnbalancedDirectives), so no #region or #if is left
// without its end.
internal static class DeclarationRemoval
{
    public static TRoot Remove<TRoot>(TRoot root, IEnumerable<SyntaxNode> nodes) where TRoot : SyntaxNode
    {
        var targets = nodes.Distinct().ToList();
        if (targets.Count == 0) return root;

        var current = root.TrackNodes(targets);
        foreach (var target in targets.OrderBy(n => n.SpanStart))
        {
            var node = current.GetCurrentNode(target);
            if (node == null) continue;

            var leading = node.GetLeadingTrivia();
            var keep = KeptPrefixLength(current, node, leading);
            if (keep > 0)
            {
                var next = node.GetLastToken(includeZeroWidth: true).GetNextToken(includeZeroWidth: true);
                current = current.ReplaceToken(next, next.WithLeadingTrivia(leading.Take(keep).Concat(next.LeadingTrivia)));
                node = current.GetCurrentNode(target)!;
                current = current.ReplaceNode(node, node.WithLeadingTrivia(node.GetLeadingTrivia().Skip(keep)));
                node = current.GetCurrentNode(target)!;
            }
            current = current.RemoveNode(node, SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
        }
        return current;
    }

    // How many leading trivia of the node stay in the file: through the last directive
    // or inactive text, and through the file header when the node starts the file.
    private static int KeptPrefixLength(SyntaxNode root, SyntaxNode node, SyntaxTriviaList leading)
    {
        var keep = 0;
        for (var i = 0; i < leading.Count; i++)
        {
            if (leading[i].IsDirective || leading[i].IsKind(SyntaxKind.DisabledTextTrivia)) keep = i + 1;
        }

        if (node.GetFirstToken(includeZeroWidth: true) == root.GetFirstToken(includeZeroWidth: true)
            && leading.Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia)))
        {
            // The header ends at the first blank line: an end-of-line that follows
            // another end-of-line (or starts the file).
            for (var i = 0; i < leading.Count; i++)
            {
                if (!leading[i].IsKind(SyntaxKind.EndOfLineTrivia)) continue;
                var blank = i == 0 || leading[i - 1].IsKind(SyntaxKind.EndOfLineTrivia)
                    || leading[i - 1].IsDirective;
                if (blank && i > 0)
                {
                    keep = Math.Max(keep, i);
                    break;
                }
            }
        }
        return keep;
    }
}
