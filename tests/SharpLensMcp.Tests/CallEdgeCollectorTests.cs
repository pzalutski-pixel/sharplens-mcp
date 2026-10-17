using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace SharpLensMcp.Tests;

// CallEdgeCollector on its own, against one in-memory compilation: for each method, the
// exact multiset of edges (target, kind, dispatch, initializer origin) it reports.
public class CallEdgeCollectorTests
{
    private const string Source = @"
namespace N
{
    public interface IShape { double Area(); }
    public class Shape : IShape
    {
        public virtual double Area() => 1;
        public int Size { get; set; }
        public event System.EventHandler Changed { add { } remove { } }
    }
    public class Square : Shape
    {
        public override double Area() => base.Area() + 1;
    }
    public struct Money
    {
        public static Money operator +(Money a, Money b) => a;
        public static implicit operator int(Money m) => 0;
    }
    public class Res : System.IDisposable { public void Dispose() { } }
    public class Pt { public void Deconstruct(out int x, out int y) { x = 0; y = 0; } }
    public static class Ext { public static int Twice(this int x) => x * 2; }

    public partial class Part
    {
        public partial int Compute();
    }
    public partial class Part
    {
        public partial int Compute() => 1;
    }

    public class Base { public Base(int x) { } public Base() { } }
    public class Built : Base
    {
        private int _seed = Seed();
        public Built() : base(1) { }
        public static int Seed() => 0;
    }
    public class Implicit : Base
    {
        private int _seed = Built.Seed();
    }
    public class Prim(int v) : Base(v)
    {
        private int _w = Built.Seed() + v;
    }
    public class Throws
    {
        public int Run(string s)
        {
            if (s.Length == 0) throw new System.ArgumentNullException(nameof(s));
            var n = s.Length > 1 ? s.Length : throw new System.FormatException();
            try { return n; }
            catch (System.InvalidOperationException) { throw; }
            catch (System.Exception e) { throw e; }
        }
    }

    public class User
    {
        public void Run(IShape shape, Shape concrete)
        {
            _ = shape.Area();
            concrete.Size = concrete.Size + 1;
            concrete.Size += 1;
            concrete.Changed += (s, e) => { };
            int total = new Money() + new Money();
            System.Func<double> area = concrete.Area;
            foreach (var c in ""ab"") { }
            using (var r = new Res()) { }
            var (x, y) = new Pt();
            _ = nameof(Shape.Size);
            System.Func<int> local = () => 3.Twice();
            _ = new Part().Compute();
        }
    }
}";

