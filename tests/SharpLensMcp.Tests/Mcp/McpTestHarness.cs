using System.Text.Json;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// Owns a single McpServer for all MCP-layer integration tests. Loads the
// SharpLensMcp solution once via tools/call roslyn:load_solution (so the
// load_solution path is itself exercised end-to-end). xUnit shares this
// across every test class in the McpServer collection so the ~30s solution
// load happens once per test run rather than per class.
public sealed class McpServerFixture : IAsyncLifetime
{
    public McpServer Server { get; private set; } = null!;
    public string SolutionDir { get; private set; } = null!;
    public string SolutionPath { get; private set; } = null!;
    public string RoslynServicePath { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var dir = currentDir;
        while (dir != null && !File.Exists(Path.Combine(dir, "SharpLensMcp.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        if (dir == null)
            throw new InvalidOperationException("Could not find SharpLensMcp.sln");

        SolutionDir = dir;
        SolutionPath = Path.Combine(dir, "SharpLensMcp.sln");
        RoslynServicePath = Path.Combine(dir, "src", "RoslynService.cs");

        Server = new McpServer();

        var harness = new McpHarnessOps(Server);
        var loadInner = await harness.CallToolAsync("roslyn:load_solution",
            new { solutionPath = SolutionPath });
        loadInner["success"]?.Value<bool>().Should().BeTrue(
            $"solution must load via MCP at fixture setup; got: {loadInner}");
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("McpServer collection")]
public class McpServerCollection : ICollectionFixture<McpServerFixture> { }

// Thin reusable wrapper around HandleRequestAsync that builds the JSON-RPC
// envelope, unwraps result.content[0].text, and parses the inner JSON. Used
// by both the fixture (during solution load) and the test base.
internal sealed class McpHarnessOps
{
    private readonly McpServer _server;
    private long _nextId;
    private static readonly JsonSerializerOptions SerializeOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public McpHarnessOps(McpServer server) { _server = server; _nextId = 1000; }

    public async Task<JObject> CallToolAsync(string toolName, object? arguments = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new { name = toolName, arguments = arguments ?? new { } }
        };
        var requestJson = JsonSerializer.Serialize(request);
        var response = await _server.HandleRequestAsync(requestJson);
        response.Should().NotBeNull($"tool '{toolName}' must return a response when called with a non-null id");

        var responseJson = JsonSerializer.Serialize(response, SerializeOpts);
        var envelope = JObject.Parse(responseJson);
        envelope["jsonrpc"]?.Value<string>().Should().Be("2.0",
            "JSON-RPC envelope must declare protocol version");
        envelope["id"]?.Value<long>().Should().Be(id,
            "id must round-trip");

        if (envelope["error"] != null)
        {
            throw new McpJsonRpcException(
                envelope["error"]!["code"]!.Value<int>(),
                envelope["error"]!["message"]?.Value<string>() ?? "",
                envelope);
        }

        var content = envelope["result"]?["content"] as JArray;
        content.Should().NotBeNull($"tool '{toolName}' must wrap response in result.content[]");
        content!.Count.Should().BeGreaterThan(0,
            $"tool '{toolName}' must produce at least one content entry");

        var first = content[0];
        first["type"]?.Value<string>().Should().Be("text",
            "MCP content type must be 'text' per the protocol");
        var innerText = first["text"]?.Value<string>();
        innerText.Should().NotBeNullOrEmpty(
            $"tool '{toolName}' content[0].text must be present");

        // The inner text MUST be a parseable JSON object with success + (data XOR error).
        JObject inner;
        try { inner = JObject.Parse(innerText!); }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"tool '{toolName}' returned non-JSON in content[0].text: {ex.Message}\nText: {innerText}");
        }

        inner["success"].Should().NotBeNull(
            $"tool '{toolName}' inner response must include a 'success' field");

        return inner;
    }
}

// Raised when the JSON-RPC layer (not the inner tool) returns an error
// response — e.g., -32602 Invalid params from the dispatcher.
internal sealed class McpJsonRpcException : Exception
{
    public int Code { get; }
    public JObject Envelope { get; }
    public McpJsonRpcException(int code, string message, JObject envelope)
        : base($"JSON-RPC error {code}: {message}")
    {
        Code = code;
        Envelope = envelope;
    }
}

// Base class for MCP-layer integration tests. Inherit + take the fixture
// via constructor injection (xUnit pattern).
[Collection("McpServer collection")]
public abstract class McpTestBase
{
    protected readonly McpServerFixture Fixture;
    private readonly McpHarnessOps _ops;

