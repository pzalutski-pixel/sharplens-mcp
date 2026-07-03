using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// validate_code on a loaded-but-projectless solution must return a structured
// "no projects" error, not the misleading "Check syntax" error that
// StandaloneValidationReferences().First() produced when Projects was empty.
public class ValidateCodeNoProjectsTests
{
    [Fact]
    public async Task ProjectlessSolution_ReturnsStructuredNoProjectsError()
    {
        var workspace = new AdhocWorkspace();
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.ValidateCodeAsync(
            "public class T { }", standalone: true));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AnalysisFailed);
        json["error"]!["Message"]!.Value<string>().Should().Contain("no projects",
            "the error must name the real cause, not blame the snippet's syntax");
    }

    [Fact]
    public async Task CancelledToken_PropagatesCancellation_NotAnalysisFailed()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode("namespace N { public class C { } }", "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => service.ValidateCodeAsync(
            "public class T { }", standalone: true, cancellationToken: cts.Token);

        // A per-call timeout (cancelled token) must surface as cancellation for the
        // dispatcher's TIMEOUT, not be swallowed into a misleading AnalysisFailed.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
