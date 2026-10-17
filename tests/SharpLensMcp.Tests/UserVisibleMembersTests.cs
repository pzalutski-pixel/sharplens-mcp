using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace SharpLensMcp.Tests;

// UserVisibleMembers and MemberClassifier: which members a listing shows, with which
// kind, and how inheritance extends the list — over source declarations and over a
// compiled (metadata) assembly, where IsImplicitlyDeclared is never set.
public class UserVisibleMembersTests
{
    private const string Source = """
        namespace Shapes
        {
            public interface IShape { double Area(); }
            public interface INamed { string Name { get; } }

            public class Square : IShape, INamed, System.IDisposable
            {
                private int _cache;
                public const int Sides = 4;
                public double Side { get; set; }
                public event System.Action Changed;
                public event System.Action Moved { add { } remove { } }
                public double this[int i] => Side * i;
                public Square() { }
                static Square() { }
                ~Square() { }
                public double Area() => Side * Side;
                string INamed.Name => "square";
                void System.IDisposable.Dispose() { }
                public static Square operator +(Square a, Square b) => a;
                public static implicit operator double(Square s) => s.Side;
                public class Corner { }
                private void Touch() { Changed?.Invoke(); _cache++; }
            }

            public record Point(int X, int Y) { public int Z { get; init; } }
            public class Holder(int value) { public int Value => value; }
            public enum Color { Red, Green }
            public struct Pair { public int A; }
            public delegate int Op(int x);
        }
        """;

