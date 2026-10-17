using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;
using static SharpLensMcp.Tests.MultiTargetNavigationSources;

namespace SharpLensMcp.Tests;

// The navigation tools on a multi-targeted project (MultiTargetSolutionFixture with
// MultiTargetNavigationSources): every symbol and location of Multi is bound once per
// target framework, and each tool reports it once, in the canonical order, with the
// targetFrameworks it occurred in; single-target results carry no targetFrameworks.
// Derived types and implementations are found across the framework boundary: Consumer
// references Multi(net10.0), so a Multi type resolved in its net8.0 compilation still
// has Consumer's implementers.
public class MultiTargetNavigationTests
{
    private static readonly string[] BothFrameworks = { MultiTargetSolutionFixture.Net10, MultiTargetSolutionFixture.Net8 };

    private static MultiTargetSolutionFixture Fixture() => new(Multi, Consumer);

    private static string Site(string path, (int line, int column) pos, string kind) =>
        $"{ToolResult.Reported(path)}:{pos.line}:{pos.column}:{kind}";

    // The reference sites of Multi.Greeter, in canonical (path, line, column) order:
    // Consumer/App.cs sorts before Multi/Greeter.cs.
    private static List<string> GreeterSites(MultiTargetSolutionFixture fixture)
    {
        var app = fixture.ConsumerFilePath(MultiTargetSources.ConsumerFileName);
        var greeter = fixture.MultiFilePath(MultiTargetSources.MultiFileName);
        return new List<string>
        {
            Site(app, Pos(Consumer, "using W = Multi.Greeter;", "Greeter;"), "read"),
            Site(app, Pos(Consumer, "public string Run(W w)", "W w"), "read"),
            Site(app, Pos(Consumer, "CreateGreeter()", "Greeter CreateGreeter"), "read"),
            Site(app, Pos(Consumer, "CreateGreeter()", "Greeter();"), "read"),
            Site(greeter, Pos(Multi, "public Type Kind()", "Greeter"), "typeof"),
            Site(greeter, Pos(Multi, "public static Greeter Create()", "Greeter Create"), "read"),
            Site(greeter, Pos(Multi, "public static Greeter Create()", "Greeter();"), "read"),
            Site(greeter, Pos(Multi, "public object Reflect()", "Greeter"), "typeof"),
            Site(greeter, Pos(Multi, "public static Greeter NewGreeter()", "Greeter NewGreeter"), "read"),
            Site(greeter, Pos(Multi, "public static Greeter NewGreeter()", "Greeter();"), "read"),
            Site(greeter, Pos(Multi, "Poke(this Greeter greeter)", "Greeter greeter"), "read"),
        };
    }

    private static (string path, int line, int column) GreeterDeclaration(MultiTargetSolutionFixture fixture)
    {
        var (line, column) = Pos(Multi, "public class Greeter", "Greeter");
        return (fixture.MultiFilePath(MultiTargetSources.MultiFileName), line, column);
    }

