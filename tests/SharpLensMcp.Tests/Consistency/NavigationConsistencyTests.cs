using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;
using static SharpLensMcp.Tests.MultiTargetNavigationSources;

namespace SharpLensMcp.Tests.Consistency;

// Cross-tool invariants of the navigation tools, through the JSON-RPC dispatcher:
//   - a multi-targeted declaration or location is reported once by every tool
//     (MultiTargetSolutionFixture with MultiTargetNavigationSources);
//   - a symbol's reference set is the same in find_references and analyze_change_impact;
//   - overloadIndex N names the same member in get_method_overloads, get_method_source,
//     get_method_signature, get_documentation and get_super_method — partial parts in two
//     files, constructors, and (for the tools that list them) inherited overloads.
public class NavigationConsistencyTests : CrossToolConsistencyTestBase
{
    private static readonly string[] BothFrameworks = { MultiTargetSolutionFixture.Net10, MultiTargetSolutionFixture.Net8 };

    private MultiTargetSolutionFixture LoadMulti()
    {
        var fixture = new MultiTargetSolutionFixture(Multi, Consumer);
        Load(fixture.Workspace);
        return fixture;
    }

    [Fact]
    public async Task FindReferences_And_AnalyzeChangeImpact_AgreeOnTheReferenceSet_EachSiteOnce()
    {
        using var fixture = LoadMulti();
        var (line, column) = Pos(Multi, "public class Greeter", "Greeter");
        var filePath = fixture.MultiFilePath(MultiTargetSources.MultiFileName);

        var references = await CallDataAsync("find_references", new { filePath, line, column });
        var impact = await CallDataAsync("analyze_change_impact", new { filePath, line, column, changeType = "rename" });

        var referenceSites = Locations((JArray)references["references"]!, r => r);
        var impactSites = Locations((JArray)impact["impactedLocations"]!, r => r);
        referenceSites.Should().HaveCount(11).And.OnlyHaveUniqueItems();
        impactSites.Should().Equal(referenceSites, "both tools report the same reference set");
        references["totalReferences"]!.Value<int>().Should().Be(11);
        impact["totalReferences"]!.Value<int>().Should().Be(11);
    }

    [Fact]
    public async Task SearchSymbols_ReportsTheMultiTargetedTypeOnce()
    {
        using var fixture = LoadMulti();
        var data = await CallDataAsync("search_symbols", new { query = "Greeter", kind = "Class" });

        var results = (JArray)data["results"]!;
        results.Select(r => r["fullyQualifiedName"]!.Value<string>())
            .Should().Equal("Multi.Greeter", "Multi.GreeterExt", "Multi.GreeterFactory");
        results[0]["targetFrameworks"]!.Values<string>().Should().Equal(BothFrameworks);
    }

    [Fact]
    public async Task SemanticQuery_ReportsTheMultiTargetedTypesOnce()
    {
        using var fixture = LoadMulti();
        var data = await CallDataAsync("semantic_query", new { kinds = new[] { "Class" }, namespaceFilter = "Multi" });

        ((JArray)data["results"]!).Select(r => r["fullyQualifiedName"]!.Value<string>()).Should().Equal(
            "Multi.Base", "Multi.Greeter", "Multi.GreeterExt", "Multi.GreeterFactory", "Multi.TagAttribute");
    }

    [Fact]
    public async Task FindUnusedCode_ReportsEachUnusedMemberOnce()
    {
        using var fixture = LoadMulti();
        var data = await CallDataAsync("find_unused_code", new { projectName = "Multi", symbolKindFilter = "Method" });

        Names((JArray)data["unusedSymbols"]!, s => s["fullyQualifiedName"]!.Value<string>()!).Should().Equal(
            "Multi.Greeter.Create()",
            "Multi.Greeter.Kind()",
            "Multi.Greeter.Reflect()",
            "Multi.GreeterExt.Poke(Multi.Greeter)",
            "Multi.GreeterFactory.NewGreeter()");
        ((JArray)data["unusedSymbols"]!).Count.Should().Be(5);
    }

    [Fact]
    public async Task AttributeTools_ReportTheApplicationOnce()
    {
        using var fixture = LoadMulti();
        var attributes = await CallDataAsync("get_attributes", new { attributeName = "Tag" });
        var usages = await CallDataAsync("find_attribute_usages", new { attributeName = "Tag" });

        ((JArray)attributes["symbols"]!).Select(s => s["symbolName"]!.Value<string>()).Should().Equal("Greeter");
        ((JArray)usages["usages"]!).Select(s => s["symbolName"]!.Value<string>()).Should().Equal("Greeter");
    }

    [Fact]
    public async Task FindReflectionUsage_ReportsTheCallOnce()
    {
        using var fixture = LoadMulti();
        var data = await CallDataAsync("find_reflection_usage", new { });

        ((JArray)data["usages"]!).Select(u => u["reflectionApi"]!.Value<string>()).Should().Equal("Type.GetMethod");
    }

    [Fact]
    public async Task GetInstantiationOptions_And_GetExtensionMethods_ReportOnce()
    {
        using var fixture = LoadMulti();
        var options = await CallDataAsync("get_instantiation_options", new { typeName = "Multi.Greeter" });
        var extensions = await CallDataAsync("get_extension_methods", new { typeName = "Multi.Greeter" });

        ((JArray)options["externalFactories"]!).Select(f => f["name"]!.Value<string>())
            .Should().Equal("CreateGreeter", "NewGreeter");
        ((JArray)extensions["groups"]!).SelectMany(g => (JArray)g["members"]!).Select(m => m["name"]!.Value<string>())
            .Should().Equal("Poke");
    }

