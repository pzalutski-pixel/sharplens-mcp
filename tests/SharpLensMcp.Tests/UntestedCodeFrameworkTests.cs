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
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindUntestedCodeAsync(
            projectName: null,
            includeProperties: true,
            includeInternal: true,
            maxResults: 50);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        return (json["data"]!["uncoveredSymbols"] as JArray)!;
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
}
