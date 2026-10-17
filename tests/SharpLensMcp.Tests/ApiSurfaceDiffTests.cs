using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace SharpLensMcp.Tests;

// Unit coverage for the diff_api_surface internals: SnapshotApiSurface (which
// symbols count as public API and how they render) and DiffApiSurfaces (the
// removed / changed / added classification), on hermetic in-memory
// compilations. The git-driven end-to-end path is covered by
// DiffApiSurfaceToolTests.
public class ApiSurfaceDiffTests
{
    private static CSharpCompilation CreateCompilation(string source, bool nullable = false)
    {
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        if (nullable) options = options.WithNullableContextOptions(NullableContextOptions.Enable);
        return CSharpCompilation.Create(
            "ApiSurfaceTest",
            new[] { CSharpSyntaxTree.ParseText(source) },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                    .First(a => a.GetName().Name == "System.Runtime").Location)
            },
            options);
    }

    [Fact]
    public void Snapshot_IncludesPublicAndProtected_ExcludesInternalAndPrivate()
    {
        var surface = RoslynService.SnapshotApiSurface(CreateCompilation("""
            namespace Lib;
            public class Widget
            {
                public void PublicMethod() { }
                protected void ProtectedMethod() { }
                internal void InternalMethod() { }
                private void PrivateMethod() { }
            }
            internal class Hidden { public void NotApi() { } }
            """));

        var signatures = surface.Select(e => e.Signature).ToList();
        signatures.Should().Contain(s => s.Contains("PublicMethod"));
        signatures.Should().Contain(s => s.Contains("ProtectedMethod"));
        signatures.Should().NotContain(s => s.Contains("InternalMethod"));
        signatures.Should().NotContain(s => s.Contains("PrivateMethod"));
        signatures.Should().NotContain(s => s.Contains("Hidden"),
            "members of an internal type are not API even when declared public");
        // Exactly: the Widget type entry, its implicit-ctor-free two methods.
        surface.Should().HaveCount(3, "Widget + PublicMethod + ProtectedMethod");
    }

    [Fact]
    public void Snapshot_ExcludesProtectedMembersOfSealedTypes()
    {
        var surface = RoslynService.SnapshotApiSurface(CreateCompilation("""
            namespace Lib;
            public sealed class Closed
            {
                public void PublicMethod() { }
                protected void ProtectedMethod() { }
            }
            """));

        var signatures = surface.Select(e => e.Signature).ToList();
        signatures.Should().Contain(s => s.Contains("PublicMethod"));
        signatures.Should().NotContain(s => s.Contains("ProtectedMethod"),
            "a sealed type cannot be subclassed, so its protected members are not consumable API surface");
        surface.Should().HaveCount(2, "only the Closed type entry and its public method");
    }

    [Fact]
    public void Diff_RemovedMethod_ReportsSingleBreakingRemoved()
    {
        var baseline = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib; public class C { public void A() { } public void B() { } }"));
        var current = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib; public class C { public void A() { } }"));

        var (breaking, added) = RoslynService.DiffApiSurfaces(baseline, current);

        added.Should().BeEmpty();
        breaking.Should().HaveCount(1);
        breaking[0].changeKind.Should().Be("removed");
        breaking[0].signature.Should().Contain("B()");
        breaking[0].signature.Should().StartWith("public");
    }

    [Fact]
    public void Diff_AddedMethod_ReportsSingleAdded()
    {
        var baseline = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib; public class C { public void A() { } }"));
        var current = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib; public class C { public void A() { } public void B() { } }"));

        var (breaking, added) = RoslynService.DiffApiSurfaces(baseline, current);

        breaking.Should().BeEmpty();
        added.Should().HaveCount(1);
        added[0].Signature.Should().Contain("B()");
    }

    [Fact]
    public void Diff_ReturnTypeChange_ReportsChangedWithBothSignatures()
    {
        var baseline = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib; public class C { public int M() => 1; }"));
        var current = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib; public class C { public string M() => \"\"; }"));

        var (breaking, added) = RoslynService.DiffApiSurfaces(baseline, current);

        added.Should().BeEmpty("the method identity (name + parameters) is unchanged");
        breaking.Should().HaveCount(1);
        breaking[0].changeKind.Should().Be("changed");
        breaking[0].baselineSignature.Should().Contain("int");
        breaking[0].signature.Should().Contain("string");
    }

    [Fact]
    public void Diff_ReturnNullabilityChange_ReportsChanged()
    {
        // string M(...) → string? M(...): the nullable annotation is part of the public
        // contract, so it must register as a breaking change, not collapse to identical.
        var baseline = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib; public class C { public string M(string s) => s; }", nullable: true));
        var current = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib; public class C { public string? M(string s) => s; }", nullable: true));

        var (breaking, added) = RoslynService.DiffApiSurfaces(baseline, current);

        added.Should().BeEmpty("the method identity (name + parameters) is unchanged");
        breaking.Should().HaveCount(1);
        breaking[0].changeKind.Should().Be("changed");
        breaking[0].baselineSignature.Should().NotContain("string?",
            "the baseline return type was non-nullable");
        breaking[0].signature.Should().Contain("string?",
            "the new return type is nullable, which the signature must record");
    }

    [Fact]
    public void Diff_AccessibilityReduced_ReportsChanged()
    {
        var baseline = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib; public class C { public void M() { } }"));
        var current = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib; public class C { protected void M() { } }"));

        var (breaking, _) = RoslynService.DiffApiSurfaces(baseline, current);

        breaking.Should().HaveCount(1);
        breaking[0].changeKind.Should().Be("changed");
        breaking[0].baselineSignature.Should().StartWith("public");
        breaking[0].signature.Should().StartWith("protected");
    }

    [Fact]
    public void Diff_RemovedType_CollapsesMembersIntoOneEntry()
    {
        var baseline = RoslynService.SnapshotApiSurface(CreateCompilation("""
            namespace Lib;
            public class Gone
            {
                public void M1() { }
                public void M2() { }
                public int P { get; set; }
            }
            public class Stays { public void Keep() { } }
            """));
        var current = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib; public class Stays { public void Keep() { } }"));

        var (breaking, added) = RoslynService.DiffApiSurfaces(baseline, current);

        added.Should().BeEmpty();
        breaking.Should().HaveCount(1, "the removed type's members must collapse into the type entry");
        breaking[0].changeKind.Should().Be("removed");
        breaking[0].signature.Should().Contain("class");
        breaking[0].signature.Should().Contain("Gone");
    }

    [Fact]
    public void Diff_AddedType_CollapsesMembersIntoOneEntry()
    {
        var baseline = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib; public class Stays { public void Keep() { } }"));
        var current = RoslynService.SnapshotApiSurface(CreateCompilation("""
            namespace Lib;
            public class Stays { public void Keep() { } }
            public class Extra
            {
                public void M1() { }
                public void M2() { }
            }
            """));

        var (breaking, added) = RoslynService.DiffApiSurfaces(baseline, current);

        breaking.Should().BeEmpty();
        added.Should().HaveCount(1, "the added type's members must collapse into the type entry");
        added[0].Signature.Should().Contain("class");
        added[0].Signature.Should().Contain("Extra");
    }

    [Fact]
    public void Snapshot_RendersGenericTypeAndMethodArity()
    {
        var surface = RoslynService.SnapshotApiSurface(CreateCompilation("""
            namespace Lib;
            public class Box<T>
            {
                public T Value { get; set; } = default!;
                public TResult Map<TResult>(System.Func<T, TResult> f) => f(Value);
            }
            """));
        var signatures = surface.Select(e => e.Signature).ToList();

        signatures.Should().Contain(s => s.Contains("Box<T>"),
            "the generic type renders with its type parameter");
        signatures.Should().Contain(s => s.Contains("Map<TResult>"),
            "the generic method renders with its own type parameter");
    }

    [Fact]
    public void Diff_GenericTypeArityChange_IsTreatedAsRemovedPlusAdded()
    {
        // Box (non-generic) → Box<T>: distinct DocumentationCommentIds (Box vs Box`1),
        // so it is a removal of the old and an addition of the new, not a "changed".
        var baseline = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib; public class Box { public int V; }"));
        var current = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib; public class Box<T> { public T V; }"));

        var (breaking, added) = RoslynService.DiffApiSurfaces(baseline, current);

        breaking.Should().Contain(b => b.changeKind == "removed" && b.signature.Contains("Box"));
        added.Should().Contain(a => a.Signature.Contains("Box<T>"));
    }

    [Fact]
    public void Snapshot_RendersRecordAndNestedType()
    {
        var surface = RoslynService.SnapshotApiSurface(CreateCompilation("""
            namespace Lib;
            public record Point(int X, int Y);
            public class Outer
            {
                public class Inner { public void Ping() { } }
            }
            """));
        var signatures = surface.Select(e => e.Signature).ToList();

        signatures.Should().Contain(s => s.Contains("Point"), "a public record is API surface");
        signatures.Should().Contain(s => s.Contains("Outer.Inner") || s.Contains("Inner"),
            "a public nested type is API surface");
        signatures.Should().Contain(s => s.Contains("Ping"),
            "the nested type's public member is API surface");
    }

    [Fact]
    public void Diff_DuplicateDeclarations_AreComparedOnce_NotACrash()
    {
        // Two declarations of one type in one compilation (a duplicated source file) merge
        // into one type with the same member declared twice — two entries with one
        // documentation-comment id; the diff compares the key once.
        var baseline = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib { public class C { public void A() { } public void B() { } } } namespace Lib { public class C { public void A() { } } }"));
        var current = RoslynService.SnapshotApiSurface(CreateCompilation(
            "namespace Lib { public class C { public void A() { } } }"));

        var (breaking, added) = RoslynService.DiffApiSurfaces(baseline, current);

        baseline.Count(e => e.Key == "M:Lib.C.A").Should().Be(2, "the fixture must carry the duplicate key it guards against");
        breaking.Select(b => $"{b.changeKind}: {b.signature}").Should().Equal("removed: public void Lib.C.B()");
        added.Should().BeEmpty();
    }
}
