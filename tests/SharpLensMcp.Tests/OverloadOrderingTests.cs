using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace SharpLensMcp.Tests;

// The canonical member ordering behind every overloadIndex: declared members by
// declaration position (path case-insensitive, then span), then — when requested —
// the inherited members C# lookup still sees, never overridden, hidden, private or
// constructor members. Constructors, explicit interface implementations, local
// functions and partial parts each belong to exactly one set at a fixed index.
public class OverloadOrderingTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "System.Runtime").Location)
    };

    private const string SplitA = @"
namespace Lib
{
    public partial class P { public void Foo(int value) { } }
}";

    private const string SplitB = @"
namespace Lib
{
    public partial class P { public void Foo(string text) { } }
}";

    private const string Members = @"
namespace Lib
{
    public class Base2 { public int Calc(int x) => x; }
    public class Derived2 : Base2 { public string Calc(string s) => s; }

    public class Root
    {
        public virtual void M(int x) { }
        public void M(string s) { }
        private void M(double d) { }
        public void M(long l) { }
    }

    public class Leaf : Root
    {
        public override void M(int x) { }
        public new void M(long l) { }
        public void M(bool b) { }
    }

    public class FieldHider : Root { public new int M; }

    public interface IShape { void Draw(); }
    public class Square : IShape
    {
        void IShape.Draw() { }
        public void Draw(int scale) { }
    }

    public class Widget
    {
        public Widget() { }
        public Widget(int c) { }
    }

    public struct Point { public Point(int x) { } }

    public class LocalHost
    {
        public int Outer()
        {
            int Helper(int v) => v + 1;
            return Helper(1);
        }
    }

