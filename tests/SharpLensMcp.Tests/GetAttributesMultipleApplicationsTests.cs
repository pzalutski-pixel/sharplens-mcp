using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_attributes counts per attribute APPLICATION, like find_attribute_usages: an
// AllowMultiple attribute applied twice on one declaration must make totalFound and the
// returned-symbol count agree, must respect maxResults without overshooting inside a
// single declaration, and must emit the positional constructor arguments.
public class GetAttributesMultipleApplicationsTests
{
    private const string Code = @"
using System;
namespace Demo
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class TagAttribute : Attribute
    {
        public TagAttribute(string name) { Name = name; }
        public string Name { get; }
    }

    [Tag(""first"")]
    [Tag(""second"")]
    public class Decorated { }
}";

    private static async Task<JObject> Run(int maxResults)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Tags.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return JObject.FromObject(await service.GetAttributesAsync("Tag", maxResults: maxResults));
    }

    [Fact]
    public async Task TwoApplications_CountConsistent_WithPositionalArguments()
    {
        var data = (await Run(maxResults: 100))["data"]!;

        data["totalFound"]!.Value<int>().Should().Be(2, "two [Tag] applications are two matches");

        var symbols = (data["symbols"] as JArray)!;
        symbols.Should().HaveCount(2, "returnedCount must equal totalFound, not the declaration count of 1");
        symbols.Select(s => s["symbolName"]!.Value<string>()).Should().AllBe("Decorated");

        var args = symbols.Select(s => (s["attribute"]!["arguments"] as JArray)!.Single().Value<string>()).ToList();
        args.Should().BeEquivalentTo(new[] { "\"first\"", "\"second\"" },
            "the positional constructor arguments render as C# literals, like find_attribute_usages");
    }

    [Fact]
    public async Task MaxResults_NotOvershotWithinOneDeclaration()
    {
        var data = (await Run(maxResults: 1))["data"]!;

        data["totalFound"]!.Value<int>().Should().Be(2, "both applications are counted even when results are capped");
        (data["symbols"] as JArray)!.Should().HaveCount(1,
            "the per-application cap must stop at maxResults, not add a whole declaration's attributes past it");
    }
}
