namespace SharpLensMcp.Tests;

// The deterministic in-memory sources the call-flow tool tests load (CallFlowFixture):
// every construct the call tools must agree on - extension and generic calls, object
// creation, property and indexer accessors, partial members, lambdas and same-named local
// functions, interface and virtual dispatch through open and sealed receivers, member
// initializers, and the exception-flow shapes (primary constructors, local functions,
// rethrow, ++ on a property, indexers, dispatch). Positions are found by marker
// (Locate), never by hard-coded numbers.
internal static class CallFlowSources
{
    public const string FileName = "Flow.cs";

    public const string Flow = """
        namespace Flow
        {
            public static class Target
            {
                public static int Leaf(int x) => x;
                public static void Hit() { }
                public static void Hit2() { }
                public static void Deep() { }
                public static void FromGetter() { }
                public static void InArea() { }
                public static void Danger() { }
                public static void FromPartial() { }
                public static int ForCg() => 0;
            }

            public static class Ext
            {
                public static int Twice(this int x) => Target.Leaf(x) * 2;
            }

            public class ExtUser
            {
                public int UseExt() { return 5.Twice(); }
            }

            public static class Gen
            {
                public static T Id<T>(T x) => x;
                public static void UseGen() { Id(1); Id("s"); 3.Twice(); Ext.Twice(4); }
            }

            public class LambdaUser
            {
                public void A() { Run(() => Target.Hit()); }
                public void B() { Run(() => Target.Hit2()); }
                public static void Run(System.Action a) => a();
            }

            public class LocA
            {
                public void S1() { Local(); void Local() { Target.Hit(); } }
                public void S2() { Local(); void Local() { } }
            }

            public class Worker
            {
                public Worker() { Target.Deep(); }
            }

            public class CtorUser
            {
                public void Make() { var w = new Worker(); }
            }

            public class PropHost
            {
                public int Value { get { Target.FromGetter(); return 1; } }
            }

            public class PropUser
            {
                public int Read(PropHost h) => h.Value;
            }

            public interface IShape { double Area(); }

            public class Sq : IShape
            {
                public double Area() { Target.InArea(); return 1; }
            }

            public class Circle : IShape
            {
                public double Area() => 3;
            }

            public class ShapeUser
            {
                public double Use(IShape s) => s.Area();
            }

            public sealed class Plain { }

            public class Noisy
            {
                public override string ToString() { Target.Danger(); return ""; }
            }

            public class ToStringUser
            {
                public string Show(Plain p) => p.ToString();
            }

            public partial class PartialHost
            {
                partial void Hook();
                partial void Hook() { Target.FromPartial(); }
                public void Fire() => Hook();
            }

            public class Sibling
            {
                public void Root() { }
                public void Other() { }
                public void Caller() { Root(); Other(); }
            }

            public class Chain
            {
                public void S() { A1(); }
                public void A1() { C1(); }
                public void C1() { B1(); }
                public void B1() { T1(); }
                public void T1() { }
            }

            public class CgHost
            {
                public int P => Target.ForCg();
                static int F = Target.ForCg();
                public void L() { System.Func<int> f = () => Target.ForCg(); }
                public void Direct() { Target.ForCg(); }
            }

            public class DepthHost
            {
                public int P { get { Target.Deep(); return 1; } }
                public void X() { Y(); }
                public void Y() { var v = P; }
            }

            public class Random
            {
                public void Roll() { Target.Hit(); }
            }

            public class Money
            {
                public static Money operator +(Money a, Money b) { Target.Hit(); return a; }
                ~Money() { Target.Hit2(); }
            }
        }

        namespace Flow.Ex
        {
            public class Widget(int size)
            {
                public int Size => size;
                public void Boom() => throw new System.InvalidOperationException("boom");
            }

            public class WidgetUser
            {
                public void Make() { var w = new Widget(1); }
            }

            public class LocalThrow
            {
                public void Run()
                {
                    Local();
                    void Local() => throw new System.ArgumentException("local");
                }
            }

            public class LocalCaught
            {
                public void Run()
                {
                    try { Local(); }
                    catch (System.ArgumentException) { }
                    void Local() => throw new System.ArgumentException("caught local");
                }
            }

            public static class ThrowExt
            {
                public static void Explode(this string s) => throw new System.FormatException("ext");
            }

            public class ExtThrowUser
            {
                public void Go() { "a".Explode(); }
            }

            public class Rethrower
            {
                public void M()
                {
                    try { Inner(); }
                    catch (System.Exception) { throw; }
                }

                void Inner() => throw new System.InvalidOperationException("inner");
            }

            public class IncHost
            {
                public int Counter { get => 0; set => throw new System.NotSupportedException("set"); }
                public void Bump() { Counter++; }
            }

            public class Indexed
            {
                public int this[int i] => throw new System.IndexOutOfRangeException("index");
                public int Get() { return this[0]; }
            }

            public partial class PartialThrow
            {
                partial void Hook2();
                partial void Hook2() { throw new System.TimeoutException("partial"); }
                public void Fire2() { Hook2(); }
            }

            public interface IThrower { void Do(); }

            public class ThrowerImpl : IThrower
            {
                public void Do() => throw new System.DivideByZeroException("dispatch");
            }

            public class IfaceUser
            {
                public void Use(IThrower t) { t.Do(); }
            }
        }
        """;

    // Zero-based (line, column) of the first occurrence of marker in Flow, plus offset
    // characters into the marker.
    public static (int line, int column) Locate(string marker, int offset = 0)
    {
        var (line, column) = RetargetedSources.Locate(Flow, marker);
        return (line, column + offset);
    }

    // Zero-based line of the first occurrence of marker in Flow.
    public static int LineOf(string marker) => Locate(marker).line;
}
