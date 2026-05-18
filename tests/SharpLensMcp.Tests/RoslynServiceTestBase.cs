using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

/// <summary>
/// Base class for RoslynService integration tests.
/// Loads the actual SharpLensMcp solution for testing.
/// </summary>
public abstract class RoslynServiceTestBase : IAsyncLifetime
{
    protected RoslynService Service { get; private set; } = null!;
    protected string SolutionPath { get; private set; } = null!;
    protected string RoslynServicePath { get; private set; } = null!;
    protected string McpServerPath { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Find the solution path (go up from test bin directory)
        var currentDir = Directory.GetCurrentDirectory();
        var solutionDir = currentDir;

        while (solutionDir != null && !File.Exists(Path.Combine(solutionDir, "SharpLensMcp.sln")))
        {
            solutionDir = Directory.GetParent(solutionDir)?.FullName;
        }

        if (solutionDir == null)
            throw new InvalidOperationException("Could not find SharpLensMcp.sln");

        SolutionPath = Path.Combine(solutionDir, "SharpLensMcp.sln");
        RoslynServicePath = Path.Combine(solutionDir, "src", "RoslynService.cs");
        McpServerPath = Path.Combine(solutionDir, "src", "McpServer.cs");

        Service = new RoslynService();
        var result = await Service.LoadSolutionAsync(SolutionPath);

        // Verify solution loaded successfully. Lock success NotBeNull-first to defeat
        // the null-conditional silent-pass pattern — `json["success"]?.Value<bool>()
        // .Should().BeTrue(...)` would skip the assertion entirely if the success
        // field were missing (e.g., a regression that bypasses CreateSuccessResponse).
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull(
            "every response envelope must include a success field");
        json["success"]!.Value<bool>().Should().BeTrue("Solution should load successfully");
    }

    public Task DisposeAsync()
    {
        // RoslynService doesn't implement IDisposable
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asserts that a response indicates success. Locks the success field NotBeNull-first
    /// so a missing-envelope regression can't silently pass via the null-conditional
    /// short-circuit on `.Should()`.
    /// </summary>
    protected void AssertSuccess(object response)
    {
        var json = JObject.FromObject(response);
        json["success"].Should().NotBeNull("response envelope must include success field");
        json["success"]!.Value<bool>().Should().BeTrue(
            $"Expected success but got: {json["error"]}");
    }

    /// <summary>
    /// Asserts that a response indicates failure with specific error.
    /// Same NotBeNull-first hardening as AssertSuccess; additionally locks the
    /// error.code path so missing-error or missing-code regressions can't pass.
    /// </summary>
    protected void AssertError(object response, string? errorCodeContains = null)
    {
        var json = JObject.FromObject(response);
        json["success"].Should().NotBeNull("response envelope must include success field");
        json["success"]!.Value<bool>().Should().BeFalse();

        if (errorCodeContains != null)
        {
            json["error"].Should().NotBeNull("error responses must carry an error object");
            // RoslynError uses C# PascalCase properties (Code, Message, Hint, Context),
            // and Newtonsoft.Json's JObject.FromObject preserves the casing as-is. The
            // prior `["error"]?["code"]?.Value<string>().Should().Contain(...)` chain
            // was silently passing because (a) the field is "Code" not "code", and
            // (b) the null-conditional short-circuited the entire assertion.
            json["error"]!["Code"].Should().NotBeNull("error.Code field is required");
            json["error"]!["Code"]!.Value<string>().Should().Contain(errorCodeContains);
        }
    }

    /// <summary>
    /// Gets the data portion of a successful response.
    /// </summary>
    protected JToken GetData(object response)
    {
        var json = JObject.FromObject(response);
        AssertSuccess(response);
        return json["data"]!;
    }
}
