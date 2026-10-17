using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;

namespace SharpLensMcp.Tests;

// In-process reads of a RoslynService tool response (Newtonsoft JObject.FromObject):
// the envelope's success/data/meta/error keys are lowercase, the meta (ResponseMetadata)
// and error (RoslynError) fields PascalCase. Every accessor asserts the envelope shape
// first, so a missing field fails the test instead of skipping an assertion.
internal static class ToolResult
{
    // A service over an in-memory workspace.
    public static RoslynService Load(Workspace workspace)
    {
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    public static JToken Data(object response)
    {
        var json = JObject.FromObject(response);
        json["success"].Should().NotBeNull("every response envelope carries success");
        json["success"]!.Value<bool>().Should().BeTrue($"the call must succeed; got: {json}");
        return json["data"]!;
    }

    public static JToken Meta(object response)
    {
        var json = JObject.FromObject(response);
        json["success"]!.Value<bool>().Should().BeTrue($"the call must succeed; got: {json}");
        return json["meta"]!;
    }

    // The error object of a failed call, after asserting its code.
    public static JToken Error(object response, string expectedCode)
    {
        var json = JObject.FromObject(response);
        json["success"].Should().NotBeNull("every response envelope carries success");
        json["success"]!.Value<bool>().Should().BeFalse($"the call must fail; got: {json}");
        json["error"]!["Code"].Should().NotBeNull("an error carries its code");
        json["error"]!["Code"]!.Value<string>().Should().Be(expectedCode, json.ToString());
        return json["error"]!;
    }

    // A path as the tools report it for a document of an in-memory workspace (no
    // solution file, so FormatPath emits the absolute path with forward slashes).
    public static string Reported(string absolutePath) => absolutePath.Replace('\\', '/');
}
