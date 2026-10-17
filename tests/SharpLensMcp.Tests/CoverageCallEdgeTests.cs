using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// The call edges the coverage walk must follow beyond plain invocations: each case puts
// production members in Lib that a Lib.Tests [Fact] reaches ONLY through one kind of
// edge, plus members nothing reaches, and pins find_untested_code's exact uncovered list.
// Every case runs on the retargeted and the control two-project shape.
public class CoverageCallEdgeTests
{
    private const string FactStub = @"
namespace Xunit
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class FactAttribute : System.Attribute { }
}
";

    private static string Tests(string body) => FactStub + @"
namespace Lib.Tests
{
    public class EdgeTests
    {
" + body + @"
    }
}";

    private static async Task<JToken> UntestedLib(string libSource, string testSource, bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(libSource, testSource, retarget);
        var service = await CoverageRetargetingTests.LoadAsync(fixture);
        return CoverageRetargetingTests.Data(await service.FindUntestedCodeAsync(
            projectName: RetargetedSolutionFixture.LibProjectName,
            includeProperties: false, includeInternal: false, maxResults: 100));
    }

    private static List<string> UncoveredNames(JToken data) =>
        ((JArray)data["uncoveredSymbols"]!).Select(s => s["fullName"]!.Value<string>()!).ToList();

    internal const string PropertyLib = @"
namespace Lib
{
    public class P
    {
        public int Doubled => Twice(3);
        public int Twice(int x) => x * 2;

        public int Prop { get => GetCore(); set => SetCore(value); }
        public int GetCore() => 1;
        public void SetCore(int v) { }

        public int Counter { get => ReadCounter(); set => WriteCounter(value); }
        public int ReadCounter() => 0;
        public void WriteCounter(int v) { }

        public int Sink { get => SinkGet(); set => SinkSet(value); }
        public int SinkGet() => 0;
        public void SinkSet(int v) { }

        public int InitOnly { get => InitGet(); set => InitSet(value); }
        public int InitGet() => 0;
        public void InitSet(int v) { }

