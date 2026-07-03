using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_naming_violations must not flag primary-constructor parameters (records and
// C# 12 classes/structs) as camelCase violations — they follow PascalCase because
// they seed or become properties. A regular constructor parameter still must.
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
    public async Task PrimaryConstructorParameters_AreNotFlagged_RegularCtorParamIs()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Primary.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.FindNamingViolationsAsync("TestProject"))["data"]!;
        var symbols = (data["violations"] as JArray)!
            .Select(v => v["symbol"]!.Value<string>()!).ToList();

        symbols.Should().Contain(s => s.Contains("BadParam"),
            "a regular constructor parameter in PascalCase still violates the camelCase rule");
        symbols.Should().NotContain(s => s.Contains("TotalAmount"),
            "a record's primary-constructor parameter follows PascalCase and is not a violation");
        symbols.Should().NotContain(s => s.Contains("InitialValue"),
            "a C# 12 class's primary-constructor parameter follows PascalCase and is not a violation");
    }
}
