using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_naming_violations skips explicit-interface implementations because they cannot be
// renamed independently. The skip covered methods and properties but not events. An
// explicit event implementation's reported name is the fully-qualified interface-member
// form (ConfigProbe.C.ConfigProbe.IHasChanged.Changed), which starts uppercase and so
// violates a camelCase event rule — it would be flagged without the IEventSymbol skip.
// This test fails (two event violations instead of one) if IEventSymbol is dropped.
public class NamingViolationsExplicitEventTests
{
    [Fact]
    public async Task ExplicitInterfaceEvent_IsNotFlagged_OnlyTheInterfaceDeclarationIs()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            @"namespace ConfigProbe
{
    public interface IHasChanged { event System.EventHandler Changed; }
    public class C : IHasChanged
    {
        event System.EventHandler IHasChanged.Changed { add { } remove { } }
    }
}",
            "C.cs");

        var configText = """
            is_global = true
            dotnet_naming_rule.events_camel.symbols = event_group
            dotnet_naming_rule.events_camel.style = camel
            dotnet_naming_rule.events_camel.severity = warning
            dotnet_naming_symbols.event_group.applicable_kinds = event
            dotnet_naming_style.camel.capitalization = camel_case
            """;
        var configPath = Path.Combine(Path.GetDirectoryName(document.FilePath!)!, ".globalconfig");
        var solution = workspace.CurrentSolution.AddAnalyzerConfigDocument(
            DocumentId.CreateNewId(document.Project.Id),
            ".globalconfig",
            SourceText.From(configText),
            filePath: configPath);
        workspace.TryApplyChanges(solution).Should().BeTrue();

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.FindNamingViolationsAsync())["data"]!;
        data["rulesSource"]!.Value<string>().Should().Be("editorconfig");

        var eventViolations = (data["violations"] as JArray)!
            .Where(v => v["kind"]!.Value<string>() == "event")
            .Select(v => v["symbol"]!.Value<string>()!)
            .ToList();

        // The explicit implementation's reported name is the fully-qualified
        // ConfigProbe.C.ConfigProbe.IHasChanged.Changed, which starts uppercase and so
        // violates the camelCase event rule — it WOULD be flagged without the IEventSymbol
        // skip (verified: removing the skip yields two event violations). With the skip,
        // only IHasChanged's own renameable declaration remains.
        eventViolations.Should().ContainSingle(
            "the unrenameable explicit-interface event implementation is skipped");
        eventViolations.Single().Should().NotContain("ConfigProbe.C.",
            "the surviving violation is the interface declaration, not the explicit implementation in class C");
    }
}
