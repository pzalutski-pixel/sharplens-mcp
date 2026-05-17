using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Verifies get_external_type_info against the BCL and the project's source types.
// External-assembly resolution is what closes the AI-hallucination gap for NuGet/BCL APIs.
public class ExternalApiTests : RoslynServiceTestBase
{
    [Fact]
    public async Task GetExternalTypeInfo_OnSystemString_ReturnsExpectedMembers()
    {
        var result = await Service.GetExternalTypeInfoAsync("System.String");

        AssertSuccess(result);
        var data = GetData(result);
        data["typeName"]?.Value<string>().Should().Be("string");
        // String lives in System.Private.CoreLib on modern .NET; older runtimes named it differently.
        data["assembly"]?.Value<string>().Should().NotBeNullOrEmpty();
        data["typeKind"]?.Value<string>().Should().Be("Class");

        var members = data["members"] as JArray;
        members.Should().NotBeNullOrEmpty();
        var names = members!.Select(m => m["name"]?.Value<string>()).ToList();
        names.Should().Contain("Length");
        names.Should().Contain("Substring");
        names.Should().Contain("Trim");

        // At least one well-known member should carry a non-empty XML doc summary.
        members!.Any(m => !string.IsNullOrEmpty(m["xmlDoc"]?.Value<string>()))
            .Should().BeTrue("System.String members ship with XML docs in the .NET runtime reference assemblies");
    }

    [Fact]
    public async Task GetExternalTypeInfo_OnUnknownType_ReturnsTypeNotFound()
    {
        var result = await Service.GetExternalTypeInfoAsync("System.Garbage.DoesNotExist");
        AssertError(result, ErrorCodes.TypeNotFound);
    }

    [Fact]
    public async Task GetExternalTypeInfo_RespectsMaxMembers()
    {
        // List`1 has many public members; max:5 must cap returnedCount while totalCount stays honest.
        var result = await Service.GetExternalTypeInfoAsync(
            "System.Collections.Generic.List`1",
            maxMembers: 5);

        AssertSuccess(result);
        var data = GetData(result);
        var members = data["members"] as JArray;
        members.Should().NotBeNull();
        members!.Count.Should().BeLessOrEqualTo(5, "maxMembers cap must be enforced");

        var meta = JObject.FromObject(result)["meta"];
        meta?["totalCount"]?.Value<int>().Should().BeGreaterThan(5,
            "List<T>'s public surface is much larger than 5; totalCount must reflect the true count");
        meta?["returnedCount"]?.Value<int>().Should().BeLessOrEqualTo(5);
    }

    [Fact]
    public async Task GetExternalTypeInfo_OnSourceType_StillWorks()
    {
        // The tool name says "external" but resolution should also succeed for source types
        // in the loaded solution. Verifies no source/metadata split in the API.
        var result = await Service.GetExternalTypeInfoAsync("SharpLensMcp.RoslynService");

        AssertSuccess(result);
        var data = GetData(result);
        data["typeName"]?.Value<string>().Should().EndWith("RoslynService");
        var members = data["members"] as JArray;
        members.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetExternalTypeInfo_OnGenericInterface_ReturnsItsMembers()
    {
        var result = await Service.GetExternalTypeInfoAsync("System.Collections.Generic.IEnumerable`1");
        AssertSuccess(result);
        var data = GetData(result);
        data["typeKind"]?.Value<string>().Should().Be("Interface");
        var members = data["members"] as JArray;
        members.Should().NotBeNullOrEmpty();
        members!.Any(m => m["name"]?.Value<string>() == "GetEnumerator")
            .Should().BeTrue("IEnumerable<T> exposes GetEnumerator");
    }
}
