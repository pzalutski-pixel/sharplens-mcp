using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Xunit;

namespace SharpLensMcp.Tests;

// Direct unit test for GetNestedActionsOrEmpty. The production loop in
// apply_code_action_by_title descends through CodeActionWithNestedActions
// wrappers until it finds a leaf whose title still matches the caller's
// request. Roslyn's CodeActionWithNestedActions is internal, so the impl
// discovers nested children via reflection on a "NestedCodeActions" property.
// This test exercises that reflection path with a stand-in CodeAction
// subclass that exposes its own NestedCodeActions property.
public class CodeActionNestedDescentTests
{
    private sealed class LeafAction : CodeAction
    {
        public LeafAction(string title) { _title = title; }

        private readonly string _title;

        public override string Title => _title;
    }

    private sealed class MenuAction : CodeAction
    {
        public MenuAction(string title, ImmutableArray<CodeAction> nested)
        {
            _title = title;
            NestedCodeActions = nested;
        }

        private readonly string _title;

        public override string Title => _title;

        public ImmutableArray<CodeAction> NestedCodeActions { get; }
    }

    [Fact]
    public void Returns_NestedChildren_WhenActionExposesProperty()
    {
        var leaf = new LeafAction("Wrap and align argument list");
        var menu = new MenuAction("Wrap...", ImmutableArray.Create<CodeAction>(leaf));
        var nested = RoslynService.GetNestedActionsOrEmpty(menu);
        nested.Should().HaveCount(1, "the impl must surface the inner array via the reflected property");
        nested[0].Title.Should().Be("Wrap and align argument list");
    }

    [Fact]
    public void Returns_Empty_ForLeafAction_WithNoNestedProperty()
    {
        var leaf = new LeafAction("Inline temporary variable");
        var nested = RoslynService.GetNestedActionsOrEmpty(leaf);
        nested.Should().BeEmpty("the impl must terminate the descent loop on actions lacking a NestedCodeActions property");
    }

    [Fact]
    public void ResolveLeaf_PicksTheFirstChildMatchingTheTitle()
    {
        var wanted = new LeafAction("Introduce local for 'x'");
        var other = new LeafAction("Introduce constant for 'x'");
        var menu = new MenuAction("Introduce...", ImmutableArray.Create<CodeAction>(other, wanted));

        var (leaf, candidates) = RoslynService.ResolveLeafAction(menu, "Introduce local");
        leaf.Should().BeSameAs(wanted, "the first child whose title contains the request is chosen");
        candidates.Should().BeNull();
    }

    [Fact]
    public void ResolveLeaf_ReturnsAmbiguous_WhenNoChildMatchesTheTitle()
    {
        var a = new LeafAction("Align argument list");
        var b = new LeafAction("Indent all arguments");
        var menu = new MenuAction("Wrap...", ImmutableArray.Create<CodeAction>(a, b));

        // No child contains the request title — the old code applied nested[0]
        // silently; now the caller is asked to choose among the children.
        var (leaf, candidates) = RoslynService.ResolveLeafAction(menu, "Wrap expression");
        leaf.Should().BeNull("no leaf matches, so no action may be applied silently");
        candidates.Should().BeEquivalentTo(new[] { "Align argument list", "Indent all arguments" });
    }

    [Fact]
    public void ResolveLeaf_UnwrapsSingleChildMenu_RegardlessOfTitle()
    {
        var leaf = new LeafAction("Wrap and align argument list");
        var menu = new MenuAction("Wrap...", ImmutableArray.Create<CodeAction>(leaf));

        var (resolved, candidates) = RoslynService.ResolveLeafAction(menu, "totally different");
        resolved.Should().BeSameAs(leaf, "a one-child menu is just a wrapper and unwraps unconditionally");
        candidates.Should().BeNull();
    }

    [Fact]
    public void Descends_AcrossMultipleLevelsOfNesting()
    {
        // The production loop is `while (true) { nested = ...; if empty break; pick leaf }`.
        // Multi-level nesting must terminate at the deepest leaf.
        var deepLeaf = new LeafAction("Wrap and align argument list");
        var middle = new MenuAction("Wrap argument list", ImmutableArray.Create<CodeAction>(deepLeaf));
        var top = new MenuAction("Wrap...", ImmutableArray.Create<CodeAction>(middle));

        var current = (CodeAction)top;
        var depth = 0;
        while (true)
        {
            var nested = RoslynService.GetNestedActionsOrEmpty(current);
            if (nested.Length == 0) break;
            current = nested[0];
            depth++;
            if (depth > 10) break;
        }

        depth.Should().Be(2, "the loop must descend exactly twice: top → middle → deepLeaf");
        current.Title.Should().Be("Wrap and align argument list", "the loop must land on the leaf, not a wrapper");
    }
}