    private static readonly CSharpCompilation Compilation = CSharpCompilation.Create(
        "Edges",
        new[] { CSharpSyntaxTree.ParseText(Source) },
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p)),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static IMethodSymbol Method(string typeName, Func<IMethodSymbol, bool> pick) =>
        Compilation.GetTypeByMetadataName(typeName)!.GetMembers().OfType<IMethodSymbol>().Single(pick);

    private static List<(string target, CallEdgeKind kind, bool viaDispatch, bool inInitializer)> Edges(IMethodSymbol method)
    {
        var result = CallEdgeCollector.Collect(method, Compilation, CancellationToken.None);
        return result.Edges
            .Select(e => (e.Target.ToDisplayString(), e.Kind, e.ViaDispatch, e.InMemberInitializer))
            .ToList();
    }

    [Fact]
    public void Fixture_Compiles()
    {
        Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString()).Should().BeEmpty();
    }

    [Fact]
    public void MethodBody_ReportsEveryEdgeKindWithItsDispatch()
    {
        var edges = Edges(Method("N.User", m => m.Name == "Run"));

        edges.Should().BeEquivalentTo(new (string, CallEdgeKind, bool, bool)[]
        {
            ("N.IShape.Area()", CallEdgeKind.Invocation, true, false),
            ("N.Shape.Size.get", CallEdgeKind.PropertyGet, false, false),
            ("N.Shape.Size.set", CallEdgeKind.PropertySet, false, false),
            ("N.Shape.Size.get", CallEdgeKind.PropertyGet, false, false),
            ("N.Shape.Size.set", CallEdgeKind.PropertySet, false, false),
            ("N.Shape.Changed.add", CallEdgeKind.EventAdd, false, false),
            ("N.Money.Money()", CallEdgeKind.ObjectCreation, false, false),
            ("N.Money.Money()", CallEdgeKind.ObjectCreation, false, false),
            ("N.Money.operator +(N.Money, N.Money)", CallEdgeKind.Operator, false, false),
            ("N.Money.implicit operator int(N.Money)", CallEdgeKind.Conversion, false, false),
            ("N.Shape.Area()", CallEdgeKind.DelegateCreation, true, false),
            ("string.GetEnumerator()", CallEdgeKind.ImplicitCall, false, false),
            ("System.CharEnumerator.MoveNext()", CallEdgeKind.ImplicitCall, false, false),
            ("System.CharEnumerator.Current.get", CallEdgeKind.ImplicitCall, false, false),
            ("System.IDisposable.Dispose()", CallEdgeKind.ImplicitCall, true, false),
            ("N.Res.Res()", CallEdgeKind.ObjectCreation, false, false),
            ("N.Res.Dispose()", CallEdgeKind.ImplicitCall, false, false),
            ("N.Pt.Pt()", CallEdgeKind.ObjectCreation, false, false),
            ("N.Pt.Deconstruct(out int, out int)", CallEdgeKind.ImplicitCall, false, false),
            ("N.Ext.Twice(int)", CallEdgeKind.Invocation, false, false),
            ("N.Part.Part()", CallEdgeKind.ObjectCreation, false, false),
            ("N.Part.Compute()", CallEdgeKind.Invocation, false, false),
        }, options => options.WithoutStrictOrdering(),
            "nameof(Shape.Size) runs nothing; the lambda's call belongs to Run; the concrete.Size read and the " +
            "+= each run the getter and setter; foreach over a string runs CharEnumerator's members and its " +
            "IDisposable.Dispose; using runs Res.Dispose");
    }

    [Fact]
    public void PartialMethodCall_TargetsTheImplementationPart()
    {
        var call = CallEdgeCollector.Collect(Method("N.User", m => m.Name == "Run"), Compilation, CancellationToken.None)
            .Edges.Single(e => e.Target.Name == "Compute");

        ((IMethodSymbol)call.Target).PartialDefinitionPart.Should().NotBeNull(
            "the call binds to the definition part, but the implementation part carries the body");
    }

    [Fact]
    public void ReducedExtensionMethod_ResolvesToItsStaticDefinition()
    {
        var twice = Compilation.GetTypeByMetadataName("N.Ext")!.GetMembers("Twice").OfType<IMethodSymbol>().Single();
        var reduced = twice.ReduceExtensionMethod(Compilation.GetSpecialType(SpecialType.System_Int32))!;
        reduced.MethodKind.Should().Be(MethodKind.ReducedExtension);

        CallEdgeCollector.ResolveBodyCarrier(reduced).Should().BeSameAs(twice);
    }

    [Fact]
    public void OverrideCallingBase_IsANonDispatchedInvocation()
    {
        Edges(Method("N.Square", m => m.Name == "Area")).Should().Equal(
            ("N.Shape.Area()", CallEdgeKind.Invocation, false, false));
    }

    [Fact]
    public void ExplicitConstructor_ReportsItsInitializerCallAndTheMemberInitializersItRuns()
    {
        var edges = Edges(Method("N.Built", m => m.MethodKind == MethodKind.Constructor));

        edges.Should().BeEquivalentTo(new (string, CallEdgeKind, bool, bool)[]
        {
            ("N.Base.Base(int)", CallEdgeKind.ConstructorInitializer, false, false),
            ("N.Built.Seed()", CallEdgeKind.Invocation, false, true),
        });
    }

    [Fact]
    public void SynthesizedConstructor_RunsInitializersAndTheBaseParameterlessConstructor()
    {
        var implicitConstructor = Method("N.Implicit", m => m.MethodKind == MethodKind.Constructor);
        implicitConstructor.IsImplicitlyDeclared.Should().BeTrue();

        var result = CallEdgeCollector.Collect(implicitConstructor, Compilation, CancellationToken.None);

        result.BodyBound.Should().BeTrue("the field initializer was bound");
        result.Edges.Select(e => (e.Target.ToDisplayString(), e.Kind, e.ViaDispatch, e.InMemberInitializer))
            .Should().BeEquivalentTo(new (string, CallEdgeKind, bool, bool)[]
            {
                ("N.Built.Seed()", CallEdgeKind.Invocation, false, true),
                ("N.Base.Base()", CallEdgeKind.ConstructorInitializer, false, false),
            });
    }

    [Fact]
    public void PrimaryConstructor_ReportsItsBaseCallAndTheMemberInitializersItRuns()
    {
        var primary = Method("N.Prim", m => m.MethodKind == MethodKind.Constructor);

        Edges(primary).Should().BeEquivalentTo(new (string, CallEdgeKind, bool, bool)[]
        {
            ("N.Base.Base(int)", CallEdgeKind.ConstructorInitializer, false, false),
            ("N.Built.Seed()", CallEdgeKind.Invocation, false, true),
        }, "the base-type argument list is the primary constructor's : base(v) call");
    }

    [Fact]
    public void Edge_CarriesItsCallSiteAndTheReceiversStaticType()
    {
        var area = CallEdgeCollector.Collect(Method("N.User", m => m.Name == "Run"), Compilation, CancellationToken.None)
            .Edges.Single(e => e.Kind == CallEdgeKind.Invocation && e.Target.ToDisplayString() == "N.IShape.Area()");

        area.ReceiverType!.ToDisplayString().Should().Be("N.IShape");
        var site = CallEdgeCollector.SiteLocation(area);
        site.SourceTree!.GetText().ToString(site.SourceSpan).Should().Be("Area",
            "the site is the invoked member's name, where the call is written");
        site.GetLineSpan().StartLinePosition.Line.Should().Be(
            Source.Split('\n').ToList().FindIndex(l => l.Contains("_ = shape.Area();")));
    }

    [Fact]
    public void ThrowSites_ReportTheThrownTypeAndWhetherTheThrowRethrowsTheCaughtException()
    {
        var result = CallEdgeCollector.Collect(Method("N.Throws", m => m.Name == "Run"), Compilation, CancellationToken.None);

        result.Throws.Select(t => (t.ExceptionType?.ToDisplayString(), t.IsRethrow)).Should().BeEquivalentTo(new (string?, bool)[]
        {
            ("System.ArgumentNullException", false),
            ("System.FormatException", false),
            (null, true),
            ("System.Exception", true),
        }, "`throw;` rethrows whatever its catch caught, and `throw e;` rethrows the caught exception object");
    }
}