    [Fact]
    public async Task HierarchyTools_ReportEachDerivedTypeOnce_AcrossTheFrameworkBoundary()
    {
        using var fixture = LoadMulti();
        var (line, column) = Pos(Multi, "public interface IThing", "IThing");
        var filePath = fixture.MultiFilePath(MultiTargetSources.MultiFileName);

        var implementations = await CallDataAsync("find_implementations", new { filePath, line, column });
        var derived = await CallDataAsync("get_derived_types", new { baseTypeName = "Multi.IThing" });
        var hierarchy = await CallDataAsync("get_type_hierarchy", new { filePath, line, column });

        ((JArray)implementations["implementations"]!).Select(i => i["name"]!.Value<string>())
            .Should().Equal("Consumer.App", "Multi.Greeter");
        ((JArray)derived["derivedTypes"]!).Select(i => i["fullName"]!.Value<string>())
            .Should().Equal("Consumer.App", "Multi.Greeter");
        ((JArray)hierarchy["derivedTypes"]!).Select(i => i["name"]!.Value<string>())
            .Should().Equal("Consumer.App", "Multi.Greeter");
    }

    // Split.Over has one overload in each of two files; the canonical order puts the
    // alpha.cs part (string) before the Zeta.cs part (int), whatever order the documents
    // were added in. Widget has two constructors.
    private const string Zeta = """
        namespace Lib
        {
            public partial class Split
            {
                /// <summary>Int overload.</summary>
                public int Over(int x) => x;
            }

            public class Widget
            {
                /// <summary>Default.</summary>
                public Widget() { }
                /// <summary>Sized.</summary>
                public Widget(int size) { }
            }
        }
        """;

    private const string Alpha = """
        namespace Lib
        {
            public partial class Split
            {
                /// <summary>String overload.</summary>
                public string Over(string s) => s;
            }
        }
        """;

    [Theory]
    [InlineData("Over", "public int Over", "Over", 0, "Lib.Split.Over(string)", "String overload.")]
    [InlineData("Over", "public int Over", "Over", 1, "Lib.Split.Over(int)", "Int overload.")]
    [InlineData("Widget", "public Widget(int size)", "Widget", 0, "Lib.Widget.Widget()", "Default.")]
    [InlineData("Widget", "public Widget(int size)", "Widget", 1, "Lib.Widget.Widget(int)", "Sized.")]
    public async Task OverloadIndex_NamesTheSameMember_InEveryTool(
        string memberName, string lineMarker, string token, int overloadIndex, string signature, string summary)
    {
        var (workspace, documents) = TestHelpers.CreateWorkspaceWithMultipleDocuments((Zeta, "Zeta.cs"), (Alpha, "alpha.cs"));
        Load(workspace);
        var typeName = memberName == "Over" ? "Lib.Split" : "Lib.Widget";
        var (line, column) = Pos(Zeta, lineMarker, token);

        var overloads = await CallDataAsync("get_method_overloads", new { filePath = documents[0].FilePath, line, column });
        var source = await CallDataAsync("get_method_source", new { typeName, methodName = memberName, overloadIndex });
        var methodSignature = await CallDataAsync("get_method_signature", new { typeName, methodName = memberName, overloadIndex });
        var documentation = await CallDataAsync("get_documentation", new { typeName, memberName, overloadIndex });
        var super = await CallDataAsync("get_super_method", new { typeName, memberName, overloadIndex });

        var entry = ((JArray)overloads["overloads"]!).Single(o => o["overloadIndex"]!.Value<int>() == overloadIndex);
        entry["signature"]!.Value<string>().Should().Be(signature);
        source["signature"]!.Value<string>().Should().Be(signature);
        methodSignature["fullSignature"]!.Value<string>().Should().Be(signature);
        documentation["symbol"]!.Value<string>().Should().Be(signature);
        documentation["documentation"]!["summary"]!.Value<string>().Should().Be(summary);
        super["symbol"]!.Value<string>().Should().Be(signature);
    }

    private const string Inherited = """
        namespace Lib
        {
            public class Base2 { public int Calc(int x) => x; }
            public class Derived2 : Base2 { public string Calc(string s) => s; }
        }
        """;

    [Theory]
    [InlineData(0, "Lib.Derived2.Calc(string)", false)]
    [InlineData(1, "Lib.Base2.Calc(int)", true)]
    public async Task OverloadIndex_IncludesInheritedOverloadsAfterTheDeclaredOnes(int overloadIndex, string signature, bool isInherited)
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Inherited, "Inherited.cs");
        Load(workspace);
        var (line, column) = Pos(Inherited, "public class Derived2", "Calc");

        var overloads = await CallDataAsync("get_method_overloads", new { filePath = document.FilePath, line, column });
        var source = await CallDataAsync("get_method_source", new { typeName = "Lib.Derived2", methodName = "Calc", overloadIndex });
        var methodSignature = await CallDataAsync("get_method_signature", new { typeName = "Lib.Derived2", methodName = "Calc", overloadIndex });

        var entry = ((JArray)overloads["overloads"]!).Single(o => o["overloadIndex"]!.Value<int>() == overloadIndex);
        entry["signature"]!.Value<string>().Should().Be(signature);
        entry["isInherited"]!.Value<bool>().Should().Be(isInherited);
        source["signature"]!.Value<string>().Should().Be(signature);
        source["isInherited"]!.Value<bool>().Should().Be(isInherited);
        methodSignature["fullSignature"]!.Value<string>().Should().Be(signature);
        methodSignature["isInherited"]!.Value<bool>().Should().Be(isInherited);
    }
}
