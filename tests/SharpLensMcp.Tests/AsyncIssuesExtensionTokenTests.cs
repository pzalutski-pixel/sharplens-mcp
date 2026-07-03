using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_async_issues token-forwarding must see a cancellable VARIANT of an extension
// method even when it lives in a DIFFERENT static class (the common EF Core shape),
// not only same-named overloads on the called method's own type.
public class AsyncIssuesExtensionTokenTests
{
    [Fact]
    public async Task TokenTakingExtensionOverloadInAnotherClass_IsDetected()
    {
        const string code = @"
using System.Threading;
using System.Threading.Tasks;

namespace Demo
{
    public interface IThing { }

    public static class ThingExt
    {
        public static Task DoAsync(this IThing t) => Task.CompletedTask;
    }

    public static class ThingTokenExt
    {
        public static Task DoAsync(this IThing t, CancellationToken ct) => Task.CompletedTask;
    }

    public class Service
    {
        public async Task Run(IThing t, CancellationToken ct)
        {
            await t.DoAsync();
        }
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Async.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var issues = (JObject.FromObject(await service.FindAsyncIssuesAsync())["data"]!["issues"] as JArray)!;

        issues.Should().Contain(
            i => i["category"]!.Value<string>() == "tokenNotForwarded"
                && i["detail"]!.Value<string>()!.Contains("DoAsync"),
            "the cancellable DoAsync extension lives in another class, but the token is still not forwarded");
    }
}
