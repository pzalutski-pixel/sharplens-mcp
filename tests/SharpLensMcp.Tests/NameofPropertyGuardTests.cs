using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// A property referenced only inside nameof(...) is a compile-time reference, not a
// runtime read. get_outgoing_calls must not list it as an outgoing call, and
// get_exception_flow must not pull in its accessor's exceptions. Both walkers share
// IsInNameofOrAttributeArgument, but each is exercised here.
public class NameofPropertyGuardTests
{
    private const string Code = @"
namespace Demo;

public class Thrower
{
    public int Danger => throw new System.InvalidOperationException();
    public int Reads() => Danger + nameof(Danger).Length;
    public string Names() => nameof(Danger);
}
";

    private static RoslynService Loaded()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Thrower.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    [Fact]
    public async Task ExceptionFlow_NameofPropertyDoesNotContributeExceptions()
    {
        var service = Loaded();

        (await EscapingOf(service, "Names")).Should().NotContain("System.InvalidOperationException",
            "Danger appears only inside nameof in Names(), so its getter is never invoked");
        (await EscapingOf(service, "Reads")).Should().Contain("System.InvalidOperationException",
            "Reads() actually evaluates Danger, so the getter's exception escapes (control)");
    }

    [Fact]
    public async Task OutgoingCalls_NameofPropertyIsNotAnOutgoingReference()
    {
        var service = Loaded();

        (await OutgoingShortNamesOf(service, "Names")).Should().NotContain("Thrower.Danger",
            "Danger appears only inside nameof in Names(), so it is not an outgoing reference");
        (await OutgoingShortNamesOf(service, "Reads")).Should().Contain("Thrower.Danger",
            "Reads() actually reads Danger, so it is an outgoing reference (control)");
    }

    private static async Task<List<string>> EscapingOf(RoslynService service, string member)
    {
        var result = await service.GetExceptionFlowAsync(
            filePath: null, line: null, column: null, typeName: "Thrower", memberName: member);
        var data = JObject.FromObject(result)["data"]!;
        return (data["escaping"] as JArray)!.Select(e => e["exceptionType"]!.Value<string>()!).ToList();
    }

    private static async Task<List<string>> OutgoingShortNamesOf(RoslynService service, string member)
    {
        var search = await service.SearchSymbolsAsync(member, kind: "Method", maxResults: 10);
        var loc = JObject.FromObject(search)["data"]!["results"]!
            .First(r => r["name"]!.Value<string>() == member)["location"]!;
        var result = await service.GetOutgoingCallsAsync(
            loc["filePath"]!.Value<string>()!, loc["line"]!.Value<int>(), loc["column"]!.Value<int>());
        var data = JObject.FromObject(result)["data"]!;
        return (data["calls"] as JArray)!.Select(c => c["shortName"]!.Value<string>()!).ToList();
    }
}
