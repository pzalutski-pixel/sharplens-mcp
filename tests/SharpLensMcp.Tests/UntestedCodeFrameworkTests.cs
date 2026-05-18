using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// In-memory tests for find_untested_code covering test-framework attribute detection
// (xUnit Theory, NUnit, MSTest), the includeProperties flag, and the includeInternal
// flag. Uses the AdhocWorkspace test seam so we can simulate each framework's attribute
// classes without taking real NuGet dependencies on NUnit / MSTest.
public class UntestedCodeFrameworkTests
{
    // Each framework gets its own minimal attribute class in the SAME namespace as
    // the real framework. The IsTestMethod detection matches by (namespace, name)
    // so these stub attributes are indistinguishable from the real thing.
    private const string XunitTheoryCode = @"
namespace Xunit
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class TheoryAttribute : System.Attribute { }
}

namespace Production
{
    public class Target
    {
        public int Reached() => 1;
        public int Unreached() => 2;
    }
}

namespace Tests
{
    public class T
    {
        [Xunit.Theory]
        public void TestReachedByTheory()
        {
            var t = new Production.Target();
            _ = t.Reached();
        }
    }
}";

    private const string NUnitTestCode = @"
namespace NUnit.Framework
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class TestAttribute : System.Attribute { }
}

namespace Production
{
    public class Target
    {
        public int Reached() => 1;
        public int Unreached() => 2;
    }
}

namespace Tests
{
    public class T
    {
        [NUnit.Framework.Test]
        public void TestReachedByNUnit()
        {
            var t = new Production.Target();
            _ = t.Reached();
        }
    }
}";

    private const string MSTestCode = @"
namespace Microsoft.VisualStudio.TestTools.UnitTesting
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class TestMethodAttribute : System.Attribute { }
}

namespace Production
{
    public class Target
    {
        public int Reached() => 1;
        public int Unreached() => 2;
    }
}

namespace Tests
{
    public class T
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void TestReachedByMSTest()
        {
            var t = new Production.Target();
            _ = t.Reached();
        }
    }
}";

    private const string PropertyCoverageCode = @"
namespace Xunit
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class FactAttribute : System.Attribute { }
}

namespace Production
{
    public class HasProperties
    {
        public int Used { get; set; }
        public int Unused { get; set; }
    }
}

namespace Tests
{
    public class T
    {
        [Xunit.Fact]
        public void UsesUsedProperty()
        {
            var x = new Production.HasProperties();
            x.Used = 5;
            _ = x.Used;
        }
    }
}";

    private const string InternalCode = @"
namespace Xunit
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class FactAttribute : System.Attribute { }
}

namespace Production
{
    public class Target
    {
        public int PublicReached() => 1;
        internal int InternalUnreached() => 2;
    }
}

namespace Tests
{
    public class T
    {
        [Xunit.Fact]
        public void ReachPublicOnly()
        {
            var t = new Production.Target();
            _ = t.PublicReached();
        }
    }
}";

    private const string FalsePositiveSameNameCode = @"
namespace MyApp.Attributes
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class FactAttribute : System.Attribute { }
}

namespace Production
{
    public class Target
    {
        public int NotActuallyTested() => 1;
    }
}