    protected McpTestBase(McpServerFixture fixture)
    {
        Fixture = fixture;
        _ops = new McpHarnessOps(fixture.Server);
    }

    // Sends a tools/call through the MCP layer. Returns the inner tool
    // response (after content[0].text unwrap) as a JObject. Throws
    // McpJsonRpcException if the JSON-RPC layer returns an error.
    protected Task<JObject> CallToolAsync(string toolName, object? arguments = null)
        => _ops.CallToolAsync(toolName, arguments);

    // Convenience: calls the tool and asserts the inner response is success.
    protected async Task<JToken> CallAndGetDataAsync(string toolName, object? arguments = null)
    {
        var inner = await CallToolAsync(toolName, arguments);
        inner["success"]?.Value<bool>().Should().BeTrue(
            $"tool '{toolName}' must succeed; got: {inner}");
        return inner["data"]!;
    }

    // Convenience: calls the tool and asserts the inner response is error
    // (i.e., success=false with a structured error). Returns the error
    // object for further assertions on code/message.
    protected async Task<JToken> CallAndGetErrorAsync(string toolName, object? arguments = null, string? codeContains = null)
    {
        var inner = await CallToolAsync(toolName, arguments);
        inner["success"]?.Value<bool>().Should().BeFalse(
            $"tool '{toolName}' must fail; got: {inner}");
        var error = inner["error"]!;
        if (codeContains != null)
        {
            error["code"]?.Value<string>().Should().Contain(codeContains);
        }
        return error;
    }

    // Calls the tool expecting the JSON-RPC dispatcher itself to reject the
    // request (e.g., missing required param → -32602). Asserts the JSON-RPC
    // error code matches and returns the error message for further checks.
    protected async Task<string> CallExpectingJsonRpcErrorAsync(
        string toolName, object? arguments, int expectedCode)
    {
        try
        {
            var inner = await CallToolAsync(toolName, arguments);
            throw new Exception(
                $"Expected JSON-RPC error {expectedCode} from '{toolName}' but tool returned: {inner}");
        }
        catch (McpJsonRpcException ex)
        {
            ex.Code.Should().Be(expectedCode,
                $"JSON-RPC error code must match; envelope: {ex.Envelope}");
            return ex.Envelope["error"]?["message"]?.Value<string>() ?? "";
        }
    }

    // Locate a symbol via the search_symbols MCP tool — handy primitive for
    // any per-position tool test that needs (filePath, line, column). Returns
    // an absolute path so callers can both pass it back into other tools and
    // File.ReadAllLines it directly (search_symbols returns paths via
    // FormatPath which defaults to relative).
    protected async Task<(string filePath, int line, int column)> LocateSymbolAsync(
        string query, string? kind = null, Func<JToken, bool>? predicate = null)
    {
        var data = await CallAndGetDataAsync("roslyn:search_symbols", new
        {
            query,
            kind,
            maxResults = 50
        });
        var results = data["results"] as JArray;
        results.Should().NotBeNullOrEmpty($"search_symbols('{query}') must return at least one result");
        var match = predicate != null ? results!.FirstOrDefault(predicate) : results![0];
        match.Should().NotBeNull($"no result matching predicate for '{query}'");
        var loc = match!["location"]!;
        var rawPath = loc["filePath"]!.Value<string>()!;
        var absolute = Path.IsPathRooted(rawPath)
            ? rawPath
            : Path.GetFullPath(Path.Combine(Fixture.SolutionDir, rawPath));
        return (
            absolute,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());
    }
}
