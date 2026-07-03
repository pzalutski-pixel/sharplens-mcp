using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// add_null_checks must not treat a parameter as already-guarded when it only appears in
// a compound && condition — `if (a == null && cond) throw` throws only when the OTHER
// operand also holds, so `a` is not actually guarded and still needs a check.
public class AddNullChecksCompoundGuardTests
{
    [Fact]
    public async Task ParameterInCompoundAndCheck_IsNotTreatedAsGuarded()
    {
        const string code = @"
namespace Demo
{
    public class C
    {
        public void M(string a, string b)
        {
            if (a == null && b != null) throw new System.ArgumentException();
        }
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.AddNullChecksAsync(
            filePath: null, line: null, column: null, typeName: "C", memberName: "M"))["data"]!;

        (data["skippedAlreadyGuarded"] as JArray)!.Select(x => x.Value<string>())
            .Should().NotContain("a", "a compound && null-check does not actually guard the parameter");
        data["generatedCode"]!.Value<string>()!.Should().Contain("(a)",
            "a guard must still be generated for the parameter the && check does not cover");
    }
}
