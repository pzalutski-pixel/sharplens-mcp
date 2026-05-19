using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Direct-method coverage for refactoring/inspection tools that live across
// RoslynService.Refactoring.cs, .Inspection.cs, .Analysis.cs, .Navigation.cs,
// and .TypeDiscovery.cs. Every assertion locks a specific field the named impl
// method emits — `?.` short-circuit chains are forbidden here because they
// silently skip the assertion when the field is missing.
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
        // Locks every field the preview branch emits (Refactoring.cs:229-238).
        data["symbolName"]!.Value<string>().Should().Be("_workspace");
        data["symbolKind"]!.Value<string>().Should().Be("Field",
            "the located symbol is a Field per the search_symbols kind filter");
        data["newName"]!.Value<string>().Should().Be("_roslynWorkspace");
        data["verbosity"]!.Value<string>().Should().Be("summary",
            "default verbosity is 'summary' (Refactoring.cs:27)");
        data["preview"]!.Value<bool>().Should().BeTrue();
        data["applied"]!.Value<bool>().Should().BeFalse(
            "preview=true must report applied=false (Refactoring.cs:237)");

        var changes = data["changes"] as JArray;
        changes.Should().NotBeNullOrEmpty("renaming _workspace touches at least RoslynService.cs");
        // Summary-verbosity entries carry { filePath, changeCount }; lock both fields are present
        // and that changeCount > 0 (a zero-count entry would mean the rename didn't actually edit).
        foreach (var entry in changes!)
        {
            entry["filePath"].Should().NotBeNull("each change entry must carry filePath");
            entry["changeCount"]!.Value<int>().Should().BeGreaterThan(0,
                "every reported file must have at least one rename hit");
        }
    }

    [Fact]
    public async Task RenameSymbol_WithInvalidIdentifier_ReturnsInvalidParameter()
    {
        var searchResult = await Service.SearchSymbolsAsync("_workspace", kind: "Field", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        // Refactoring.cs:90-98 rejects names that fail SyntaxFacts.IsValidIdentifier
        // (e.g., names starting with a digit).
        var result = await Service.RenameSymbolAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            newName: "123invalid",
            preview: true);

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("not a valid C# identifier");
    }

    [Fact]
    public async Task RenameSymbol_WithEmptyNewName_ReturnsInvalidParameter()
    {
        var searchResult = await Service.SearchSymbolsAsync("_workspace", kind: "Field", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        // Refactoring.cs:81-88 rejects whitespace/empty newName before the identifier check.
        var result = await Service.RenameSymbolAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            newName: "",
            preview: true);

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("empty");
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
        // Locks every top-level field the success branch emits (Refactoring.cs:362-383).
        data["className"]!.Value<string>().Should().Be("FixtureRectangle");
        data["interfaceName"]!.Value<string>().Should().Be("IFixtureRectangle");
        data["suggestedFileName"]!.Value<string>().Should().Be("IFixtureRectangle.cs");

        // Members list — each entry has { name, kind, signature }. FixtureRectangle's
        // public surface is Width (property) and Height (property), so we expect both.
        var members = data["members"] as JArray;
        members.Should().NotBeNullOrEmpty();
        var memberNames = members!.Select(m => m["name"]!.Value<string>()).ToList();
        memberNames.Should().Contain("Width");
        memberNames.Should().Contain("Height");
        foreach (var m in members!)
        {
            m["kind"].Should().NotBeNull();
            m["signature"].Should().NotBeNull();
        }

        // The generated interface code must contain the interface header and member names,
        // and must NOT include inherited Object members like ToString.
        var code = data["interfaceCode"]!.Value<string>()!;
        code.Should().Contain("interface IFixtureRectangle");
        code.Should().Contain("Width");
        code.Should().Contain("Height");
        code.Should().NotContain("ToString",
            "extract_interface must not include inherited Object members");
    }

    [Fact]
    public async Task ExtractInterface_OnRefactoringTarget_IncludesMethodSignaturesNotFields()
    {
        // RefactoringTarget has a public field (BareCounter) plus three public methods
        // (Sum/GreetingFor/Compute). ExtractInterface filters to methods/properties/
        // events (Refactoring.cs:347-351) — fields are dropped. The generated interface
        // exercises the METHOD branch of GenerateInterfaceCode at line 649-655 (only
        // the property branch was previously tested via FixtureRectangle).
        var searchResult = await Service.SearchSymbolsAsync("RefactoringTarget", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        var target = symbols![0];
        var loc = target["location"]!;
        var result = await Service.ExtractInterfaceAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            interfaceName: "IRefactoringTarget",
            includeMemberNames: null);

        AssertSuccess(result);
        var data = GetData(result);
        var code = data["interfaceCode"]!.Value<string>()!;
        code.Should().Contain("Sum(", "Sum is a public method on the target");
        code.Should().Contain("GreetingFor(");
        code.Should().Contain("Compute(");
        code.Should().NotContain("BareCounter",
            "ExtractInterface excludes fields (Refactoring.cs:347-348 filters to methods/properties/events)");
    }

    [Fact]
    public async Task ExtractInterface_IncludeMemberNamesFilter_LimitsToNamedMembers()
    {
        // Refactoring.cs:354-357: when includeMemberNames is supplied, only members
        // whose Name matches survive. Pass just ["Width"] and assert Height/Area drop.
        var searchResult = await Service.SearchSymbolsAsync("FixtureRectangle", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        var rect = symbols!.First(s => s["name"]?.Value<string>() == "FixtureRectangle");
        var loc = rect["location"]!;
        var result = await Service.ExtractInterfaceAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            interfaceName: "IFixtureRectangle",
            includeMemberNames: new List<string> { "Width" });

        AssertSuccess(result);
        var data = GetData(result);
        var code = data["interfaceCode"]!.Value<string>()!;
        code.Should().Contain("Width");
        code.Should().NotContain("Height",
            "Height was not in includeMemberNames and must be filtered out");
        code.Should().NotContain("Area");
    }

    [Fact]
    public async Task RenameSymbol_VerbosityCompact_EmitsLocationsButNoText()
    {
        // Refactoring.cs:156-176: compact verbosity emits per-change line/column
        // entries but NOT old/new text snippets. Verifies the verbosity dispatch.
        var searchResult = await Service.SearchSymbolsAsync("_workspace", kind: "Field", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.RenameSymbolAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            newName: "_renamed",
            preview: true,
            verbosity: "compact");

        AssertSuccess(result);
        var data = GetData(result);
        data["verbosity"]!.Value<string>().Should().Be("compact");
        var changes = (data["changes"] as JArray)!;
        changes.Should().NotBeEmpty();
        // compact entries have filePath/changeCount/changes[]/truncated;
        // each inner change has line/column only (no text snippets).
        var first = changes[0]!;
        first["filePath"].Should().NotBeNull();
        var innerChanges = first["changes"] as JArray;
        innerChanges.Should().NotBeNull("compact verbosity emits inner changes array");
        if (innerChanges!.Count > 0)
        {
            innerChanges[0]!["line"].Should().NotBeNull();
            innerChanges[0]!["column"].Should().NotBeNull();
            innerChanges[0]!["oldText"].Should().BeNull("compact omits oldText");
            innerChanges[0]!["newText"].Should().BeNull("compact omits newText");
        }
    }

    [Fact]
    public async Task RenameSymbol_VerbosityFull_EmitsTextSnippets()
    {
        // Refactoring.cs:178-203: full verbosity adds oldText/newText to each
        // inner change entry. Locks the differentiator from compact.
        var searchResult = await Service.SearchSymbolsAsync("_workspace", kind: "Field", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.RenameSymbolAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            newName: "_renamed",
            preview: true,
            verbosity: "full");

        AssertSuccess(result);
        var data = GetData(result);
        data["verbosity"]!.Value<string>().Should().Be("full");
        var changes = (data["changes"] as JArray)!;
        var firstFile = changes[0]!;
        var innerChanges = (firstFile["changes"] as JArray)!;
        innerChanges.Should().NotBeEmpty();
        var firstChange = innerChanges[0]!;
        firstChange["oldText"]!.Value<string>().Should().Contain("_workspace");
        firstChange["newText"]!.Value<string>().Should().Contain("_renamed");
    }

    [Fact]
    public async Task ExtractInterface_OnNonTypePosition_ReturnsNotAType()
    {
        // Position the cursor on the MatchesGlobPattern method declaration — a method,
        // not a class. Refactoring.cs:335-343 routes non-INamedTypeSymbol results to NotAType.
        var lines = File.ReadAllLines(RoslynServicePath);
        var matchLine = Array.FindIndex(lines, l => l.Contains("MatchesGlobPattern(string input"));
        matchLine.Should().BeGreaterThan(0, "MatchesGlobPattern must exist in RoslynService.cs");

        var result = await Service.ExtractInterfaceAsync(
            RoslynServicePath, matchLine, 30,
            interfaceName: "IDoesNotMatter",
            includeMemberNames: null);

        AssertError(result, ErrorCodes.NotAType);
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
        // Locks the response shape declared at Refactoring.cs:521-533.
        data["appliesEditsAutomatically"]!.Value<bool>().Should().BeFalse(
            "generate_constructor is generation-only — it does NOT mutate the workspace");
        data["typeName"]!.Value<string>().Should().Contain("RefactoringTarget");

        // RefactoringTarget has one non-static, non-const, non-implicit field: BareCounter.
        var fields = data["fields"] as JArray;
        fields.Should().NotBeNull();
        fields!.Select(f => f.Value<string>()).Should().Contain("BareCounter");

        data["parameterCount"]!.Value<int>().Should().Be(fields!.Count,
            "parameterCount must equal the count of fields when includeProperties is false");

        // parameters[] each has { name, type }.
        var parameters = data["parameters"] as JArray;
        parameters.Should().NotBeNull();
        parameters!.Count.Should().Be(fields.Count);
        foreach (var p in parameters!)
        {
            p["name"].Should().NotBeNull();
            p["type"].Should().NotBeNull();
        }

        var code = data["constructorCode"]!.Value<string>()!;
        code.Should().Contain("public RefactoringTarget",
            "the generated constructor must declare the type's public ctor");
    }

    // ChangeSignature tests live in ChangeSignatureTests.cs.

    [Fact]
    public async Task ExtractInterface_OnEventBearingTarget_EmitsEventDeclaration()
    {
        // RoslynService.cs:664-668: IEventSymbol branch emits `event T Name;`.
        // EventBearingTarget has a public event `Triggered` of type EventHandler.
        var searchResult = await Service.SearchSymbolsAsync("EventBearingTarget", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        var target = symbols![0];
        var loc = target["location"]!;
        var result = await Service.ExtractInterfaceAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            interfaceName: "IEventBearing",
            includeMemberNames: null);

        AssertSuccess(result);
        var data = GetData(result);
        var code = data["interfaceCode"]!.Value<string>()!;
        // The IEventSymbol branch at RoslynService.cs:666-668 emits `event T Name;`
        // where T is the event type's ToDisplayString() (may be fully qualified).
        code.Should().Contain("event ",
            "the IEventSymbol branch must emit the `event` keyword");
        code.Should().Contain("Triggered;",
            "the event name with terminating semicolon must appear");
        code.Should().NotContain("OnTriggered",
            "the protected method OnTriggered must NOT leak into the interface");
    }

    [Fact]
    public async Task GenerateConstructor_IncludeProperties_PicksUpSettableProperties()
    {
        // Refactoring.cs:466-477: with includeProperties=true, regular get/set
        // properties (non-init-only, public setter) are added to the parameter
        // list alongside fields. GenCtorWithProperties has two such properties.
        var searchResult = await Service.SearchSymbolsAsync("GenCtorWithProperties", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        var target = symbols![0];
        var loc = target["location"]!;
        var result = await Service.GenerateConstructorAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            includeProperties: true);

        AssertSuccess(result);
        var data = GetData(result);
        data["parameterCount"]!.Value<int>().Should().Be(2,
            "Name + Age = 2 settable properties");
        var props = (data["properties"] as JArray)!;
        props.Select(p => p.Value<string>()).Should().BeEquivalentTo(new[] { "Name", "Age" });
        var code = data["constructorCode"]!.Value<string>()!;
        code.Should().Contain("public GenCtorWithProperties");
        code.Should().Contain("Name = name");
        code.Should().Contain("Age = age");
    }

    [Fact]
    public async Task GenerateConstructor_InitializeToDefault_OnNullableField_EmitsNullCoalesce()
    {
        // Refactoring.cs:508-510: when initializeToDefault=true and a field is
        // nullable-annotated, the assignment becomes `Field = param ?? default;`.
        var searchResult = await Service.SearchSymbolsAsync("GenCtorWithNullableField", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        var target = symbols![0];
        var loc = target["location"]!;
        var result = await Service.GenerateConstructorAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            initializeToDefault: true);

        AssertSuccess(result);
        var data = GetData(result);
        var code = data["constructorCode"]!.Value<string>()!;
        code.Should().Contain("OptionalName = optionalName ?? default",
            "nullable field with initializeToDefault gets a null-coalesce to default");
    }

    [Fact]
    public async Task GenerateConstructor_OnTypeWithNoFieldsOrProperties_ReturnsAnalysisFailed()
    {
        // Refactoring.cs:482-492: empty class → "No fields or properties found
        // to initialize" AnalysisFailed branch.
        var searchResult = await Service.SearchSymbolsAsync("GenCtorEmpty", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        var target = symbols![0];
        var loc = target["location"]!;
        var result = await Service.GenerateConstructorAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());

        AssertError(result, ErrorCodes.AnalysisFailed);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>()!.Should().Contain("No fields or properties");
        json["error"]!["Hint"]!.Value<string>()!.Should().Contain("includeProperties",
            "the no-fields hint points the caller at includeProperties: true");
    }

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

        // Extract the two `var` statements in the body.
        var result = await Service.ExtractMethodAsync(
            file,
            startLine: methodLine + 2,
            endLine: methodLine + 3,
            methodName: "ComputePartial",
            preview: true);

        AssertSuccess(result);
        var data = GetData(result);
        // Locks every field emitted by the preview branch (Refactoring.cs:1151-1170).
        data["preview"]!.Value<bool>().Should().BeTrue();
        data["methodName"]!.Value<string>().Should().Be("ComputePartial");
        data["returnType"]!.Value<string>().Should().Be("int",
            "Sum returns int and the slice produces an int `total` that flows out");
        data["returnVariable"]!.Value<string>().Should().Be("total");
        data["returnReason"]!.Value<string>().Should().Contain("total",
            "returnReason must reference the variable that flows out of the selection");
        data["statementsExtracted"]!.Value<int>().Should().Be(2,
            "the selection contains exactly two var-declaration statements");

        data["signature"]!.Value<string>().Should()
            .Contain("private int ComputePartial",
                "default accessibility is 'private' (Refactoring.cs:959) and the return is int");
        data["extractedCode"]!.Value<string>().Should()
            .Contain("private int ComputePartial",
                "the extracted method body must open with the generated signature");
        data["replacementCode"]!.Value<string>().Should()
            .Be("var total = ComputePartial(a, b, c);",
                "the call-site replacement must capture the returned `total` and forward Sum's parameters in order");

        // parameters[] — each is { name, type, reason }. Sum's signature is (int a, int b, int c)
        // and all three flow into the selection.
        var parameters = data["parameters"] as JArray;
        parameters.Should().NotBeNull();
        parameters!.Count.Should().Be(3, "Sum's signature is (int a, int b, int c)");
        var paramNames = parameters.Select(p => p["name"]!.Value<string>()).ToList();
        paramNames.Should().BeEquivalentTo(new[] { "a", "b", "c" });
        foreach (var p in parameters)
        {
            p["type"]!.Value<string>().Should().Be("int");
            p["reason"]!.Value<string>().Should().Be("read inside selection");
        }
    }

    [Fact]
    public async Task GetMissingMembers_HandlesPositionWithNoIncompleteImpl()
    {
        // Position inside MatchesGlobPattern — a complete static helper in RoslynService.cs.
        // The impl walks up to find the containing TypeDeclarationSyntax (RoslynService).
        var lines = File.ReadAllLines(RoslynServicePath);
        var matchLine = Array.FindIndex(lines, l => l.Contains("MatchesGlobPattern(string input"));
        matchLine.Should().BeGreaterThan(0, "MatchesGlobPattern must exist in RoslynService.cs");

        var result = await Service.GetMissingMembersAsync(RoslynServicePath, matchLine + 5, 10);

        AssertSuccess(result);
        var data = GetData(result);
        // Locks the response shape (Inspection.cs:789-796). The impl always emits an array
        // (possibly empty) — never a null. The previous test had an unreachable `missing == null`
        // branch; remove it so a future schema change that DROPS the field would fail loudly.
        data["typeName"]!.Value<string>().Should().Contain("RoslynService",
            "MatchesGlobPattern is declared on RoslynService");
        data["isAbstract"]!.Value<bool>().Should().BeFalse("RoslynService is concrete");
        data["interfaces"].Should().NotBeNull();

        var missing = data["missingMembers"] as JArray;
        missing.Should().NotBeNull("the impl always emits missingMembers as an array");
        missing!.Count.Should().Be(0,
            "RoslynService has no unimplemented interface/abstract members");
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

        // Find a known invocation inside the method body — the GetProjectCompilationAsync
        // call inside the foreach. Use RoslynServicePath since search returns a relative path.
        var lines = File.ReadAllLines(RoslynServicePath);
        var callLine = Array.FindIndex(lines, methodLine, l => l.Contains("GetProjectCompilationAsync(project)"));
        callLine.Should().BeGreaterThan(methodLine,
            "the GetProjectCompilationAsync invocation must live inside GetHealthCheckAsync's body");

        var result = await Service.GetOutgoingCallsAsync(file, callLine + 1, 20);

        AssertSuccess(result);
        var data = GetData(result);
        // Locks the response shape (Inspection.cs:942-948).
        data["method"]!.Value<string>().Should().Contain("GetHealthCheckAsync");
        data["containingType"]!.Value<string>().Should().Contain("RoslynService");

        var calls = data["calls"] as JArray;
        calls.Should().NotBeNullOrEmpty();
        // Locate the GetProjectCompilationAsync entry and lock its returned shape.
        var gpc = calls!.FirstOrDefault(c =>
            c["shortName"]?.Value<string>()?.EndsWith(".GetProjectCompilationAsync") == true);
        gpc.Should().NotBeNull("outgoing calls must include the GetProjectCompilationAsync invocation");
        gpc!["isAsync"]!.Value<bool>().Should().BeTrue(
            "GetProjectCompilationAsync is an async method (RoslynService.cs)");
        gpc["returnType"]!.Value<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task OrganizeUsings_OnRoslynServiceCs_OutputsSystemFirstThenAlphabetical()
    {
        var result = await Service.OrganizeUsingsAsync(RoslynServicePath);

        AssertSuccess(result);
        var data = GetData(result);
        var organized = data["organizedText"]!.Value<string>()!;
        organized.Should().Contain("using System");
        organized.Should().Contain("using Microsoft.CodeAnalysis");

        // The contract of OrganizeUsingsAsync (Analysis.cs:981-984) is: bucket 0 = System*,
        // bucket 1 = everything else, alphabetical within each bucket. Verify System* appears
        // BEFORE non-System usings — the test was previously a smoke check that didn't
        // verify any actual ordering.
        var systemIdx = organized.IndexOf("using System");
        var microsoftIdx = organized.IndexOf("using Microsoft.CodeAnalysis");
        systemIdx.Should().BeGreaterOrEqualTo(0);
        microsoftIdx.Should().BeGreaterOrEqualTo(0);
        systemIdx.Should().BeLessThan(microsoftIdx,
            "System-prefixed usings must come before non-System usings per the OrderBy in Analysis.cs:981-984");
    }

    [Fact]
    public async Task OrganizeUsingsBatch_ProcessesSharpLensMcpProject_ReportsScannedAndChanges()
    {
        var result = await Service.OrganizeUsingsBatchAsync(
            projectName: "SharpLensMcp",
            filePattern: null,
            preview: true);

        AssertSuccess(result);
        var data = GetData(result);
        // Locks the response shape declared at Analysis.cs:1094-1101 — the prior test
        // checked a `fileCount` field the impl does NOT emit, then fell back to counting
        // `files` which only contains files-with-changes. That made the test pass for the
        // wrong reason. Lock the real contract.
        data["preview"]!.Value<bool>().Should().BeTrue();
        data["totalFilesScanned"]!.Value<int>().Should().BeGreaterThan(0,
            "SharpLensMcp has multiple .cs files visible to the batch scanner");
        data["filesWithChanges"]!.Value<int>().Should().BeGreaterOrEqualTo(0,
            "filesWithChanges must be present even when zero — a missing field would silently break consumers");

        var files = data["files"] as JArray;
        files.Should().NotBeNull("files array must always be emitted (may be empty)");
    }

    [Fact]
    public async Task FormatDocumentBatch_FormatsSharpLensMcpProject_ReportsScannedAndFormatted()
    {
        var result = await Service.FormatDocumentBatchAsync(
            projectName: "SharpLensMcp",
            preview: true);

        AssertSuccess(result);
        var data = GetData(result);
        // Locks the response shape declared at Analysis.cs:1188-1195 — same `fileCount`
        // misnomer fix as OrganizeUsingsBatch.
        data["preview"]!.Value<bool>().Should().BeTrue();
        data["totalFilesScanned"]!.Value<int>().Should().BeGreaterThan(0);
        data["filesFormatted"]!.Value<int>().Should().BeGreaterOrEqualTo(0,
            "filesFormatted must be present even when zero");

        var files = data["files"] as JArray;
        files.Should().NotBeNull("files array must always be emitted (may be empty)");
    }

    [Fact]
    public async Task GetMethodOverloads_OnCreateErrorResponse_LocksMethodAndOverloadShape()
    {
        // Filter to the RoslynService overload specifically — McpServer ALSO has a
        // CreateErrorResponse (different signature: RequestId/code/message), and the
        // search returns both. The first hit is order-dependent and unreliable.
        var searchResult = await Service.SearchSymbolsAsync("CreateErrorResponse", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols!.First(s =>
            s["containingType"]?.Value<string>()?.Contains("RoslynService") == true);
        var loc = symbol["location"]!;
        var result = await Service.GetMethodOverloadsAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());

        AssertSuccess(result);
        var data = GetData(result);
        // Response shape (Inspection.cs:126-140): methodName, containingType, overloads[].
        data["methodName"]!.Value<string>().Should().Be("CreateErrorResponse");
        data["containingType"]!.Value<string>().Should().EndWith("RoslynService",
            "CreateErrorResponse is declared on the RoslynService partial class");

        var overloads = data["overloads"] as JArray;
        overloads.Should().NotBeNullOrEmpty();
        overloads!.Count.Should().BeGreaterOrEqualTo(1,
            "GetMembers(name) must return at least the method itself");
        // Per-overload shape (Inspection.cs:104-122): signature, parameters[], returnType,
        // isAsync, isStatic, location.
        foreach (var o in overloads!)
        {
            o["signature"]!.Value<string>().Should().Contain("CreateErrorResponse");
            o["parameters"].Should().NotBeNull("each overload must report its parameters list");
            o["returnType"].Should().NotBeNull("each overload must report its return type");
        }
    }

    [Fact]
    public async Task GetContainingMember_AtMethodBody_ReturnsExpectedMember()
    {
        // Find the body of MatchesGlobPattern by its `var regexPattern = "^"` assignment.
        var lines = File.ReadAllLines(RoslynServicePath);
        var bodyLine = Array.FindIndex(lines, l => l.Contains("var regexPattern = \"^\""));
        bodyLine.Should().BeGreaterThan(0, "the regexPattern assignment inside MatchesGlobPattern must exist");

        var result = await Service.GetContainingMemberAsync(RoslynServicePath, bodyLine + 1, 10);

        AssertSuccess(result);
        var data = GetData(result);
        // Locks every field in the success response (Inspection.cs:218-232).
        data["memberName"]!.Value<string>().Should().Be("MatchesGlobPattern");
        data["memberKind"]!.Value<string>().Should().Be("Method");
        data["containingType"]!.Value<string>().Should().Contain("RoslynService");
        data["signature"]!.Value<string>().Should().Contain("MatchesGlobPattern");

        var span = data["span"]!;
        var startLine = span["startLine"]!.Value<int>();
        var endLine = span["endLine"]!.Value<int>();
        startLine.Should().BeGreaterOrEqualTo(0);
        endLine.Should().BeGreaterThan(startLine, "method span must cover at least one line");
    }

    [Fact]
    public async Task GetAttributes_FindsFactAttributeOnXunitTests()
    {
        // The test project uses [Fact] extensively — every test method carries it.
        var result = await Service.GetAttributesAsync("Fact");

        AssertSuccess(result);
        var data = GetData(result);
        // Locks the full response shape (TypeDiscovery.cs:492-504).
        data["attributeName"]!.Value<string>().Should().Be("Fact");
        data["totalFound"]!.Value<int>().Should().BeGreaterThan(0,
            "the test project has at least one [Fact]-decorated method");

        var symbols = data["symbols"] as JArray;
        symbols.Should().NotBeNullOrEmpty(
            "the test project has many [Fact]-decorated methods");
        foreach (var s in symbols!)
        {
            // Each result must carry attribute info with the actual FactAttribute class name.
            s["attribute"].Should().NotBeNull();
            s["attribute"]!["name"]!.Value<string>().Should().Be("FactAttribute");
            s["symbolKind"]!.Value<string>().Should().Be("Method",
                "[Fact] only decorates methods in xUnit");
            s["containingType"].Should().NotBeNull(
                "every [Fact] method must report its containing test class");
        }
    }

    [Fact]
    public async Task FindImplementations_OnIShapeFixture_FindsAllImplementersIncludingTransitive()
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
        // Locks the response shape (Navigation.cs:467-473).
        data["baseType"]!.Value<string>().Should().EndWith("IShapeFixture");
        data["totalImplementations"]!.Value<int>().Should().BeGreaterOrEqualTo(3,
            "InterfaceHierarchyFixture declares Circle, Rectangle, AND transitively Square");

        var impls = data["implementations"] as JArray;
        impls.Should().NotBeNullOrEmpty();
        var names = impls!.Select(i => i["name"]!.Value<string>()!).ToList();
        names.Should().Contain(n => n.EndsWith("FixtureCircle"));
        names.Should().Contain(n => n.EndsWith("FixtureRectangle"));
        names.Should().Contain(n => n.EndsWith("FixtureSquare"),
            "find_implementations must include transitive implementers (FixtureSquare : FixtureRectangle : IShapeFixture)");

        // Per-impl shape (Navigation.cs:458-464): name, kind, containingNamespace, locations[].
        foreach (var i in impls!)
        {
            i["kind"]!.Value<string>().Should().Be("Class",
                "all three implementers are classes");
            i["locations"].Should().NotBeNull(
                "each implementation must carry source locations");
        }
    }

    [Fact]
    public async Task GetTypeHierarchy_OnFixtureSquare_ListsRectangleAndIShapeFixture()
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
        // Locks the response shape (Navigation.cs:587-594).
        data["typeName"]!.Value<string>().Should().EndWith("FixtureSquare");

        var baseTypes = data["baseTypes"] as JArray;
        baseTypes.Should().NotBeNullOrEmpty();
        baseTypes!.Any(b => b["name"]!.Value<string>()!.EndsWith("FixtureRectangle"))
            .Should().BeTrue("FixtureSquare's base chain must include FixtureRectangle");

        // FixtureSquare transitively implements IShapeFixture via FixtureRectangle, so
        // AllInterfaces must include it.
        var interfaces = data["interfaces"] as JArray;
        interfaces.Should().NotBeNull();
        interfaces!.Any(i => i["name"]!.Value<string>()!.EndsWith("IShapeFixture"))
            .Should().BeTrue("FixtureSquare transitively implements IShapeFixture");

        // derivedTypes is always emitted (may be empty for a leaf class).
        data["derivedTypes"].Should().NotBeNull();
    }
}
