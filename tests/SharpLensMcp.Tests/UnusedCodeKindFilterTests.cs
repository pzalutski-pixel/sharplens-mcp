using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_unused_code's symbolKindFilter must narrow to the individual kind, not just
// the group: "Method" reports only methods (not properties/fields), and "Class"
// reports only classes (not interfaces/structs).
public class UnusedCodeKindFilterTests
{
    private const string Code = @"
namespace Demo;

public interface IThing { }
public class DeadClass { }

public class Holder
{
    public void DeadMethod() { }
    public int DeadProp => 1;
    public int DeadField;
}
";

    private async Task<List<string>> UnusedWithFilter(string filter)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Kinds.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindUnusedCodeAsync(
            "TestProject", includePrivate: false, includeInternal: false, symbolKindFilter: filter);
        return (JObject.FromObject(result)["data"]!["unusedSymbols"] as JArray)!
            .Select(s => s["fullyQualifiedName"]!.Value<string>()!).ToList();
    }

    [Fact]
    public async Task MethodFilter_ReportsOnlyMethods()
    {
        var names = await UnusedWithFilter("Method");

        names.Should().Contain(n => n.Contains("Holder.DeadMethod"),
            "an unused method is reported under the Method filter");
        names.Should().NotContain(n => n.Contains("DeadProp"), "properties are excluded by the Method filter");
        names.Should().NotContain(n => n.Contains("DeadField"), "fields are excluded by the Method filter");
        names.Should().NotContain(n => n.Contains("DeadClass"), "types are excluded by the Method filter");
    }

    [Fact]
    public async Task ClassFilter_ReportsOnlyClasses()
    {
        var names = await UnusedWithFilter("Class");

        names.Should().Contain("Demo.DeadClass", "an unused class is reported under the Class filter");
        names.Should().Contain("Demo.Holder");
        names.Should().NotContain("Demo.IThing", "interfaces are excluded by the Class filter");
        names.Should().NotContain(n => n.Contains("DeadMethod"), "members are excluded by the Class filter");
    }

    [Fact]
    public async Task UnknownFilter_FindUnusedCode_ReturnsInvalidParameter()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Kinds.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.FindUnusedCodeAsync(
            "TestProject", includePrivate: false, includeInternal: false, symbolKindFilter: "Banana"));
        json["success"]!.Value<bool>().Should().BeFalse(
            "an unrecognized symbolKindFilter must be rejected, not silently report zero unused");
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task UnknownFilter_RemoveUnusedCode_ReturnsInvalidParameter()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Kinds.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.RemoveUnusedCodeAsync(
            projectName: "TestProject", symbolKindFilter: "Banana"));
        json["success"]!.Value<bool>().Should().BeFalse(
            "remove_unused_code shares the filter and must reject an unknown value too");
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task DelegateFilter_ReportsOnlyDelegates()
    {
        const string code = @"
namespace Demo;
public delegate int DeadDelegate(int x);
public enum DeadEnum { A, B }
public class DeadClass { }
";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Kinds.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var entries = (JObject.FromObject(await service.FindUnusedCodeAsync(
            "TestProject", includePrivate: false, includeInternal: false, symbolKindFilter: "Delegate"))
            ["data"]!["unusedSymbols"] as JArray)!;
        entries.Select(e => e["kind"]!.Value<string>()).Distinct()
            .Should().Equal(new[] { "Delegate" }, "the Delegate filter must surface only delegates");
        entries.Select(e => e["fullyQualifiedName"]!.Value<string>())
            .Should().Contain("Demo.DeadDelegate");
    }

    [Fact]
    public async Task EnumFilter_ReportsOnlyEnums()
    {
        const string code = @"
namespace Demo;
public delegate int DeadDelegate(int x);
public enum DeadEnum { A, B }
public class DeadClass { }
";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Kinds.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var entries = (JObject.FromObject(await service.FindUnusedCodeAsync(
            "TestProject", includePrivate: false, includeInternal: false, symbolKindFilter: "Enum"))
            ["data"]!["unusedSymbols"] as JArray)!;
        entries.Select(e => e["kind"]!.Value<string>()).Distinct()
            .Should().Equal(new[] { "Enum" }, "the Enum filter must surface only enums");
        entries.Select(e => e["fullyQualifiedName"]!.Value<string>())
            .Should().Contain("Demo.DeadEnum");
    }

    [Fact]
    public async Task NullFilter_ReportsUnusedEnum()
    {
        // §5b refutation: MatchesKindFilter has no Enum case, but a null filter returns
        // true before the kind switch, so the type loop reports an unused enum.
        const string enumCode = @"
namespace Demo;
public enum DeadEnum { A, B }
";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(enumCode, "DeadEnum.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var entries = (JObject.FromObject(await service.FindUnusedCodeAsync(
            "TestProject", includePrivate: false, includeInternal: false, symbolKindFilter: null))
            ["data"]!["unusedSymbols"] as JArray)!;
        entries.Should().Contain(
            e => e["fullyQualifiedName"]!.Value<string>() == "Demo.DeadEnum"
                && e["kind"]!.Value<string>() == "Enum",
            "a null filter reports unused enums — the missing Enum case in MatchesKindFilter does not hide them");
    }
}
