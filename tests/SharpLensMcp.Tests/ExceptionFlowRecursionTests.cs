using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_exception_flow must mark a result truncated when a recursion cycle is hit: the
// cycle branch's deeper escapes are not followed, so the result is incomplete and must
// not be memoized as authoritative.
public class ExceptionFlowRecursionTests
{
    [Fact]
    public async Task RecursionCycle_MarksTruncated()
    {
        const string code = @"
namespace Demo
{
    public class C
    {
        public void A() { B(); }
        public void B() { A(); throw new System.InvalidOperationException(); }
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.GetExceptionFlowAsync(
            filePath: null, line: null, column: null, typeName: "C", memberName: "A"))["data"]!;

        data["truncatedByDepth"]!.Value<bool>().Should().BeTrue(
            "the A<->B recursion cycle leaves this branch's deeper escapes unfollowed, so the result is truncated");
    }
}
