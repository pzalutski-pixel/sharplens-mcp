using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// load_solution is transactional: a load that fails, or is cancelled, leaves the previously
// loaded solution fully usable on its live workspace and disposes the new workspace it
// created. Real temp solutions through MSBuildWorkspace.
public class SolutionLoadLifecycleTests : IDisposable
{
    private readonly TempSolutionDirectory _dir = new("loadlifecycle");

    public void Dispose() => _dir.Dispose();

    private string WriteGoodSolution()
    {
        _dir.Write("Lib/Lib.csproj", TempSolutionDirectory.MinimalCsproj);
        _dir.Write("Lib/Widget.cs", "namespace Lib;\npublic class Widget { }\n");
        return _dir.WriteSolution("Good.sln", ("Lib", "Lib\\Lib.csproj"));
    }

    // The previous solution is still the loaded one AND its workspace is live: a disposed
    // workspace is cleared to an empty solution, so a live one still holds the project.
    private static void AssertStillLoaded(RoslynService service, string expectedSolutionPath)
    {
        service.GetSolutionForTesting()!.FilePath.Should().Be(expectedSolutionPath);
        service.GetWorkspaceForTesting()!.CurrentSolution.ProjectIds.Should().HaveCount(1,
            "the previous solution's workspace must not have been disposed");
    }

    private static async Task<JToken> LastLoadFailureAsync(RoslynService service)
    {
        var health = JObject.FromObject(await service.GetHealthCheckAsync());
        return health["data"]!["lastLoadFailure"]!;
    }

    [Theory]
    [InlineData("notes.ini")]
    [InlineData("Lib/Lib.csproj")]
    public async Task NonSolutionFile_ReturnsInvalidParameter_AndKeepsThePreviousSolution(string relativePath)
    {
        var good = WriteGoodSolution();
        var target = File.Exists(_dir.PathOf(relativePath)) ? _dir.PathOf(relativePath) : _dir.Write(relativePath, "[section]\nkey=value\n");
        var service = new RoslynService();
        JObject.FromObject(await service.LoadSolutionAsync(good))["success"]!.Value<bool>().Should().BeTrue();

        var result = JObject.FromObject(await service.LoadSolutionAsync(target));

        result["success"]!.Value<bool>().Should().BeFalse();
        result["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        result["error"]!["Message"]!.Value<string>().Should().Be(
            $"Not a solution file: {target}. load_solution takes a .sln, .slnx or .slnf file.");
        result["error"]!["Hint"]!.Value<string>().Should().Contain("dotnet sln");
        AssertStillLoaded(service, good);

        var failure = await LastLoadFailureAsync(service);
        failure["solutionPath"]!.Value<string>().Should().Be(target);
        failure["code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task UnreadableSolutionFile_ReturnsAnalysisFailed_AndKeepsThePreviousSolution()
    {
        var good = WriteGoodSolution();
        var garbage = _dir.Write("Garbage.sln", "this is not a solution file\n");
        var service = new RoslynService();
        await service.LoadSolutionAsync(good);

        var result = JObject.FromObject(await service.LoadSolutionAsync(garbage));

        result["success"]!.Value<bool>().Should().BeFalse();
        result["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AnalysisFailed);
        result["error"]!["Message"]!.Value<string>().Should().StartWith($"Could not load solution '{garbage}': ");
        result["error"]!["Hint"]!.Value<string>().Should().Contain("previously loaded solution");
        AssertStillLoaded(service, good);
    }

    [Fact]
    public async Task CancelledLoad_DisposesItsWorkspace_AndKeepsThePreviousSolution()
    {
        var good = WriteGoodSolution();
        _dir.Write("Other/Other.csproj", TempSolutionDirectory.MinimalCsproj);
        var other = _dir.WriteSolution("Other.sln", ("Other", "Other\\Other.csproj"));
        var service = new RoslynService();
        await service.LoadSolutionAsync(good);

        // Cancel deterministically at the point the new workspace holds the opened solution,
        // so its emptiness afterwards proves it was disposed (Dispose clears the solution).
        using var cts = new CancellationTokenSource();
        Workspace? opened = null;
        service.WorkspaceOpenedForTesting = workspace =>
        {
            opened = workspace;
            workspace.CurrentSolution.ProjectIds.Should().HaveCount(1, "the hook runs after the solution opened");
            cts.Cancel();
        };

        var act = () => service.LoadSolutionAsync(other, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        opened.Should().NotBeNull();
        opened!.CurrentSolution.ProjectIds.Should().BeEmpty("the cancelled load's workspace must be disposed, not leaked");
        AssertStillLoaded(service, good);
        var failure = await LastLoadFailureAsync(service);
        failure["solutionPath"]!.Value<string>().Should().Be(other);
        failure["code"]!.Value<string>().Should().Be(ErrorCodes.Timeout);
    }

    [Fact]
    public async Task SuccessfulLoad_DisposesThePreviousWorkspace_AndClearsTheLastFailure()
    {
        var good = WriteGoodSolution();
        _dir.Write("Other/Other.csproj", TempSolutionDirectory.MinimalCsproj);
        var other = _dir.WriteSolution("Other.sln", ("Other", "Other\\Other.csproj"));
        var service = new RoslynService();
        await service.LoadSolutionAsync(good);
        var first = service.GetWorkspaceForTesting()!;
        await service.LoadSolutionAsync(_dir.PathOf("missing.sln"));

        JObject.FromObject(await service.LoadSolutionAsync(other))["success"]!.Value<bool>().Should().BeTrue();

        first.CurrentSolution.ProjectIds.Should().BeEmpty("the replaced workspace is disposed once the new one is in place");
        service.GetSolutionForTesting()!.FilePath.Should().Be(other);
        (await LastLoadFailureAsync(service)).Type.Should().Be(JTokenType.Null,
            "a successful load clears the previous failure");
    }
}
