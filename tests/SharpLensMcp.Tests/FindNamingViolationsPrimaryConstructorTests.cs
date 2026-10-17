using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_naming_violations treats primary-constructor parameters as the compiler's IDE1006
// does: a record's are not checked (they are the names of its positional properties), a
// C# 12 class's are parameters like any other and follow the camelCase parameter rule —
// as does a regular constructor's.
public class FindNamingViolationsPrimaryConstructorTests
{
    private const string Code = @"
namespace Demo
{
    public record Order(int TotalAmount);
    public class Box(int InitialValue) { public int V => InitialValue; }

    public class Regular
    {
        public Regular(int BadParam) { _ = BadParam; }
    }
}";

    [Fact]
    public async Task RecordParameters_AreNotFlagged_ClassPrimaryAndRegularCtorParamsAre()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Primary.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.FindNamingViolationsAsync("TestProject"))["data"]!;
        var symbols = (data["violations"] as JArray)!
            .Select(v => v["symbol"]!.Value<string>()!).ToList();

        symbols.Should().Equal(
            "Demo.Box.Box(int) parameter 'InitialValue'",
            "Demo.Regular.Regular(int) parameter 'BadParam'");
    }
}