namespace Tests
{
    public class T
    {
        // Same simple name as xUnit's [Fact] but different namespace.
        // Must NOT be treated as a test attribute.
        [MyApp.Attributes.Fact]
        public void NotATest()
        {
            var t = new Production.Target();
            _ = t.NotActuallyTested();
        }
    }
}";

    private static async Task<JArray> RunAndGetUncovered(string code)
    {
        var data = await RunAndGetData(code, includeProperties: true, includeInternal: true);
        return (data["uncoveredSymbols"] as JArray)!;
    }

    // Helper that returns the full data object (not just the uncoveredSymbols array)
    // so callers can inspect testMethodCount, productionProject, per-entry shape, etc.
    // Locks `success` with NotBeNull-first to defeat the null-conditional silent-pass
    // pattern (`json["success"]?.Value<bool>().Should().BeTrue(...)` short-circuits if
    // the field is missing).
    private static async Task<JToken> RunAndGetData(string code, bool includeProperties, bool includeInternal)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindUntestedCodeAsync(
            projectName: null,
            includeProperties: includeProperties,
            includeInternal: includeInternal,
            maxResults: 50);

        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success field");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return json["data"]!;
    }

    [Fact]
    public async Task FindUntestedCode_RecognizesXunitTheory()
    {
        var uncovered = await RunAndGetUncovered(XunitTheoryCode);
        var names = uncovered.Select(u => u["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.Contains("Target.Unreached"));
        names.Should().NotContain(n => n.Contains("Target.Reached"),
            "Reached is invoked from a [Theory] test method");
    }

    [Fact]
    public async Task FindUntestedCode_RecognizesNUnitTest()
    {
        var uncovered = await RunAndGetUncovered(NUnitTestCode);
        var names = uncovered.Select(u => u["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.Contains("Target.Unreached"));
        names.Should().NotContain(n => n.Contains("Target.Reached"),
            "Reached is invoked from an NUnit [Test] method");
    }

    [Fact]
    public async Task FindUntestedCode_RecognizesMSTestTestMethod()
    {
        var uncovered = await RunAndGetUncovered(MSTestCode);
        var names = uncovered.Select(u => u["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.Contains("Target.Unreached"));
        names.Should().NotContain(n => n.Contains("Target.Reached"),
            "Reached is invoked from an MSTest [TestMethod]");
    }

    [Fact]
    public async Task FindUntestedCode_IncludeProperties_SurfacesUntestedProperty()
    {
        var uncovered = await RunAndGetUncovered(PropertyCoverageCode);
        var names = uncovered.Select(u => u["fullName"]?.Value<string>() ?? "").ToList();

        names.Should().Contain(n => n.EndsWith(".Unused"),
            "Unused property has no test references");
        names.Should().NotContain(n => n.EndsWith(".Used"),
            "Used property is read and written from the [Fact] test");
    }

    [Fact]
    public async Task FindUntestedCode_IncludeInternal_SurfacesInternalUnreached()
    {
        var uncovered = await RunAndGetUncovered(InternalCode);
        var names = uncovered.Select(u => u["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.Contains("Target.InternalUnreached"),
            "with includeInternal:true, the internal method should be flagged as uncovered");
    }

    [Fact]
    public async Task FindUntestedCode_DoesNotMatchSameNameDifferentNamespace()
    {
        // MyApp.Attributes.FactAttribute has the same simple name as xUnit's [Fact]
        // but a different namespace — must NOT be treated as a test attribute.
        // Therefore NotActuallyTested IS uncovered (no real test reaches it).
        var uncovered = await RunAndGetUncovered(FalsePositiveSameNameCode);
        var names = uncovered.Select(u => u["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.Contains("Target.NotActuallyTested"),
            "the look-alike attribute is in MyApp.Attributes, not Xunit, so the 'test' method shouldn't count as a test");
    }

    [Fact]
    public async Task FindUntestedCode_ResponseLocksTestMethodCountAndPerEntryShape()
    {
        // The previous tests only checked uncoveredSymbols[*].fullName. Lock the
        // top-level testMethodCount AND the per-entry shape — without this, a
        // regression that drops kind/accessibility/complexity/location/reason would
        // silently pass every prior test.
        var data = await RunAndGetData(XunitTheoryCode, includeProperties: true, includeInternal: true);

        // Each fixture above has exactly one test method (TestReachedByTheory).
        data["testMethodCount"].Should().NotBeNull();
        data["testMethodCount"]!.Value<int>().Should().Be(1,
            "XunitTheoryCode has exactly one [Theory] test method");

        var uncovered = (data["uncoveredSymbols"] as JArray)!;
        var unreached = uncovered.FirstOrDefault(u =>
            u["fullName"]?.Value<string>()?.Contains("Target.Unreached") == true);
        unreached.Should().NotBeNull();

        // Per-entry shape (Quality.cs:62-70): fullName, kind, complexity, accessibility,
        // location, reason — every field must be present on the matched entry.
        unreached!["kind"].Should().NotBeNull("entry must carry kind");
        unreached["kind"]!.Value<string>().Should().Be("Method",
            "Unreached is declared as a method");
        unreached["accessibility"].Should().NotBeNull();
        unreached["accessibility"]!.Value<string>().Should().Be("Public");
        unreached["complexity"].Should().NotBeNull();
        unreached["complexity"]!.Type.Should().Be(JTokenType.Integer,
            "complexity is always reported as an integer (cyclomatic)");
        unreached["location"].Should().NotBeNull(
            "every uncovered entry must surface its declaration location");
        unreached["reason"]!.Value<string>().Should().Contain("Not reachable from any test method",
            "the impl's documented reason string must round-trip");
    }

    [Fact]
    public async Task FindUntestedCode_IncludeInternalFalse_ExcludesInternalMethods()
    {
        // Companion to IncludeInternal_SurfacesInternalUnreached (which uses true).
        // With includeInternal=false (the default), internal symbols must NOT appear
        // in the uncovered list.
        var data = await RunAndGetData(InternalCode, includeProperties: true, includeInternal: false);
        var uncovered = (data["uncoveredSymbols"] as JArray)!;
        var names = uncovered.Select(u => u["fullName"]?.Value<string>() ?? "").ToList();

        names.Should().NotContain(n => n.Contains("InternalUnreached"),
            "with includeInternal=false, the internal method must NOT appear in the uncovered list");
    }
}
