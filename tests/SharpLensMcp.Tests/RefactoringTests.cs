using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

/// <summary>
/// Tests for refactoring tools: rename_symbol, extract_method, organize_usings, etc.
/// </summary>
public class RefactoringTests : RoslynServiceTestBase
{
    [Fact]
    public async Task RenameSymbol_WithPreview_ShowsChanges()
    {
        var searchResult = await Service.SearchSymbolsAsync("_workspace", kind: "Field", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty("_workspace is a field in RoslynService");

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.RenameSymbolAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            newName: "_roslynWorkspace",
            preview: true);

        AssertSuccess(result);
        var data = GetData(result);
        data["newName"]?.Value<string>().Should().Be("_roslynWorkspace");
        data["preview"]?.Value<bool>().Should().BeTrue();
        var changes = data["changes"] as JArray;
        changes.Should().NotBeNullOrEmpty("renaming _workspace touches at least RoslynService.cs");
    }

    [Fact]
    public async Task RenameSymbol_WithInvalidName_ReturnsInvalidParameter()
    {
        var searchResult = await Service.SearchSymbolsAsync("_workspace", kind: "Field", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.RenameSymbolAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            newName: "123invalid",
            preview: true);

        AssertError(result, ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task ExtractInterface_GeneratesInterfaceCodeWithMembers()
    {
        var searchResult = await Service.SearchSymbolsAsync("FixtureRectangle", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var rect = symbols!.First(s => s["name"]?.Value<string>() == "FixtureRectangle");
        var loc = rect["location"]!;
        var result = await Service.ExtractInterfaceAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            interfaceName: "IFixtureRectangle",
            includeMemberNames: null);

        AssertSuccess(result);
        var data = GetData(result);
        var code = data["interfaceCode"]?.Value<string>();
        code.Should().Contain("interface IFixtureRectangle");
        code.Should().Contain("Width");
        code.Should().Contain("Height");
        code.Should().NotContain("ToString",
            "extract_interface must not include inherited Object members");
    }

    [Fact]
    public async Task GenerateConstructor_CreatesConstructorWithAllFields()
    {
        var searchResult = await Service.SearchSymbolsAsync("RefactoringTarget", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var target = symbols![0];
        var loc = target["location"]!;
        var result = await Service.GenerateConstructorAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());

        AssertSuccess(result);
        var data = GetData(result);
        var code = data["constructorCode"]?.Value<string>();
        code.Should().NotBeNullOrEmpty();
        code.Should().Contain("public RefactoringTarget");
    }

    // ChangeSignature tests live in ChangeSignatureTests.cs (per plan section E).

    [Fact]
    public async Task ExtractMethod_OnFixtureSumBody_GeneratesExtractedMethod()
    {
        var searchResult = await Service.SearchSymbolsAsync("Sum", kind: "Method", maxResults: 50);
        var symbols = GetData(searchResult)["results"] as JArray;
        var sum = symbols!.First(s =>
            s["containingType"]?.Value<string>()?.Contains("RefactoringTarget") == true);
        var loc = sum["location"]!;
        var file = loc["filePath"]!.Value<string>()!;
        var methodLine = loc["line"]!.Value<int>();

        // Extract lines containing the two `var` statements.
        var result = await Service.ExtractMethodAsync(
            file,
            startLine: methodLine + 2,
            endLine: methodLine + 3,
            methodName: "ComputePartial",
            preview: true);

        AssertSuccess(result);
        var data = GetData(result);
        data["methodName"]?.Value<string>().Should().Be("ComputePartial");
        // RefactoringTarget.Sum returns int and the selected slice produces a single
        // `total` local that flows out — so the extracted method returns int via `total`.
        data["returnType"]?.Value<string>().Should().Be("int");
        data["returnVariable"]?.Value<string>().Should().Be("total");
        data["signature"]?.Value<string>().Should()
            .Contain("private int ComputePartial",
                "the generated signature must declare the new private method named ComputePartial returning int");
        data["extractedCode"]?.Value<string>().Should()
            .Contain("private int ComputePartial",
                "the extracted method body must open with the generated signature");
        data["replacementCode"]?.Value<string>().Should()
            .Be("var total = ComputePartial(a, b, c);",
                "the call-site replacement must capture the returned `total` and forward Sum's parameters in order");
    }

    [Fact]
    public async Task GetMissingMembers_HandlesPositionWithNoIncompleteImpl()
    {
        // Position inside MatchesGlobPattern — a complete static helper in
        // RoslynService.cs that doesn't implement any interface.
        var lines = File.ReadAllLines(RoslynServicePath);
        var matchLine = Array.FindIndex(lines, l => l.Contains("MatchesGlobPattern(string input"));
        matchLine.Should().BeGreaterThan(0, "MatchesGlobPattern must exist in RoslynService.cs");

        var result = await Service.GetMissingMembersAsync(RoslynServicePath, matchLine + 5, 10);

        AssertSuccess(result);
        var data = GetData(result);
        var missing = data["missingMembers"] as JArray;
        // The tool may omit the field or emit an empty array; both signal "nothing missing".
        // Split the cases so a future schema change that drops one branch doesn't silently pass.
        if (missing == null)
        {
            data["missingMembers"].Should().BeNull(
                "the absence of the field is acceptable when the implementation has nothing to report");
        }
        else
        {
            missing.Count.Should().Be(0,
                "a complete type must report zero missing members rather than a populated array");
        }
    }

    [Fact]
    public async Task GetOutgoingCalls_OnHealthCheck_IncludesGetProjectCompilationAsync()
    {
        var searchResult = await Service.SearchSymbolsAsync("GetHealthCheckAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var file = loc["filePath"]!.Value<string>()!;
        var methodLine = loc["line"]!.Value<int>();

        // Position inside the method body by locating a known invocation line — the
        // GetProjectCompilationAsync call inside the foreach. Read from the absolute
        // RoslynServicePath because `file` is the solution-relative path returned by
        // the tool and won't resolve against the test bin directory.
        var lines = File.ReadAllLines(RoslynServicePath);
        var callLine = Array.FindIndex(lines, methodLine, l => l.Contains("GetProjectCompilationAsync(project)"));
        callLine.Should().BeGreaterThan(methodLine,
            "the GetProjectCompilationAsync invocation must live inside GetHealthCheckAsync's body");

        var result = await Service.GetOutgoingCallsAsync(file, callLine + 1, 20);

        AssertSuccess(result);
        var data = GetData(result);
        var calls = data["calls"] as JArray;
        calls.Should().NotBeNullOrEmpty();
        calls!.Any(c => c["shortName"]?.Value<string>()?.EndsWith(".GetProjectCompilationAsync") == true)
            .Should().BeTrue("outgoing calls must include the GetProjectCompilationAsync invocation");
    }

    [Fact]
    public async Task OrganizeUsings_OnRoslynServiceCs_ReturnsTextWithUsings()
    {
        var result = await Service.OrganizeUsingsAsync(RoslynServicePath);

        AssertSuccess(result);
        var data = GetData(result);
        var organized = data["organizedText"]!.Value<string>()!;
        organized.Should().Contain("using System");
        organized.Should().Contain("using Microsoft.CodeAnalysis");
    }

    [Fact]
    public async Task OrganizeUsingsBatch_ProcessesProject_ReportsFilesProcessed()
    {
        var result = await Service.OrganizeUsingsBatchAsync(
            projectName: "SharpLensMcp",
            filePattern: null,
            preview: true);

        AssertSuccess(result);
        var data = GetData(result);
        var fileCount = data["fileCount"]?.Value<int>() ?? (data["files"] as JArray)?.Count;
        fileCount.Should().NotBeNull();
        fileCount!.Value.Should().BeGreaterThan(0,
            "the project has multiple .cs files with using directives");
    }

    [Fact]
    public async Task FormatDocumentBatch_FormatsProject_ReportsFilesProcessed()
    {
        var result = await Service.FormatDocumentBatchAsync(
            projectName: "SharpLensMcp",
            preview: true);

        AssertSuccess(result);
        var data = GetData(result);
        var fileCount = data["fileCount"]?.Value<int>() ?? (data["files"] as JArray)?.Count;
        fileCount.Should().NotBeNull();
        fileCount!.Value.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetMethodOverloads_OnCreateErrorResponse_AllOverloadsShareName()
    {
        var searchResult = await Service.SearchSymbolsAsync("CreateErrorResponse", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.GetMethodOverloadsAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());

        AssertSuccess(result);
        var data = GetData(result);
        data["methodName"]?.Value<string>().Should().Be("CreateErrorResponse");
        var overloads = data["overloads"] as JArray;
        overloads.Should().NotBeNullOrEmpty();
        foreach (var o in overloads!)
        {
            o["signature"]?.Value<string>().Should().Contain("CreateErrorResponse");
        }
    }

    [Fact]
    public async Task GetContainingMember_AtMethodBody_ReturnsExpectedMember()
    {
        // Dynamically locate the body of MatchesGlobPattern — its `var regexPattern = "^"`
        // assignment line — so the test survives renames or insertions above it.
        var lines = File.ReadAllLines(RoslynServicePath);
        var bodyLine = Array.FindIndex(lines, l => l.Contains("var regexPattern = \"^\""));
        bodyLine.Should().BeGreaterThan(0, "the regexPattern assignment inside MatchesGlobPattern must exist");

        var result = await Service.GetContainingMemberAsync(RoslynServicePath, bodyLine + 1, 10);

        AssertSuccess(result);
        var data = GetData(result);
        data["memberName"]?.Value<string>().Should().Be("MatchesGlobPattern");
        data["memberKind"]?.Value<string>().Should().Be("Method");
    }

    [Fact]
    public async Task GetAttributes_FindsFactAttributeOnXunitTests()
    {
        // Xunit's [Fact] attribute is heavily used in the test project.
        var result = await Service.GetAttributesAsync("Fact");

        AssertSuccess(result);
        var data = GetData(result);
        var symbols = data["symbols"] as JArray;
        symbols.Should().NotBeNullOrEmpty(
            "the test project has many [Fact]-decorated methods");
        symbols!.All(s => s["attribute"]?["name"]?.Value<string>() == "FactAttribute")
            .Should().BeTrue("every returned symbol must be decorated with [Fact]");
    }

    [Fact]
    public async Task FindImplementations_OnIShapeFixture_FindsAllImplementers()
    {
        var searchResult = await Service.SearchSymbolsAsync("IShapeFixture", kind: "Interface", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var ishape = symbols!.First(s => s["name"]?.Value<string>() == "IShapeFixture");
        var loc = ishape["location"]!;
        var result = await Service.FindImplementationsAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());

        AssertSuccess(result);
        var data = GetData(result);
        var impls = data["implementations"] as JArray;
        impls.Should().NotBeNullOrEmpty();

        var names = impls!.Select(i => i["name"]?.Value<string>()).ToList();
        names.Should().Contain(n => n!.EndsWith("FixtureCircle"));
        names.Should().Contain(n => n!.EndsWith("FixtureRectangle"));
        names.Should().Contain(n => n!.EndsWith("FixtureSquare"),
            "find_implementations must include transitive implementers (FixtureSquare : FixtureRectangle : IShapeFixture)");
    }

    [Fact]
    public async Task GetTypeHierarchy_OnFixtureSquare_ListsRectangleAncestor()
    {
        var searchResult = await Service.SearchSymbolsAsync("FixtureSquare", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var sq = symbols!.First(s => s["name"]?.Value<string>() == "FixtureSquare");
        var loc = sq["location"]!;
        var result = await Service.GetTypeHierarchyAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());

        AssertSuccess(result);
        var data = GetData(result);
        var baseTypes = data["baseTypes"] as JArray;
        baseTypes.Should().NotBeNullOrEmpty();
        baseTypes!.Any(b => b["name"]?.Value<string>()?.EndsWith("FixtureRectangle") == true)
            .Should().BeTrue("FixtureSquare's hierarchy must include FixtureRectangle");
    }
}