    [Fact]
    public async Task FindReferences_ReportsEachSiteOnce_InCanonicalOrder_WithTheFrameworksOfMultiSites()
    {
        using var fixture = Fixture();
        var service = ToolResult.Load(fixture.Workspace);
        var (path, line, column) = GreeterDeclaration(fixture);

        var response = await service.FindReferencesAsync(path, line, column);
        var data = ToolResult.Data(response);

        var references = (JArray)data["references"]!;
        references.Select(r => $"{r["filePath"]}:{r["line"]}:{r["column"]}:{r["kind"]}")
            .Should().Equal(GreeterSites(fixture));
        data["totalReferences"]!.Value<int>().Should().Be(11);
        foreach (var reference in references)
        {
            var inMulti = reference["filePath"]!.Value<string>()!.EndsWith("/Multi/Greeter.cs", StringComparison.Ordinal);
            if (inMulti) reference["targetFrameworks"]!.Values<string>().Should().Equal(BothFrameworks);
            else reference["targetFrameworks"].Should().BeNull("a Consumer site comes from a single-target project");
        }
        var meta = ToolResult.Meta(response);
        meta["TotalCount"]!.Value<int>().Should().Be(11);
        meta["ReturnedCount"]!.Value<int>().Should().Be(11);
        meta["Truncated"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task FindReferences_CappedPage_IsTheCanonicalPrefix()
    {
        using var fixture = Fixture();
        var service = ToolResult.Load(fixture.Workspace);
        var (path, line, column) = GreeterDeclaration(fixture);

        var response = await service.FindReferencesAsync(path, line, column, maxResults: 3);

        ((JArray)ToolResult.Data(response)["references"]!)
            .Select(r => $"{r["filePath"]}:{r["line"]}:{r["column"]}:{r["kind"]}")
            .Should().Equal(GreeterSites(fixture).Take(3));
        var meta = ToolResult.Meta(response);
        meta["TotalCount"]!.Value<int>().Should().Be(11);
        meta["ReturnedCount"]!.Value<int>().Should().Be(3);
        meta["Truncated"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeChangeImpact_CountsEachSiteOnce()
    {
        using var fixture = Fixture();
        var service = ToolResult.Load(fixture.Workspace);
        var (path, line, column) = GreeterDeclaration(fixture);

        var data = ToolResult.Data(await service.AnalyzeChangeImpactAsync(path, line, column, "rename"));

        data["totalReferences"]!.Value<int>().Should().Be(11);
        ((JArray)data["impactedLocations"]!).Select(r => $"{r["filePath"]}:{r["line"]}:{r["column"]}")
            .Should().Equal(GreeterSites(fixture).Select(s => s[..s.LastIndexOf(':')]));
    }

    [Fact]
    public async Task SearchSymbols_ReportsEachSymbolOnce_SortedByDisplayName()
    {
        using var fixture = Fixture();
        var service = ToolResult.Load(fixture.Workspace);

        var data = ToolResult.Data(await service.SearchSymbolsAsync("Greeter", kind: null, maxResults: 50));

        var results = (JArray)data["results"]!;
        results.Select(r => $"{r["fullyQualifiedName"]}|{r["kind"]}").Should().Equal(
            "Consumer.Factory.CreateGreeter()|Method",
            "Multi.Greeter|Class",
            "Multi.GreeterExt|Class",
            "Multi.GreeterFactory|Class",
            "Multi.GreeterFactory.NewGreeter()|Method");
        results[0]["targetFrameworks"].Should().BeNull();
        results[1]["targetFrameworks"]!.Values<string>().Should().Equal(BothFrameworks);
    }

    [Fact]
    public async Task SearchSymbols_Namespace_ReportedOnce()
    {
        using var fixture = Fixture();
        var service = ToolResult.Load(fixture.Workspace);

        var data = ToolResult.Data(await service.SearchSymbolsAsync("Multi", kind: "Namespace", maxResults: 50));

        ((JArray)data["results"]!).Select(r => r["fullyQualifiedName"]!.Value<string>()).Should().Equal("Multi");
    }

    [Fact]
    public async Task SemanticQuery_ReportsEachMethodOnce()
    {
        using var fixture = Fixture();
        var service = ToolResult.Load(fixture.Workspace);

        var data = ToolResult.Data(await service.SemanticQueryAsync(
            kinds: new List<string> { "Method" }, isAsync: null, namespaceFilter: "Multi", accessibility: null,
            isStatic: null, type: null, returnType: null, attributes: null, parameterIncludes: null,
            parameterExcludes: null, maxResults: 100));

        ((JArray)data["results"]!).Select(r => r["fullyQualifiedName"]!.Value<string>()).Should().Equal(
            "Multi.Base.Run()",
            "Multi.Greeter.Create()",
            "Multi.Greeter.Format(string)",
            "Multi.Greeter.Go()",
            "Multi.Greeter.Greet(string)",
            "Multi.Greeter.Kind()",
            "Multi.Greeter.Reflect()",
            "Multi.Greeter.Run()",
            "Multi.GreeterExt.Poke(Multi.Greeter)",
            "Multi.GreeterFactory.NewGreeter()",
            "Multi.IThing.Go()");
        data["countByKind"]!["Method"]!.Value<int>().Should().Be(11);
    }

    [Fact]
    public async Task FindUnusedCode_ReportsEachUnusedMethodOnce()
    {
        using var fixture = Fixture();
        var service = ToolResult.Load(fixture.Workspace);

        var data = ToolResult.Data(await service.FindUnusedCodeAsync(
            "Multi", includePrivate: true, includeInternal: true, symbolKindFilter: "Method"));

        ((JArray)data["unusedSymbols"]!).Select(s => s["fullyQualifiedName"]!.Value<string>()).Should().Equal(
            "Multi.Greeter.Kind()",
            "Multi.Greeter.Create()",
            "Multi.Greeter.Reflect()",
            "Multi.GreeterFactory.NewGreeter()",
            "Multi.GreeterExt.Poke(Multi.Greeter)");
    }

    [Fact]
    public async Task AttributeTools_ReportTheApplicationOnce()
    {
        using var fixture = Fixture();
        var service = ToolResult.Load(fixture.Workspace);

        var attributes = (JArray)ToolResult.Data(await service.GetAttributesAsync("Tag"))["symbols"]!;
        var usages = (JArray)ToolResult.Data(await service.FindAttributeUsagesAsync("Tag"))["usages"]!;

        attributes.Select(a => a["symbolName"]!.Value<string>()).Should().Equal("Greeter");
        attributes[0]["targetFrameworks"]!.Values<string>().Should().Equal(BothFrameworks);
        usages.Select(u => u["symbolName"]!.Value<string>()).Should().Equal("Greeter");
    }

    [Fact]
    public async Task FindReflectionUsage_ReportsTheCallOnce()
    {
        using var fixture = Fixture();
        var service = ToolResult.Load(fixture.Workspace);

        var usages = (JArray)ToolResult.Data(await service.FindReflectionUsageAsync())["usages"]!;

        var (line, column) = Pos(Multi, "public object Reflect()", "typeof(Greeter).GetMethod");
        usages.Select(u => $"{u["reflectionApi"]}|{u["location"]!["line"]}:{u["location"]!["column"]}")
            .Should().Equal($"Type.GetMethod|{line}:{column}");
    }

    [Fact]
    public async Task GetInstantiationOptions_ReportsEachExternalFactoryOnce()
    {
        using var fixture = Fixture();
        var service = ToolResult.Load(fixture.Workspace);

        var data = ToolResult.Data(await service.GetInstantiationOptionsAsync("Multi.Greeter"));

        ((JArray)data["factoryMethods"]!).Select(f => f["name"]!.Value<string>()).Should().Equal("Create");
        ((JArray)data["externalFactories"]!).Select(f => $"{f["containingType"]}.{f["name"]}").Should().Equal(
            "Consumer.Factory.CreateGreeter",
            "Multi.GreeterFactory.NewGreeter");
    }

    [Fact]
    public async Task GetExtensionMethods_ReportsTheExtensionOnce()
    {
        using var fixture = Fixture();
        var service = ToolResult.Load(fixture.Workspace);

        var groups = (JArray)ToolResult.Data(await service.GetExtensionMethodsAsync("Multi.Greeter"))["groups"]!;

        groups.SelectMany(g => (JArray)g["members"]!).Select(m => m["signature"]!.Value<string>())
            .Should().Equal("Multi.GreeterExt.Poke(Multi.Greeter)");
    }

    [Theory]
    [InlineData("public interface IThing", "IThing", "Consumer.App", "Multi.Greeter")]
    [InlineData("public abstract class Base", "Base", "Consumer.Concrete", "Multi.Greeter")]
    public async Task FindImplementations_FindsImplementersAcrossTheFrameworkBoundary_Once(
        string lineMarker, string token, string first, string second)
    {
        using var fixture = Fixture();
        var service = ToolResult.Load(fixture.Workspace);
        var (line, column) = Pos(Multi, lineMarker, token);

        var data = ToolResult.Data(await service.FindImplementationsAsync(
            fixture.MultiFilePath(MultiTargetSources.MultiFileName), line, column));

        var implementations = (JArray)data["implementations"]!;
        implementations.Select(i => i["name"]!.Value<string>()).Should().Equal(first, second);
        implementations[0]["targetFrameworks"].Should().BeNull();
        implementations[1]["targetFrameworks"]!.Values<string>().Should().Equal(BothFrameworks);
        data["totalImplementations"]!.Value<int>().Should().Be(2);
    }

    [Theory]
    [InlineData("Multi.IThing", "Consumer.App", "Multi.Greeter")]
    [InlineData("Multi.Base", "Consumer.Concrete", "Multi.Greeter")]
    public async Task GetDerivedTypes_FindsDerivedTypesAcrossTheFrameworkBoundary_Once(
        string baseTypeName, string first, string second)
    {
        using var fixture = Fixture();
        var service = ToolResult.Load(fixture.Workspace);

        var data = ToolResult.Data(await service.GetDerivedTypesAsync(baseTypeName));

        ((JArray)data["derivedTypes"]!).Select(d => d["fullName"]!.Value<string>()).Should().Equal(first, second);
        data["totalDerived"]!.Value<int>().Should().Be(2);
    }

    [Fact]
    public async Task GetTypeHierarchy_DerivedTypesAcrossTheFrameworkBoundary_Once()
    {
        using var fixture = Fixture();
        var service = ToolResult.Load(fixture.Workspace);
        var (line, column) = Pos(Multi, "public abstract class Base", "Base");

        var data = ToolResult.Data(await service.GetTypeHierarchyAsync(
            fixture.MultiFilePath(MultiTargetSources.MultiFileName), line, column));

        ((JArray)data["derivedTypes"]!).Select(d => d["name"]!.Value<string>())
            .Should().Equal("Consumer.Concrete", "Multi.Greeter");
        data["totalDerivedTypes"]!.Value<int>().Should().Be(2);
    }
}