    public partial class Gen { public partial void Run(int x); }
    public partial class Gen { public partial void Run(int x) { } public void Run(string s) { } }
}";

    private static CSharpCompilation Compile(params (string code, string path)[] files) =>
        CSharpCompilation.Create("OverloadOrderingFixture",
            files.Select(f => CSharpSyntaxTree.ParseText(f.code, path: f.path)),
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static CSharpCompilation MembersCompilation() => Compile((Members, "Members.cs"));

    private static INamedTypeSymbol Type(Compilation compilation, string metadataName) =>
        compilation.GetTypeByMetadataName(metadataName)!;

    private static List<string> Display(OverloadSet set) =>
        set.Members.Select(m => m.ToDisplayString()).ToList();

    [Fact]
    public void PartialTypeAcrossFiles_OrdersByPathCaseInsensitively()
    {
        // Ordinal order would put "B.cs" before "a.cs"; the canonical order is
        // case-insensitive, so a.cs's Foo(int) is index 0 on every platform.
        var compilation = Compile((SplitB, "B.cs"), (SplitA, "a.cs"));
        var set = OverloadOrdering.MembersNamed(Type(compilation, "Lib.P"), "Foo", includeInherited: false);
        Display(set).Should().Equal("Lib.P.Foo(int)", "Lib.P.Foo(string)");
        set.DeclaredCount.Should().Be(2);
    }

    [Fact]
    public void InheritedOverloads_FollowTheDeclaredOnes()
    {
        var derived = Type(MembersCompilation(), "Lib.Derived2");

        var declaredOnly = OverloadOrdering.MembersNamed(derived, "Calc", includeInherited: false);
        Display(declaredOnly).Should().Equal("Lib.Derived2.Calc(string)");

        var withInherited = OverloadOrdering.MembersNamed(derived, "Calc", includeInherited: true);
        Display(withInherited).Should().Equal("Lib.Derived2.Calc(string)", "Lib.Base2.Calc(int)");
        withInherited.DeclaredCount.Should().Be(1);
        withInherited.IsInherited(0).Should().BeFalse();
        withInherited.IsInherited(1).Should().BeTrue();
    }

    [Fact]
    public void InheritedOverloads_ExcludeOverriddenHiddenAndPrivateMembers()
    {
        var set = OverloadOrdering.MembersNamed(Type(MembersCompilation(), "Lib.Leaf"), "M", includeInherited: true);
        Display(set).Should().Equal(
            "Lib.Leaf.M(int)", "Lib.Leaf.M(long)", "Lib.Leaf.M(bool)",
            "Lib.Root.M(string)");
        set.DeclaredCount.Should().Be(3);
    }

    [Fact]
    public void DeclaredNonMethod_HidesEveryBaseMemberOfTheName()
    {
        var set = OverloadOrdering.MembersNamed(Type(MembersCompilation(), "Lib.FieldHider"), "M", includeInherited: true);
        Display(set).Should().Equal("Lib.FieldHider.M");
    }

    [Fact]
    public void Constructors_ByDotCtorOrTypeName_SynthesizedLast()
    {
        var compilation = MembersCompilation();
        var widget = Type(compilation, "Lib.Widget");
        Display(OverloadOrdering.MembersNamed(widget, ".ctor", includeInherited: true))
            .Should().Equal("Lib.Widget.Widget()", "Lib.Widget.Widget(int)");
        Display(OverloadOrdering.MembersNamed(widget, "Widget", includeInherited: true))
            .Should().Equal("Lib.Widget.Widget()", "Lib.Widget.Widget(int)");

        Display(OverloadOrdering.MembersNamed(Type(compilation, "Lib.Point"), ".ctor", includeInherited: false))
            .Should().Equal("Lib.Point.Point(int)", "Lib.Point.Point()");
    }

    [Theory]
    [InlineData("Draw")]
    [InlineData("IShape.Draw")]
    [InlineData("Lib.IShape.Draw")]
    public void ExplicitInterfaceImplementation_IsAddressableByEveryName(string memberName)
    {
        var set = OverloadOrdering.MembersNamed(Type(MembersCompilation(), "Lib.Square"), memberName, includeInherited: false);
        var expected = memberName == "Draw"
            ? new[] { "Lib.Square.Lib.IShape.Draw()", "Lib.Square.Draw(int)" }
            : new[] { "Lib.Square.Lib.IShape.Draw()" };
        Display(set).Should().Equal(expected);
    }

    [Fact]
    public void OverloadsOf_ConstructorExplicitImplementationAndLocalFunction()
    {
        var compilation = MembersCompilation();

        var ctor = Type(compilation, "Lib.Widget").InstanceConstructors.Single(c => c.Parameters.Length == 1);
        var ctorSet = OverloadOrdering.OverloadsOf(ctor, includeInherited: true);
        Display(ctorSet).Should().Equal("Lib.Widget.Widget()", "Lib.Widget.Widget(int)");
        ctorSet.IndexOf(ctor).Should().Be(1);

        var explicitDraw = Type(compilation, "Lib.Square").GetMembers("Lib.IShape.Draw").Single();
        var drawSet = OverloadOrdering.OverloadsOf(explicitDraw, includeInherited: false);
        Display(drawSet).Should().Equal("Lib.Square.Lib.IShape.Draw()", "Lib.Square.Draw(int)");
        drawSet.IndexOf(explicitDraw).Should().Be(0);

        var tree = compilation.SyntaxTrees.Single();
        var localSyntax = tree.GetRoot().DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single();
        var local = compilation.GetSemanticModel(tree).GetDeclaredSymbol(localSyntax)!;
        var localSet = OverloadOrdering.OverloadsOf(local, includeInherited: true);
        Display(localSet).Should().Equal("Helper(int)");
        localSet.IndexOf(local).Should().Be(0);
    }

    [Fact]
    public void OverloadsOf_PartialImplementationPart_IndexesAsItsDefinition()
    {
        var compilation = MembersCompilation();
        var definition = (IMethodSymbol)Type(compilation, "Lib.Gen").GetMembers("Run")
            .Single(m => m is IMethodSymbol { PartialImplementationPart: not null });
        var implementation = definition.PartialImplementationPart!;

        var set = OverloadOrdering.OverloadsOf(implementation, includeInherited: false);
        Display(set).Should().Equal("Lib.Gen.Run(int)", "Lib.Gen.Run(string)");
        set.IndexOf(implementation).Should().Be(0);
        set.IndexOf(definition).Should().Be(0);
        SymbolEqualityComparer.Default.Equals(set.Members[0], definition).Should().BeTrue(
            "the set holds the definition part; either part indexes to it");
    }

    [Fact]
    public void GeneratedParts_DoNotDecideTheOrder()
    {
        // The partial method's implementation lives in "0gen.g.cs", which sorts first
        // but is not user-authored; its user-authored definition in x.cs sorts after
        // a.cs's Foo(string), so with isUserSource the declared order is a.cs, x.cs.
        var compilation = Compile(
            ("namespace Lib { public partial class Q { public partial void Foo(int x); } }", "x.cs"),
            ("namespace Lib { public partial class Q { public void Foo(string s) { } } }", "a.cs"),
            ("namespace Lib { public partial class Q { public partial void Foo(int x) { } } }", "0gen.g.cs"));
        var q = Type(compilation, "Lib.Q");

        Display(OverloadOrdering.MembersNamed(q, "Foo", includeInherited: false,
                isUserSource: tree => !tree.FilePath.EndsWith(".g.cs", StringComparison.Ordinal)))
            .Should().Equal("Lib.Q.Foo(string)", "Lib.Q.Foo(int)");
        Display(OverloadOrdering.MembersNamed(q, "Foo", includeInherited: false))
            .Should().Equal("Lib.Q.Foo(int)", "Lib.Q.Foo(string)");
    }
}
