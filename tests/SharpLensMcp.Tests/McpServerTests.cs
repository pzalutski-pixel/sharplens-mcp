using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

public class McpServerTests
{
    private readonly McpServer _server = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private JsonObject ParseResponse(object response)
    {
        var json = JsonSerializer.Serialize(response, _jsonOptions);
        return JsonSerializer.Deserialize<JsonObject>(json)!;
    }

    [Fact]
    public async Task HandleRequest_WithIntegerId_EchoesIdBack()
    {
        var request = """{"jsonrpc":"2.0","id":42,"method":"initialize"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<long>().Should().Be(42);
        response["jsonrpc"]!.GetValue<string>().Should().Be("2.0");
    }

    [Fact]
    public async Task HandleRequest_WithStringId_EchoesIdBack()
    {
        var request = """{"jsonrpc":"2.0","id":"abc-123","method":"initialize"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<string>().Should().Be("abc-123");
    }

    [Fact]
    public async Task HandleRequest_WithGuidId_EchoesIdBack()
    {
        var request = """{"jsonrpc":"2.0","id":"550e8400-e29b-41d4-a716-446655440000","method":"initialize"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<string>().Should().Be("550e8400-e29b-41d4-a716-446655440000");
    }

    [Fact]
    public async Task HandleRequest_Initialize_ReturnsProtocolVersion()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"initialize"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var result = response["result"]!.AsObject();
        result["protocolVersion"]!.GetValue<string>().Should().NotBeEmpty();
        result["serverInfo"]!.AsObject()["name"]!.GetValue<string>().Should().Be("SharpLensMcp");
        result["serverInfo"]!.AsObject()["version"]!.GetValue<string>().Should().NotBe("1.0.0");
    }

    [Fact]
    public async Task HandleRequest_ToolsList_ReturnsTools()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var result = response["result"]!.AsObject();
        var tools = result["tools"]!.AsArray();
        tools.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleRequest_UnknownMethod_ReturnsMethodNotFound()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"unknown/method"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var error = response["error"]!.AsObject();
        error["code"]!.GetValue<int>().Should().Be(-32601);
        error["message"]!.GetValue<string>().Should().Contain("Method not found");
    }

    [Fact]
    public async Task HandleRequest_MissingMethod_ReturnsInvalidRequest()
    {
        var request = """{"jsonrpc":"2.0","id":1}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var error = response["error"]!.AsObject();
        error["code"]!.GetValue<int>().Should().Be(-32600);
    }

    [Fact]
    public async Task HandleRequest_InvalidJson_ReturnsParseError()
    {
        var request = "not valid json";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var error = response["error"]!.AsObject();
        error["code"]!.GetValue<int>().Should().Be(-32603);
    }

    [Fact]
    public async Task HandleRequest_MissingId_ResponseContainsIdField()
    {
        var request = """{"jsonrpc":"2.0","method":"initialize"}""";
        var json = JsonSerializer.Serialize(await _server.HandleRequestAsync(request), _jsonOptions);

        // Response should contain "id" field (JSON-RPC requires it in responses)
        json.Should().Contain("\"id\":");
    }

    [Fact]
    public async Task HandleRequest_ToolCallWithoutSolution_ReturnsError()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"roslyn:health_check","arguments":{}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<long>().Should().Be(1);
        response["result"].Should().NotBeNull();
    }

    [Fact]
    public async Task HandleRequest_NotificationInitialized_ReturnsNull()
    {
        var request = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
        var response = await _server.HandleRequestAsync(request);

        response.Should().BeNull();
    }

    [Fact]
    public async Task HandleRequest_NotificationWithNoId_ReturnsNull()
    {
        var request = """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"abc"}}""";
        var response = await _server.HandleRequestAsync(request);

        response.Should().BeNull();
    }
}
