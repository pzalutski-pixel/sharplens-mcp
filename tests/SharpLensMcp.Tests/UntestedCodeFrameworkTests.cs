using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// In-memory tests for find_untested_code covering test-framework attribute detection
// (xUnit Theory, NUnit, MSTest), the includeProperties flag, the includeInternal flag,
// the IsTestMethod (ns,name) match (false-positive guard), and the InvalidParameter
// branch for unknown projects. Uses the AdhocWorkspace test seam so we can simulate
// each framework's attribute classes without taking real NuGet dependencies on NUnit
// or MSTest.
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

    // Returns the full data object so callers can lock testMethodCount,
    // productionProject, testProjectsScanned, and per-entry shape. Locks
    // `success` with NotBeNull-first to defeat the null-conditional silent-pass
    // pattern (`json["success"]?.Value<bool>().Should().BeTrue(...)` short-circuits
    // when the field is missing).
    private static async Task<JToken> RunAndGetData(string code, bool includeProperties, bool includeInternal, int maxResults = 50)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindUntestedCodeAsync(
            projectName: null,
            includeProperties: includeProperties,
            includeInternal: includeInternal,
            maxResults: maxResults);

        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success field");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return json["data"]!;
    }

    [Fact]
    public async Task FindUntestedCode_RecognizesXunitTheory()
    {
        var data = await RunAndGetData(XunitTheoryCode, includeProperties: true, includeInternal: true);
        // Locks the (ns, name) test-attribute match for ("Xunit", "TheoryAttribute").
        data["testMethodCount"]!.Value<int>().Should().Be(1,
            "the fixture declares exactly one [Xunit.Theory] method");
        data["productionProject"]!.Value<string>().Should().Be("TestProject");

        var uncovered = (data["uncoveredSymbols"] as JArray)!;
        var names = uncovered.Select(u => u["fullName"]!.Value<string>()!).ToList();
        names.Should().Contain(n => n.Contains("Target.Unreached"));
        names.Should().NotContain(n => n.Contains("Target.Reached"),
            "Reached is invoked from the [Theory] test method");
    }

    [Fact]
    public async Task FindUntestedCode_RecognizesNUnitTest()
    {
        var data = await RunAndGetData(NUnitTestCode, includeProperties: true, includeInternal: true);
        // Locks the (ns, name) test-attribute match for ("NUnit.Framework", "TestAttribute").
        data["testMethodCount"]!.Value<int>().Should().Be(1,
            "the fixture declares exactly one [NUnit.Framework.Test] method");
        data["productionProject"]!.Value<string>().Should().Be("TestProject");

        var uncovered = (data["uncoveredSymbols"] as JArray)!;
        var names = uncovered.Select(u => u["fullName"]!.Value<string>()!).ToList();
        names.Should().Contain(n => n.Contains("Target.Unreached"));
        names.Should().NotContain(n => n.Contains("Target.Reached"),
            "Reached is invoked from the NUnit [Test] method");
    }

    [Fact]
    public async Task FindUntestedCode_RecognizesMSTestTestMethod()
    {
        var data = await RunAndGetData(MSTestCode, includeProperties: true, includeInternal: true);
        // Locks the (ns, name) test-attribute match for the MSTest TestMethodAttribute.
        data["testMethodCount"]!.Value<int>().Should().Be(1,
            "the fixture declares exactly one MSTest [TestMethod] method");
        data["productionProject"]!.Value<string>().Should().Be("TestProject");

        var uncovered = (data["uncoveredSymbols"] as JArray)!;
        var names = uncovered.Select(u => u["fullName"]!.Value<string>()!).ToList();
        names.Should().Contain(n => n.Contains("Target.Unreached"));
        names.Should().NotContain(n => n.Contains("Target.Reached"),
            "Reached is invoked from the MSTest [TestMethod]");
    }

    [Fact]
    public async Task FindUntestedCode_IncludeProperties_SurfacesUntestedProperty()
    {
        var data = await RunAndGetData(PropertyCoverageCode, includeProperties: true, includeInternal: true);
        data["testMethodCount"]!.Value<int>().Should().Be(1);
        data["productionProject"]!.Value<string>().Should().Be("TestProject");

        var uncovered = (data["uncoveredSymbols"] as JArray)!;
        var names = uncovered.Select(u => u["fullName"]!.Value<string>()!).ToList();
        names.Should().Contain(n => n.EndsWith(".Unused"),
            "Unused property has no test references");
        names.Should().NotContain(n => n.EndsWith(".Used"),
            "Used property is read and written from the [Fact] test");
    }

    [Fact]
    public async Task FindUntestedCode_IncludeInternal_SurfacesInternalUnreached()
    {
        var data = await RunAndGetData(InternalCode, includeProperties: true, includeInternal: true);
        data["testMethodCount"]!.Value<int>().Should().Be(1);
        data["productionProject"]!.Value<string>().Should().Be("TestProject");

        var uncovered = (data["uncoveredSymbols"] as JArray)!;
        var names = uncovered.Select(u => u["fullName"]!.Value<string>()!).ToList();
        names.Should().Contain(n => n.Contains("Target.InternalUnreached"),
            "with includeInternal:true, the internal method must be flagged as uncovered");
    }

    [Fact]
    public async Task FindUntestedCode_DoesNotMatchSameNameDifferentNamespace()
    {
        // MyApp.Attributes.FactAttribute has the same simple name as xUnit's [Fact]
        // but a different namespace — must NOT be treated as a test attribute. The
        // (ns, name) tuple lookup at Quality.cs:191 is the guard against this kind
        // of false positive that would inflate the reachable set.
        var data = await RunAndGetData(FalsePositiveSameNameCode, includeProperties: true, includeInternal: true);
        data["testMethodCount"]!.Value<int>().Should().Be(0,
            "the look-alike attribute lives in MyApp.Attributes, not Xunit — IsTestMethod must reject it");

        // testProjectsScanned tracks projects where IsTestMethod found something.
        // With zero hits, the set must stay empty (Quality.cs:90, 108).
        var scanned = (data["testProjectsScanned"] as JArray)!;
        scanned.Count.Should().Be(0,
            "no project should be marked as test-scanned when no test method was detected");

        var uncovered = (data["uncoveredSymbols"] as JArray)!;
        var names = uncovered.Select(u => u["fullName"]!.Value<string>()!).ToList();
        names.Should().Contain(n => n.Contains("Target.NotActuallyTested"),
            "the look-alike-attributed method does not count as a test, so the called production method stays uncovered");
    }

    [Fact]
    public async Task FindUntestedCode_ResponseLocksTestMethodCountAndPerEntryShape()
    {
        // The framework-recognition tests above lock testMethodCount and fullName
        // but not the per-entry shape. This test pins kind, accessibility,
        // complexity, location, and reason — a regression that drops any one of
        // those would otherwise pass every framework-recognition test.
        var data = await RunAndGetData(XunitTheoryCode, includeProperties: true, includeInternal: true);

        data["testMethodCount"]!.Value<int>().Should().Be(1);

        var uncovered = (data["uncoveredSymbols"] as JArray)!;
        var unreached = uncovered.FirstOrDefault(u =>
            u["fullName"]?.Value<string>()?.Contains("Target.Unreached") == true);
        unreached.Should().NotBeNull();

        // Per-entry shape (Quality.cs:62-70).
        unreached!["kind"]!.Value<string>().Should().Be("Method",
            "Unreached is declared as a method");
        unreached["accessibility"]!.Value<string>().Should().Be("Public");
        unreached["complexity"]!.Type.Should().Be(JTokenType.Integer,
            "complexity is always reported as an integer (cyclomatic)");
        unreached["complexity"]!.Value<int>().Should().BeGreaterOrEqualTo(1,
            "EstimateCyclomaticAsync returns 1 + branches; the floor is 1");
        unreached["location"].Should().NotBeNull(
            "every uncovered entry must surface its declaration location");
        unreached["reason"]!.Value<string>().Should().Be("Not reachable from any test method",
            "the impl's documented reason string at Quality.cs:69 must round-trip verbatim");
    }

    [Fact]
    public async Task FindUntestedCode_IncludeInternalFalse_ExcludesInternalMethods()
    {
        // Companion to IncludeInternal_SurfacesInternalUnreached (which uses true).
        // With includeInternal=false (the default per Quality.cs:37), internal
        // symbols must NOT appear in the uncovered list.
        var data = await RunAndGetData(InternalCode, includeProperties: true, includeInternal: false);
        data["testMethodCount"]!.Value<int>().Should().Be(1,
            "the test method is still discovered regardless of the includeInternal flag");
        data["productionProject"]!.Value<string>().Should().Be("TestProject");

        var uncovered = (data["uncoveredSymbols"] as JArray)!;
        var names = uncovered.Select(u => u["fullName"]!.Value<string>()!).ToList();
        names.Should().NotContain(n => n.Contains("InternalUnreached"),
            "with includeInternal=false, the internal method must NOT appear in the uncovered list");
    }

    [Fact]
    public async Task FindUntestedCode_OnNonExistentProject_ReturnsInvalidParameter()
    {
        // Quality.cs:139-142 throws ArgumentException for an unknown projectName;
        // FindUntestedCodeAsync (line 47-53) catches it and returns InvalidParameter.
        // Lock that branch — without it, a regression to letting the exception escape
        // would crash the tool rather than surface a structured error.
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(XunitTheoryCode);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindUntestedCodeAsync(
            projectName: "DoesNotExist_12345",
            includeProperties: true,
            includeInternal: true,
            maxResults: 50);

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Contain("DoesNotExist_12345",
            "the error message must echo the bad project name for caller correlation");
    }
}
