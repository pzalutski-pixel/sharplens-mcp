using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// A per-call timeout that fires during the code-fix / refactoring provider loops or the
// health-check diagnostics sweep must surface as cancellation (→ dispatcher TIMEOUT), not
// be swallowed by the bare catch that skips a throwing provider / ignores a failing sweep.
// Mirrors SyncDocumentsCancellationTests.
//
// HealthCheckWithDiagnostics is the genuine fail-pre-fix isolator: its swallowing try
// wraps the FIRST token-aware call (GetProjectCompilationAsync), so a pre-cancelled token
// lands inside that catch — verified to fail with the rethrow removed. The three
// provider-loop tools resolve a semantic model (GetSemanticModelAsync) BEFORE their
// provider loops; GetSemanticModelAsync observes a pre-cancelled token even from cache, so
// pre-warming does NOT move the throw into the loop — verified empirically (removing the
// loop rethrows leaves all three still passing). They are therefore regression guards for
// the rethrow, not isolators; the loop rethrows keep cancellation surfacing when the token
// fires DURING provider execution (the realistic timeout case), which a deterministic test
// cannot stage.
public class CodeActionCancellationTests
{
    private const string Code = @"using System.Text;
namespace Demo
{
    public class C
    {
        public int N() => 1;
    }
}";

    private static RoslynService Loaded(out string filePath)
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        filePath = document.FilePath!;
        return service;
    }

    private static CancellationToken Cancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts.Token;
    }

    [Fact]
    public async Task HealthCheckWithDiagnostics_PreCancelledToken_Propagates()
    {
        var service = Loaded(out _);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetHealthCheckAsync(includeDiagnostics: true, Cancelled()));
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_PreCancelledToken_Propagates()
    {
        var service = Loaded(out var filePath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetCodeActionsAtPositionAsync(filePath, line: 0, column: 0, cancellationToken: Cancelled()));
    }

    [Fact]
    public async Task ApplyCodeFix_PreCancelledToken_Propagates()
    {
        var service = Loaded(out var filePath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ApplyCodeFixAsync(filePath, "CS8019", line: 0, column: 0, cancellationToken: Cancelled()));
    }

    [Fact]
    public async Task FixAll_PreCancelledToken_Propagates()
    {
        var service = Loaded(out var filePath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.FixAllAsync("CS8019", scope: "file", filePath: filePath, cancellationToken: Cancelled()));
    }
}