        public int this[int i] { get => IndexGet(i); set => IndexSet(i, value); }
        public int IndexGet(int i) => i;
        public void IndexSet(int i, int v) { }
    }
}";

    internal static readonly string PropertyTests = Tests(@"
        [Xunit.Fact]
        public void Uses()
        {
            var p = new Lib.P { InitOnly = 1 };
            _ = p.Doubled;
            _ = p.Prop;
            p.Counter += 1;
            p.Sink = 2;
            _ = p[0];
        }");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PropertyAndIndexerAccessors_WalkTheAccessorActuallyUsed(bool retarget)
    {
        var data = await UntestedLib(PropertyLib, PropertyTests, retarget);

        // A read walks the getter, a write (assignment, object initializer) the setter, a
        // compound assignment both; the accessor never used leaves its callee unreached.
        UncoveredNames(data).Should().Equal(
            "Lib.P.IndexSet(int, int)",
            "Lib.P.InitGet()",
            "Lib.P.SetCore(int)",
            "Lib.P.SinkGet()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PropertyGetter_FindTestsReportsTheReadingTest(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(PropertyLib, PropertyTests, retarget);
        var service = await CoverageRetargetingTests.LoadAsync(fixture);
        var data = CoverageRetargetingTests.Data(await service.FindTestsAsync("Lib.P", "Twice"));

        ((JArray)data["coveringTests"]!).Select(t => t["testMethod"]!.Value<string>())
            .Should().Equal(new[] { "Uses" }, "Twice is reached only through the Doubled getter, which Uses reads");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TargetTypedNew_ReachesTheConstructor(bool retarget)
    {
        const string lib = @"
namespace Lib
{
    public class Made
    {
        public Made() { Init(); }
        public void Init() { }
        public void Untouched() { }
    }
}";
        var data = await UntestedLib(lib, Tests(@"
        [Xunit.Fact]
        public void Builds() { Lib.Made m = new(); }"), retarget);

        UncoveredNames(data).Should().Equal("Lib.Made.Untouched()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConstructorInitializers_AndImplicitBaseCalls_AreFollowed(bool retarget)
    {
        const string lib = @"
namespace Lib
{
    public class Base { public Base(int x) { BaseWork(); } public void BaseWork() { } }
    public class Derived : Base { public Derived() : base(1) { } public Derived(string s) : this() { } }

    public class Base2 { public Base2() { Base2Work(); } public void Base2Work() { } }
    public class Derived2 : Base2 { public Derived2() { } }

    public class Base3 { public Base3() { Base3Work(); } public void Base3Work() { } }
    public class Derived3 : Base3 { }

    public class Unbuilt { public Unbuilt() { } }
}";
        var data = await UntestedLib(lib, Tests(@"
        [Xunit.Fact]
        public void Builds()
        {
            _ = new Lib.Derived(""a"");
            _ = new Lib.Derived2();
            _ = new Lib.Derived3();
        }"), retarget);

        // : this() and : base(1) are explicit initializer calls; Derived2's explicit and
        // Derived3's implicit constructor each call their base's parameterless constructor.
        UncoveredNames(data).Should().Equal("Lib.Unbuilt.Unbuilt()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FieldPropertyAndStaticInitializers_RunWithTheirConstructor(bool retarget)
    {
        const string lib = @"
namespace Lib
{
    public class WithInit
    {
        private readonly int _v = Seed();
        public int Auto { get; } = AutoSeed();
        public static int Seed() => 1;
        public static int AutoSeed() => 2;
    }

    public class WithInit2
    {
        private int _w = Seed2();
        public WithInit2() { }
        public static int Seed2() => 1;
    }

    public class StaticInit
    {
        private static readonly int S = SSeed();
        public static int Get() => S;
        public static int SSeed() => 1;
    }

    public class StaticCtor
    {
        static StaticCtor() { SCtorWork(); }
        public static void Touch() { }
        public static void SCtorWork() { }
    }

    public class NotBuilt
    {
        private int _n = NeverSeed();
        public static int NeverSeed() => 0;
    }
}";
        var data = await UntestedLib(lib, Tests(@"
        [Xunit.Fact]
        public void Builds()
        {
            _ = new Lib.WithInit();
            _ = new Lib.WithInit2();
            _ = Lib.StaticInit.Get();
            Lib.StaticCtor.Touch();
        }"), retarget);

        // Instance initializers run as part of the (implicit or explicit) constructor that
        // a test reaches; static initializers and the static constructor run when any
        // member of the type is first used. NotBuilt is never constructed.
        UncoveredNames(data).Should().Equal("Lib.NotBuilt.NeverSeed()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UserDefinedOperatorsAndConversions_AreFollowed(bool retarget)
    {
        const string lib = @"
namespace Lib
{
    public struct Money
    {
        public int V;
        public static Money operator +(Money a, Money b) => Combine(a, b);
        public static Money operator -(Money a) => Negate(a);
        public static implicit operator int(Money m) => ToInt(m);
        public static explicit operator Money(int v) => FromInt(v);
        public static Money Combine(Money a, Money b) => a;
        public static Money Negate(Money a) => a;
        public static int ToInt(Money m) => m.V;
        public static Money FromInt(int v) => default(Money);
        public static Money Spare() => default(Money);

        public static bool operator true(Money m) => IsTrue(m);
        public static bool operator false(Money m) => IsFalse(m);
        public static Money operator &(Money a, Money b) => And(a, b);
        public static Money operator |(Money a, Money b) => Or(a, b);
        public static bool IsTrue(Money m) => true;
        public static bool IsFalse(Money m) => false;
        public static Money And(Money a, Money b) => a;
        public static Money Or(Money a, Money b) => a;
    }
}";
        var data = await UntestedLib(lib, Tests(@"
        [Xunit.Fact]
        public void Computes()
        {
            var m = new Lib.Money() + new Lib.Money();
            var n = -m;
            int i = n;
            var k = (Lib.Money)5;
            var both = m && n;
        }"), retarget);

        // m && n tests m with operator false and then applies operator &; operator true
        // and operator | belong to ||, which nothing uses.
        UncoveredNames(data).Should().Equal(
            "Lib.Money.IsTrue(Lib.Money)", "Lib.Money.Or(Lib.Money, Lib.Money)", "Lib.Money.Spare()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EventSubscription_WalksTheAddAccessorOnly(bool retarget)
    {
        const string lib = @"
namespace Lib
{
    public class Bus
    {
        public event System.EventHandler Changed { add { OnAdd(); } remove { OnRemove(); } }
        public void OnAdd() { }
        public void OnRemove() { }
    }
}";
        var data = await UntestedLib(lib, Tests(@"
        [Xunit.Fact]
        public void Subscribes()
        {
            var b = new Lib.Bus();
            b.Changed += (s, e) => { };
        }"), retarget);

        UncoveredNames(data).Should().Equal("Lib.Bus.OnRemove()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Foreach_FollowsGetEnumeratorMoveNextAndCurrent(bool retarget)
    {
        const string lib = @"
namespace Lib
{
    public class Bag
    {
        public Enumerator GetEnumerator() => new Enumerator();

        public struct Enumerator
        {
            public bool MoveNext() => Step();
            public int Current => Peek();
            public static bool Step() => false;
            public static int Peek() => 0;
            public void Reset() { }
        }
    }
}";
        var data = await UntestedLib(lib, Tests(@"
        [Xunit.Fact]
        public void Iterates() { foreach (var x in new Lib.Bag()) { } }"), retarget);

        UncoveredNames(data).Should().Equal("Lib.Bag.Enumerator.Reset()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UsingStatementAndDeclaration_FollowDispose(bool retarget)
    {
        const string lib = @"
namespace Lib
{
    public class Res : System.IDisposable
    {
        public void Dispose() => Release();
        public void Release() { }
        public void Spare() { }
    }

    public class Res2 : System.IDisposable
    {
        public void Dispose() => Release2();
        public void Release2() { }
    }
}";
        var data = await UntestedLib(lib, Tests(@"
        [Xunit.Fact]
        public void Disposes()
        {
            using (var r = new Lib.Res()) { }
            using var r2 = new Lib.Res2();
        }"), retarget);

        UncoveredNames(data).Should().Equal("Lib.Res.Spare()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CollectionInitializer_FollowsAdd(bool retarget)
    {
        const string lib = @"
namespace Lib
{
    public class Coll : System.Collections.IEnumerable
    {
        public void Add(int x) => Store(x);
        public void Store(int x) { }
        public System.Collections.IEnumerator GetEnumerator() => null;
    }
}";
        var data = await UntestedLib(lib, Tests(@"
        [Xunit.Fact]
        public void Fills() { var c = new Lib.Coll { 1, 2 }; }"), retarget);

        UncoveredNames(data).Should().Equal("Lib.Coll.GetEnumerator()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeconstructionAndPositionalPatterns_FollowDeconstruct(bool retarget)
    {
        const string lib = @"
namespace Lib
{
    public class Pt
    {
        public void Deconstruct(out int x, out int y) { x = X(); y = 0; }
        public int X() => 1;
        public int Y() => 2;
    }

    public class Pt2
    {
        public void Deconstruct(out int x, out int y) { x = X2(); y = 0; }
        public int X2() => 1;
    }
}";
        var data = await UntestedLib(lib, Tests(@"
        [Xunit.Fact]
        public void Splits()
        {
            var (a, b) = new Lib.Pt();
            _ = new Lib.Pt2() is (1, _);
        }"), retarget);

        UncoveredNames(data).Should().Equal("Lib.Pt.Y()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalFunctionsAndLambdas_InsideABody_AreFollowed(bool retarget)
    {
        const string lib = @"
namespace Lib
{
    public class Fn
    {
        public int ViaLocal() { int L() => LocalTarget(); return L(); }
        public int LocalTarget() => 1;
        public int ViaLambda() { System.Func<int> f = () => LambdaTarget(); return f(); }
        public int LambdaTarget() => 2;
        public int Spare() => 3;
    }
}";
        var data = await UntestedLib(lib, Tests(@"
        [Xunit.Fact]
        public void Calls() { var f = new Lib.Fn(); _ = f.ViaLocal(); _ = f.ViaLambda(); }"), retarget);

        UncoveredNames(data).Should().Equal("Lib.Fn.Spare()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExtensionMethods_CalledAndPassedAsMethodGroups_ReachTheirDefinition(bool retarget)
    {
        const string lib = @"
namespace Lib
{
    public static class Ext
    {
        public static int Twice(this int x) => TwiceCore(x);
        public static int TwiceCore(int x) => x * 2;
        public static int Thrice(this string s) => ThriceCore(s.Length);
        public static int ThriceCore(int x) => x * 3;
        public static int Spare() => 0;
    }
}";
        var data = await UntestedLib(lib, Tests(@"
        [Xunit.Fact]
        public void Extends()
        {
            _ = Lib.Ext.Twice(3);
            _ = 3.Twice();
            System.Func<int> f = ""abc"".Thrice;
            _ = f();
        }"), retarget);

        // A call in extension form and an extension method group both reach the static
        // definition, whose body runs.
        UncoveredNames(data).Should().Equal("Lib.Ext.Spare()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartialMembers_WalkTheImplementationPart(bool retarget)
    {
        const string lib = @"
namespace Lib
{
    public partial class Part
    {
        public partial int Compute();
        public partial int Value { get; }
    }

    public partial class Part
    {
        public partial int Compute() => ComputeCore();
        public partial int Value { get => ValueCore(); }
        public int ComputeCore() => 1;
        public int ValueCore() => 2;
        public int Spare() => 0;
    }
}";
        var data = await UntestedLib(lib, Tests(@"
        [Xunit.Fact]
        public void Uses() { var p = new Lib.Part(); _ = p.Compute(); _ = p.Value; }"), retarget);

        // A call binds to the partial member's definition part, which has no body; the
        // implementation part's body is the one that runs.
        UncoveredNames(data).Should().Equal("Lib.Part.Spare()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Await_FollowsTheAwaiterPattern(bool retarget)
    {
        const string lib = @"
namespace Lib
{
    public class Job { public JobAwaiter GetAwaiter() => new JobAwaiter(); }

    public struct JobAwaiter : System.Runtime.CompilerServices.INotifyCompletion
    {
        public bool IsCompleted => Done();
        public int GetResult() => Result();
        public void OnCompleted(System.Action continuation) => Resume(continuation);
        public static bool Done() => true;
        public static int Result() => 0;
        public static void Resume(System.Action continuation) { }
        public void Spare() { }
    }
}";
        var data = await UntestedLib(lib, Tests(@"
        [Xunit.Fact]
        public async System.Threading.Tasks.Task Awaits() { _ = await new Lib.Job(); }"), retarget);

        // await calls GetAwaiter, IsCompleted and GetResult, and the generated state
        // machine schedules the continuation through INotifyCompletion.OnCompleted.
        UncoveredNames(data).Should().Equal("Lib.JobAwaiter.Spare()");
    }
}
