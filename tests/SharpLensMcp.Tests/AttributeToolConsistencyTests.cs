using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_attributes must match attribute names the same way find_attribute_usages does —
// exact (with trailing-Attribute handling), not substring — and render argument values
// via the C#-literal renderer, not the ambiguous boxed ToString.
public class AttributeToolConsistencyTests
{
    private const string Code = @"
using System;
namespace Demo
{
    public sealed class MarkerAttribute : Attribute { public string Note { get; set; } = """"; }
    public sealed class MarkerThingAttribute : Attribute { }

    [Marker(Note = ""hi"")]
    public class Decorated { }

    [MarkerThing]
    public class Other { }
}";

    private static async Task<JArray> SymbolsFor(string attributeName)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Attr.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (JObject.FromObject(await service.GetAttributesAsync(attributeName))["data"]!["symbols"] as JArray)!;
    }

    [Fact]
    public async Task ExactName_Matches_ExcludesSubstringSibling_AndRendersCSharpLiterals()
    {
        var names = (await SymbolsFor("Marker")).Select(s => s["symbolName"]!.Value<string>()).ToList();

        names.Should().Contain("Decorated", "[Marker] matches the query exactly");
        names.Should().NotContain("Other",
            "'Marker' must not substring-match 'MarkerThingAttribute' — exact match, like find_attribute_usages");

        var symbols = await SymbolsFor("Marker");
        var decorated = symbols.First(s => s["symbolName"]!.Value<string>() == "Decorated");
        decorated["attribute"]!["namedArguments"]!["Note"]!.Value<string>().Should().Be("\"hi\"",
            "named arguments render as C# literals (quoted strings), not boxed ToString");
    }
}