    private static Compilation Compile(string source, string assemblyName = "Shapes", params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                    .First(a => a.GetName().Name == "System.Runtime").Location)
            }.Concat(references),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString()).Should().BeEmpty("the fixture source must compile");
        return compilation;
    }

    private static MetadataReference Emit(Compilation compilation)
    {
        using var stream = new MemoryStream();
        compilation.Emit(stream).Success.Should().BeTrue();
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static List<(string Name, MemberKind Kind, bool Inherited, bool Explicit)> Listing(
        INamedTypeSymbol type, bool includeInherited) =>
        UserVisibleMembers.Of(type, includeInherited)
            .Select(m => (m.Symbol.Name, m.Kind, m.IsInherited, m.IsExplicitInterfaceImplementation))
            .ToList();

    [Fact]
    public void SiblingBaseInterfaces_KeepTheirSameSignatureMembers_WhileADerivedInterfaceHidesItsBase()
    {
        var compilation = Compile("""
            namespace Shapes
            {
                public interface IAlpha { void Do(); }
                public interface IBeta { void Do(); }
                public interface ICombined : IAlpha, IBeta { }
                public interface INarrow : IAlpha { new void Do(); }
            }
            """);

        var combined = compilation.GetTypeByMetadataName("Shapes.ICombined")!;
        UserVisibleMembers.Of(combined, includeInherited: true)
            .Select(m => m.Symbol.ToDisplayString())
            .Should().Equal("Shapes.IAlpha.Do()", "Shapes.IBeta.Do()");

        var narrow = compilation.GetTypeByMetadataName("Shapes.INarrow")!;
        UserVisibleMembers.Of(narrow, includeInherited: true)
            .Select(m => m.Symbol.ToDisplayString())
            .Should().Equal("Shapes.INarrow.Do()");
    }

    [Fact]
    public void SourceClass_ListsDeclaredMembersOnce_WithTheirKinds()
    {
        var square = Compile(Source).GetTypeByMetadataName("Shapes.Square")!;

        Listing(square, includeInherited: false).Should().Equal(
            ("_cache", MemberKind.Field, false, false),
            ("Sides", MemberKind.Field, false, false),
            ("Side", MemberKind.Property, false, false),
            ("Changed", MemberKind.Event, false, false),
            ("Moved", MemberKind.Event, false, false),
            ("this[]", MemberKind.Indexer, false, false),
            (".ctor", MemberKind.Constructor, false, false),
            (".cctor", MemberKind.StaticConstructor, false, false),
            ("Finalize", MemberKind.Finalizer, false, false),
            ("Area", MemberKind.Method, false, false),
            ("Shapes.INamed.Name", MemberKind.Property, false, true),
            ("System.IDisposable.Dispose", MemberKind.Method, false, true),
            ("op_Addition", MemberKind.Operator, false, false),
            ("op_Implicit", MemberKind.Conversion, false, false),
            ("Corner", MemberKind.NestedType, false, false),
            ("Touch", MemberKind.Method, false, false));
    }

    [Fact]
    public void SourceRecord_ListsPositionalPropertiesAndPrimaryConstructor_NotSynthesizedMembers()
    {
        var point = Compile(Source).GetTypeByMetadataName("Shapes.Point")!;

        Listing(point, includeInherited: false).Should().Equal(
            (".ctor", MemberKind.Constructor, false, false),
            ("X", MemberKind.Property, false, false),
            ("Y", MemberKind.Property, false, false),
            ("Z", MemberKind.Property, false, false));
    }

    [Fact]
    public void PrimaryConstructorClass_EnumStructAndDelegate()
    {
        var compilation = Compile(Source);

        Listing(compilation.GetTypeByMetadataName("Shapes.Holder")!, false).Should().Equal(
            (".ctor", MemberKind.Constructor, false, false),
            ("Value", MemberKind.Property, false, false));
        Listing(compilation.GetTypeByMetadataName("Shapes.Color")!, true).Should().Equal(
            ("Red", MemberKind.Field, false, false),
            ("Green", MemberKind.Field, false, false));
        Listing(compilation.GetTypeByMetadataName("Shapes.Pair")!, true).Should().Equal(
            ("A", MemberKind.Field, false, false));
        Listing(compilation.GetTypeByMetadataName("Shapes.Op")!, true).Should().BeEmpty(
            "a delegate's constructor and Invoke/BeginInvoke/EndInvoke are runtime-provided");
    }

    [Fact]
    public void Delegate_ListsNothing_InSourceAndInMetadata()
    {
        var shapes = Compile(Source);
        var fromMetadata = Compile("namespace Use { public class U { } }", "Use", Emit(shapes))
            .GetTypeByMetadataName("Shapes.Op")!;

        fromMetadata.GetMembers().Select(m => m.Name).Should().BeEquivalentTo(
            new[] { ".ctor", "Invoke", "BeginInvoke", "EndInvoke" },
            "precondition: metadata exposes the runtime-provided members as plain methods");
        UserVisibleMembers.Of(fromMetadata, includeInherited: true).Should().BeEmpty();
        UserVisibleMembers.Of(shapes.GetTypeByMetadataName("Shapes.Op")!, includeInherited: true).Should().BeEmpty();
    }

    private const string Hierarchy = """
        namespace Family
        {
            public class Root
            {
                public Root() { }
                public int Shared;
                public virtual string Describe() => "root";
                public string Name { get; set; } = "";
                public void Use(int x) { }
                public void Use(string s) { }
                public T Echo<T>(T value) => value;
                protected int Guarded() => 1;
                internal int Inside() => 2;
                private int Hidden() => 3;
                public static int Count() => 0;
            }

            public class Middle : Root
            {
                public override string Describe() => "middle";
                public new int Name => 5;
            }

            public class Leaf : Middle
            {
                public new void Use(int x) { }
                public new U Echo<U>(U item) => item;
                public int Shared() => 1;
            }

            public interface IBase { void Ping(); int Size { get; } }
            public interface IDerived : IBase { void Pong(); new long Size { get; } }
        }
        """;

    [Fact]
    public void Inherited_ExcludesConstructorsAndPrivates_AndAppliesTheHidingRule()
    {
        // Not inherited: Root() and the implicit constructors, the private Root.Hidden.
        // Hidden: Root.Shared (a field, by Leaf's method of that name), Root.Describe (by
        // Middle's override), Root.Name (by Middle's new property), Root.Use(int) (by
        // Leaf's same-signature method) and Root.Echo<T> (by Leaf.Echo<U>, one signature).
        var leaf = Compile(Hierarchy).GetTypeByMetadataName("Family.Leaf")!;

        UserVisibleMembers.Of(leaf, includeInherited: true)
            .Select(m => $"{m.Symbol.ContainingType.Name}.{m.Symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}:{m.Kind}:{m.IsInherited}")
            .Should().Equal(
                "Leaf.void Leaf.Use(int x):Method:False",
                "Leaf.U Leaf.Echo<U>(U item):Method:False",
                "Leaf.int Leaf.Shared():Method:False",
                "Middle.string Middle.Describe():Method:True",
                "Middle.int Middle.Name:Property:True",
                "Root.void Root.Use(string s):Method:True",
                "Root.int Root.Guarded():Method:True",
                "Root.int Root.Inside():Method:True",
                "Root.int Root.Count():Method:True");
    }

    [Fact]
    public void Inherited_InternalMemberOfAnotherAssembly_IsNotInherited()
    {
        var family = Emit(Compile(Hierarchy, "Family"));
        var derived = Compile("namespace Other { public class Twig : Family.Root { } }", "Other", family)
            .GetTypeByMetadataName("Other.Twig")!;

        // Root comes from metadata here, so members follow metadata order; the subject is
        // the set: Inside (internal) and Hidden (private) are absent.
        UserVisibleMembers.Of(derived, includeInherited: true)
            .Where(m => m.IsInherited)
            .Select(m => m.Symbol.Name)
            .Should().BeEquivalentTo(new[] { "Shared", "Describe", "Name", "Use", "Use", "Echo", "Guarded", "Count" });
    }

    [Fact]
    public void Interface_InheritsBaseInterfaceMembers_HiddenByName()
    {
        var derived = Compile(Hierarchy).GetTypeByMetadataName("Family.IDerived")!;

        Listing(derived, includeInherited: true).Should().Equal(
            ("Pong", MemberKind.Method, false, false),
            ("Size", MemberKind.Property, false, false),
            ("Ping", MemberKind.Method, true, false));
    }

    [Fact]
    public void Metadata_RecordAndClass_ExcludeCompilerGeneratedAndPrivateMembers()
    {
        var shapes = Emit(Compile(Source));
        var consumer = Compile("namespace Use { public class U { } }", "Use", shapes);
        var point = consumer.GetTypeByMetadataName("Shapes.Point")!;
        var square = consumer.GetTypeByMetadataName("Shapes.Square")!;

        point.Locations.All(l => l.IsInMetadata).Should().BeTrue("precondition: the type is read from metadata");
        // A compiled record's synthesized members carry [CompilerGenerated] (or a '<' name);
        // its positional properties and primary constructor do not.
        Listing(point, includeInherited: false).Should().BeEquivalentTo(new[]
        {
            (".ctor", MemberKind.Constructor, false, false),
            ("X", MemberKind.Property, false, false),
            ("Y", MemberKind.Property, false, false),
            ("Z", MemberKind.Property, false, false)
        });
        // The source listing minus what metadata hides: the private _cache, Touch and
        // static constructor; the explicit implementations stay, flagged.
        Listing(square, includeInherited: false).Should().BeEquivalentTo(new[]
        {
            ("Sides", MemberKind.Field, false, false),
            ("Side", MemberKind.Property, false, false),
            ("Changed", MemberKind.Event, false, false),
            ("Moved", MemberKind.Event, false, false),
            ("this[]", MemberKind.Indexer, false, false),
            (".ctor", MemberKind.Constructor, false, false),
            ("Finalize", MemberKind.Finalizer, false, false),
            ("Area", MemberKind.Method, false, false),
            ("Shapes.INamed.Name", MemberKind.Property, false, true),
            ("System.IDisposable.Dispose", MemberKind.Method, false, true),
            ("op_Addition", MemberKind.Operator, false, false),
            ("op_Implicit", MemberKind.Conversion, false, false),
            ("Corner", MemberKind.NestedType, false, false)
        });
    }

    [Fact]
    public void Metadata_BclExplicitImplementations_AreListedAndFlagged()
    {
        var stringType = Compile("namespace E { }").GetSpecialType(SpecialType.System_String);
        var members = UserVisibleMembers.Of(stringType, includeInherited: false);

        members.Where(m => m.IsExplicitInterfaceImplementation).Select(m => m.Symbol.Name)
            .Should().Contain("System.IConvertible.ToInt32");
        members.Where(m => m.IsExplicitInterfaceImplementation)
            .Should().OnlyContain(m => m.Symbol.DeclaredAccessibility == Accessibility.Private);
        members.Where(m => !m.IsExplicitInterfaceImplementation)
            .Should().OnlyContain(m => m.Symbol.DeclaredAccessibility == Accessibility.Public
                || m.Symbol.DeclaredAccessibility == Accessibility.Protected
                || m.Symbol.DeclaredAccessibility == Accessibility.ProtectedOrInternal);
        members.Select(m => m.Symbol).OfType<IMethodSymbol>()
            .Should().NotContain(m => m.MethodKind == MethodKind.PropertyGet, "accessors are represented by their property");
    }

    [Fact]
    public void Classifier_AccessorsLocalFunctionsAndNonMembers_AreNotListable()
    {
        var square = Compile(Source).GetTypeByMetadataName("Shapes.Square")!;

        MemberClassifier.Classify(square.GetMembers("get_Side").Single()).Should().BeNull();
        MemberClassifier.Classify(square.GetMembers("add_Moved").Single()).Should().BeNull();
        MemberClassifier.Classify(square.ContainingNamespace).Should().BeNull();
        MemberClassifier.Classify(square).Should().BeNull("a top-level type is not a member of a type");
        MemberClassifier.KindNames.Should().Equal(
            "Method", "Constructor", "StaticConstructor", "Finalizer", "Property", "Indexer",
            "Field", "Event", "Operator", "Conversion", "NestedType");
    }

    [Fact]
    public void Classifier_ExplicitOperatorImplementation_IsAnOperator()
    {
        const string code = """
            namespace Ops
            {
                public interface IAdd<T> where T : IAdd<T> { static abstract T operator +(T a, T b); }
                public class Num : IAdd<Num> { static Num IAdd<Num>.operator +(Num a, Num b) => a; }
            }
            """;
        var num = Compile(code).GetTypeByMetadataName("Ops.Num")!;
        var implementation = num.GetMembers().OfType<IMethodSymbol>()
            .Single(m => m.MethodKind == MethodKind.ExplicitInterfaceImplementation);

        MemberClassifier.Classify(implementation).Should().Be(MemberKind.Operator);
        UserVisibleMembers.IsExplicitInterfaceImplementation(implementation).Should().BeTrue();
    }
}
