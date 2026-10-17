using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// A test that calls a member through an interface- or base-typed receiver reaches the
// interface/base member, not the implementation or override that would run: with a mock
// or another implementation behind the receiver, the real code never runs. Such
// implementations and overrides stay untested but are labelled "Reached only via
// interface or virtual dispatch" in find_untested_code, and find_tests lists the
// dispatching tests separately as dispatchOnlyTests. A member that is only SEEDED
// (an interface member covered because a test calls one implementation directly) is not
// a dispatch, so other implementations of it keep the plain unreachable reason.
public class CoverageDispatchOnlyTests
{
    internal const string Lib = @"
namespace Lib
{
    public interface ICalc { int Triple(int x); }
    public class Calculator : ICalc
    {
        public int Triple(int x) => x * 3;
        public int Direct() => 1;
    }

    public abstract class Shape
    {
        public abstract double Area();
        public virtual string Name() => ""shape"";
    }
    public class Square : Shape
    {
        public override double Area() => 4;
        public override string Name() => ""square"";
    }

    public interface IGreeter { string Greet(); }
    public class Greeter : IGreeter { public string Greet() => ""hi""; }
    public class OtherGreeter : IGreeter { public string Greet() => ""yo""; }
}";

    internal const string Tests = @"
namespace Xunit
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class FactAttribute : System.Attribute { }
}

namespace Lib.Tests
{
    public class FakeCalc : Lib.ICalc { public int Triple(int x) => 0; }

    public class DispatchTests
    {
        [Xunit.Fact] public void ViaInterface() { Lib.ICalc c = new Lib.Calculator(); _ = c.Triple(1); }
        [Xunit.Fact] public void ViaFake() { Lib.ICalc c = new FakeCalc(); _ = c.Triple(1); }
        [Xunit.Fact] public void ViaBase() { Lib.Shape s = new Lib.Square(); _ = s.Area(); }
        [Xunit.Fact] public void DirectCall() { _ = new Lib.Calculator().Direct(); }
        [Xunit.Fact] public void GreetDirectly() { _ = new Lib.Greeter().Greet(); }
    }
}";

    private static async Task<JToken> Run(bool retarget, Func<RoslynService, Task<object>> call)
    {
        using var fixture = new RetargetedSolutionFixture(Lib, Tests, retarget);
        var service = await CoverageRetargetingTests.LoadAsync(fixture);
        return CoverageRetargetingTests.Data(await call(service));
    }

    private static List<string> Names(JToken data, string field) =>
        ((JArray)data[field]!).Select(t => t["testMethod"]!.Value<string>()!).ToList();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindUntestedCode_LabelsImplementationsAndOverridesReachedOnlyByDispatch(bool retarget)
    {
        var data = await Run(retarget, s => s.FindUntestedCodeAsync(
            projectName: RetargetedSolutionFixture.LibProjectName,
            includeProperties: false, includeInternal: false, maxResults: 100));

        ((JArray)data["uncoveredSymbols"]!)
            .Select(s => (s["fullName"]!.Value<string>()!, s["reason"]!.Value<string>()!))
            .Should().Equal(
                ("Lib.Calculator.Triple(int)", "Reached only via interface or virtual dispatch"),
                ("Lib.OtherGreeter.Greet()", "Not reachable from any test method"),
                ("Lib.Shape.Name()", "Not reachable from any test method"),
                ("Lib.Square.Area()", "Reached only via interface or virtual dispatch"),
                ("Lib.Square.Name()", "Not reachable from any test method"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindTests_ImplementationReachedOnlyByDispatch_ListsDispatchOnlyTests(bool retarget)
    {
        var data = await Run(retarget, s => s.FindTestsAsync("Lib.Calculator", "Triple"));

        Names(data, "coveringTests").Should().BeEmpty("no test calls Calculator.Triple itself");
        Names(data, "dispatchOnlyTests").Should().Equal(new[] { "ViaInterface", "ViaFake" },
            "both call ICalc.Triple through an interface-typed receiver, which Calculator.Triple implements");
        var entry = ((JArray)data["dispatchOnlyTests"]!)[0];
        entry["containingType"]!.Value<string>().Should().Be("Lib.Tests.DispatchTests");
        entry["testProject"]!.Value<string>().Should().Be(RetargetedSolutionFixture.TestProjectName);
        entry["kind"]!.Value<string>().Should().Be("test");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindTests_InterfaceMemberItself_IsCoveredByTheDispatchingTests(bool retarget)
    {
        var data = await Run(retarget, s => s.FindTestsAsync("Lib.ICalc", "Triple"));

        Names(data, "coveringTests").Should().Equal("ViaInterface", "ViaFake");
        Names(data, "dispatchOnlyTests").Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindTests_OverrideReachedOnlyThroughTheBase_IsDispatchOnly(bool retarget)
    {
        var data = await Run(retarget, s => s.FindTestsAsync("Lib.Square", "Area"));

        Names(data, "coveringTests").Should().BeEmpty();
        Names(data, "dispatchOnlyTests").Should().Equal("ViaBase");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindTests_TypeLevel_TypeNeverUsedButDispatchedInto_IsDispatchOnly(bool retarget)
    {
        // ViaInterface constructs a Calculator (its constructor is a Calculator member), so it
        // covers the type; ViaFake never touches Calculator but dispatches through ICalc.Triple.
        var data = await Run(retarget, s => s.FindTestsAsync("Lib.Calculator"));

        Names(data, "coveringTests").Should().Equal("ViaInterface", "DirectCall");
        Names(data, "dispatchOnlyTests").Should().Equal("ViaFake");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindTests_SeededInterfaceMember_IsNotADispatchIntoOtherImplementations(bool retarget)
    {
        var data = await Run(retarget, s => s.FindTestsAsync("Lib.OtherGreeter", "Greet"));

        Names(data, "coveringTests").Should().BeEmpty();
        Names(data, "dispatchOnlyTests").Should().BeEmpty(
            "GreetDirectly calls Greeter.Greet directly; IGreeter.Greet is only seeded, never dispatched through");
    }
}
