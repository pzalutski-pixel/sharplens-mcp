using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// Phase 11.6 — wire-level (ViaMcp / content[0].text) coverage for tools that
// previously had only in-process unit tests, so each is exercised through the
// real JSON-RPC dispatcher with exact fixture locks plus an error path. Each
// test mirrors the corresponding unit test (named in the method) against the
// same engineered fixtures the unit test uses.
public class WireCoverageViaMcpTests : McpTestBase
{
    public WireCoverageViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    // Resolves a marker line (0-based) in a Fixtures/*.cs file the same way the
    // unit tests do, so line locks stay exact yet drift-proof.
    private int FixtureLineOf(string fixtureFile, string marker)
    {
        var path = Path.Combine(Fixture.SolutionDir,
            "tests", "SharpLensMcp.Tests", "Fixtures", fixtureFile);
        var idx = Array.FindIndex(File.ReadAllLines(path), l => l.Contains(marker));
        idx.Should().BeGreaterThan(-1, $"the fixture must contain '{marker}'");
        return idx;
    }

    // Mirrors DependencyAuditTests.SrcProject_ReportsReflectionLoadedFeaturePackages.
    [Fact]
    public async Task FindUnusedDependencies_OnSrcProject_LocksUnusedAndSkipped()
    {
        var data = await CallAndGetDataAsync("find_unused_dependencies",
            new { projectName = "SharpLensMcp" });

        var projects = (data["projects"] as JArray)!;
        projects.Should().ContainSingle();
        var src = projects[0];
        src["projectName"]!.Value<string>().Should().Be("SharpLensMcp");
        src["packagesChecked"]!.Value<int>().Should().Be(7,
            "the src csproj declares exactly seven PackageReference entries");
        (src["unusedPackages"] as JArray)!.Select(u => u["name"]!.Value<string>())
            .Should().BeEquivalentTo("Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features");
        (src["skipped"] as JArray)!.Select(s => s["name"]!.Value<string>())
            .Should().BeEquivalentTo("Microsoft.Build.Framework", "Microsoft.SourceLink.GitHub");
    }

    // Mirrors DependencyAuditTests.UnknownProject_ReturnsInvalidParameterListingProjects.
    [Fact]
    public async Task FindUnusedDependencies_UnknownProject_ReturnsInvalidParameter()
    {
        var error = await CallAndGetErrorAsync("find_unused_dependencies",
            new { projectName = "NoSuchProject" }, codeContains: ErrorCodes.InvalidParameter);
        error["hint"]!.Value<string>()!.Should().Contain("SharpLensMcp");
    }

    // Mirrors ArchitectureCheckTests.DomainToInfrastructureEdge_IsFlaggedWithLocation.
    [Fact]
    public async Task CheckArchitecture_FlagsTheDomainToInfrastructureEdge()
    {
        var data = await CallAndGetDataAsync("check_architecture", new
        {
            rules = new[] { new { from = "Production.Domain*", deny = "Production.Infrastructure*" } }
        });

        data["level"]!.Value<string>().Should().Be("namespace");
        data["rulesEvaluated"]!.Value<int>().Should().Be(1);
        var violations = (data["violations"] as JArray)!;
        violations.Should().ContainSingle("exactly one type depends on the denied namespace");
        violations[0]["fromType"]!.Value<string>().Should().Be("Production.Domain.OrderAggregate");
        violations[0]["toType"]!.Value<string>().Should().Be("Production.Infrastructure.SqlOrderRepository");
        violations[0]["location"]!["filePath"]!.Value<string>()!.Should().EndWith("ArchitectureFixture.cs");
    }

    // Mirrors ArchitectureCheckTests.EmptyRules_ReturnsInvalidParameter.
    [Fact]
    public async Task CheckArchitecture_EmptyRules_ReturnsInvalidParameter()
    {
        await CallAndGetErrorAsync("check_architecture",
            new { rules = Array.Empty<object>() }, codeContains: ErrorCodes.InvalidParameter);
    }

    // Mirrors AsyncIssuesTests.FlagsOneOfEachCategory_AndExemptsTheEventHandler.
    [Fact]
    public async Task FindAsyncIssues_FlagsOneOfEachCategory_ExemptsEventHandler()
    {
        var data = await CallAndGetDataAsync("find_async_issues",
            new { projectName = "SharpLensMcp.Tests" });

        var issues = (data["issues"] as JArray)!
            .Where(i => i["symbol"]!.Value<string>()!.Contains("AsyncIssuesTarget"))
            .ToList();
        issues.Should().Contain(i => i["category"]!.Value<string>() == "asyncVoid"
            && i["symbol"]!.Value<string>()!.Contains("FireAndForget"));
        issues.Should().Contain(i => i["category"]!.Value<string>() == "blockingCall"
            && i["symbol"]!.Value<string>()!.Contains("BlockOnResult"));
        issues.Should().Contain(i => i["category"]!.Value<string>() == "tokenNotForwarded"
            && i["symbol"]!.Value<string>()!.Contains("ForwardsNothing"));
        issues.Should().NotContain(i => i["symbol"]!.Value<string>()!.Contains("OnClick"),
            "an (object, EventArgs) async void is a legitimate event handler");
    }

    // Mirrors AsyncIssuesTests.MaxResultsOutOfRange_ReturnsInvalidParameter.
    [Fact]
    public async Task FindAsyncIssues_MaxResultsOutOfRange_ReturnsInvalidParameter()
    {
        await CallAndGetErrorAsync("find_async_issues",
            new { maxResults = 0 }, codeContains: ErrorCodes.InvalidParameter);
    }

    // Mirrors FindTestsTests.DirectlyTestedMember_MapsToItsCoveringTest.
    [Fact]
    public async Task FindTests_DirectlyTestedMember_MapsToItsCoveringTest()
    {
        var data = await CallAndGetDataAsync("find_tests",
            new { typeName = "CoverageTarget", memberName = "CoveredByTest" });

        var names = (data["coveringTests"] as JArray)!
            .Select(t => t["testMethod"]!.Value<string>()!).ToList();
        names.Should().Contain("CoveredByTest_Test");
        names.Should().NotContain("ChainedFromTest_Test",
            "CoveredByTest is reached only by its own test");
        var entry = (data["coveringTests"] as JArray)!
            .First(t => t["testMethod"]!.Value<string>() == "CoveredByTest_Test");
        entry["testProject"]!.Value<string>().Should().Be("SharpLensMcp.Tests");
        entry["location"]!["filePath"]!.Value<string>()!.Should().EndWith("TestCoverageFixture.cs");
    }

    // Mirrors FindTestsTests.UnknownType_PropagatesTypeNotFound.
    [Fact]
    public async Task FindTests_UnknownType_ReturnsTypeNotFound()
    {
        await CallAndGetErrorAsync("find_tests",
            new { typeName = "NoSuchTypeAnywhere", memberName = "X" }, codeContains: ErrorCodes.TypeNotFound);
    }

    // The type-level branch: with no memberName, a test covers the type when it reaches
    // ANY of its members, so both the direct and the transitive fixture tests qualify.
    [Fact]
    public async Task FindTests_TypeLevel_NoMember_CoversTypeViaAnyMember()
    {
        var data = await CallAndGetDataAsync("find_tests", new { typeName = "CoverageTarget" });

        data["target"]!.Value<string>()!.Should().EndWith("CoverageTarget",
            "with no member, the type itself is the coverage target");
        var names = (data["coveringTests"] as JArray)!.Select(t => t["testMethod"]!.Value<string>()!).ToList();
        names.Should().Contain("CoveredByTest_Test");
        names.Should().Contain("ChainedFromTest_Test",
            "both fixture tests reach a member of CoverageTarget, so both cover the type");
    }

    // Mirrors SuperMethodTests.OverrideThatAlsoImplements_ReturnsBothBaseAndInterface.
    [Fact]
    public async Task GetSuperMethod_OverrideThatAlsoImplements_ReturnsBothBaseAndInterface()
    {
        var data = await CallAndGetDataAsync("get_super_method",
            new { typeName = "SuperDerived", memberName = "Handle" });

        (data["overrides"] as JArray)!.Select(e => e["signature"]!.Value<string>()!)
            .Should().ContainSingle().Which.Should().Contain("SuperBase.Handle");
        (data["implements"] as JArray)!.Select(e => e["signature"]!.Value<string>()!)
            .Should().ContainSingle().Which.Should().Contain("ISuperContract.Handle");
        (data["overrides"] as JArray)![0]["location"]!["filePath"]!.Value<string>()
            .Should().EndWith("SuperMethodFixture.cs");
    }

    // Mirrors SuperMethodTests.NeitherAddressingMode_ReturnsInvalidParameter.
    [Fact]
    public async Task GetSuperMethod_NeitherAddressingMode_ReturnsInvalidParameter()
    {
        await CallAndGetErrorAsync("get_super_method",
            new { }, codeContains: ErrorCodes.InvalidParameter);
    }

    // Mirrors SuperMethodTests' property-override case through the dispatcher.
    [Fact]
    public async Task GetSuperMethod_PropertyOverride_ReturnsBaseProperty()
    {
        var data = await CallAndGetDataAsync("get_super_method",
            new { typeName = "SuperDerived", memberName = "Score" });

        (data["overrides"] as JArray)!.Select(e => e["signature"]!.Value<string>()!)
            .Should().ContainSingle().Which.Should().Contain("SuperBase.Score");
        (data["implements"] as JArray)!.Should().BeEmpty("a property override has no interface involvement");
    }

    // Mirrors SuperMethodTests' metadata-base-override case through the dispatcher.
    [Fact]
    public async Task GetSuperMethod_MetadataOverride_FlagsInMetadataWithNullLocation()
    {
        var data = await CallAndGetDataAsync("get_super_method",
            new { typeName = "SuperDerived", memberName = "ToString" });

        var overrides = (data["overrides"] as JArray)!;
        overrides.Should().ContainSingle();
        overrides[0]["inMetadata"]!.Value<bool>().Should().BeTrue(
            "object.ToString is defined in metadata, not solution source");
        overrides[0]["location"]!.Type.Should().Be(JTokenType.Null,
            "a metadata super has no source location");
    }

    // Mirrors SuperMethodTests' interface-event case through the dispatcher.
    [Fact]
    public async Task GetSuperMethod_EventImplementation_ReturnsInterfaceEvent()
    {
        var data = await CallAndGetDataAsync("get_super_method",
            new { typeName = "SimpleImpl", memberName = "Fired" });

        (data["implements"] as JArray)!.Select(e => e["signature"]!.Value<string>()!)
            .Should().ContainSingle().Which.Should().Contain("ISuperContract.Fired");
    }

    // Mirrors SuperMethodTests' explicit-interface-implementation case through the
    // dispatcher. Resolved by position: an explicit implementation's member name is
    // "ISuperContract.ExplicitOnly", so simple-name addressing would not find it.
    [Fact]
    public async Task GetSuperMethod_ExplicitInterfaceImplementation_ReturnsInterfaceMember()
    {
        var (file, line, col) = await LocateSymbolAsync("ExplicitOnly", "Method",
            r => r["containingType"]?.Value<string>()?.EndsWith("SuperDerived") == true);
        var data = await CallAndGetDataAsync("get_super_method", new { filePath = file, line, column = col });

        (data["implements"] as JArray)!.Select(e => e["signature"]!.Value<string>()!)
            .Should().ContainSingle().Which.Should().Contain("ISuperContract.ExplicitOnly");
    }

    // Mirrors ExceptionFlowTests.Catcher_ReportsCaughtAtItsCatch_WithThrowSiteFromCallee.
    [Fact]
    public async Task GetExceptionFlow_Catcher_ReportsCaughtWithThrowSiteFromCallee()
    {
        var data = await CallAndGetDataAsync("get_exception_flow",
            new { typeName = "ExceptionFlowTarget", memberName = "CatcherB" });

        (data["escaping"] as JArray)!.Should().BeEmpty("CatcherB catches the only exception its callee throws");
        var caught = (data["caught"] as JArray)!;
        caught.Should().ContainSingle();
        caught[0]["exceptionType"]!.Value<string>().Should().Be("System.InvalidOperationException");
        caught[0]["catchLocation"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("ExceptionFlowFixture.cs", "catch (InvalidOperationException)"));
        caught[0]["thrownFrom"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("ExceptionFlowFixture.cs", "throw new InvalidOperationException(\"a\")"));
    }

    // Mirrors ExceptionFlowTests.BareCaller_ReportsEscaping_WithCalleeThrowSite — the
    // ESCAPING path (the wire tests only covered the fully-caught case).
    [Fact]
    public async Task GetExceptionFlow_BareCaller_ReportsEscapingWithThrowSite()
    {
        var data = await CallAndGetDataAsync("get_exception_flow",
            new { typeName = "ExceptionFlowTarget", memberName = "BareCallerC" });

        (data["caught"] as JArray)!.Should().BeEmpty("BareCallerC has no try/catch");
        var escaping = (data["escaping"] as JArray)!;
        escaping.Should().ContainSingle();
        escaping[0]["exceptionType"]!.Value<string>().Should().Be("System.InvalidOperationException");
        escaping[0]["sampleThrowSite"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("ExceptionFlowFixture.cs", "throw new InvalidOperationException(\"a\")"));
        data["truncatedByDepth"]!.Value<bool>().Should().BeFalse("depth 3 covers the one-hop chain");
    }

    // Mirrors ExceptionFlowTests.MaxDepthOutOfRange_ReturnsInvalidParameter.
    [Fact]
    public async Task GetExceptionFlow_MaxDepthOutOfRange_ReturnsInvalidParameter()
    {
        var error = await CallAndGetErrorAsync("get_exception_flow",
            new { typeName = "ExceptionFlowTarget", memberName = "CatcherB", maxDepth = 0 },
            codeContains: ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>()!.Should().Contain("between 1 and 10");
    }

    // Mirrors InterceptorTests.FindsBothAttributeForms_WithExactTargets.
    [Fact]
    public async Task FindInterceptors_FindsBothAttributeForms()
    {
        var data = await CallAndGetDataAsync("find_interceptors", new { projectName = "SharpLensMcp.Tests" });
        var interceptors = (data["interceptors"] as JArray)!;
        interceptors.Should().HaveCount(2, "the fixture declares one legacy and one data form");
        var legacy = interceptors.First(i =>
            i["interceptorMethod"]!.Value<string>() == "InterceptorImpls.InterceptOriginal");
        legacy["target"]!["form"]!.Value<string>().Should().Be("legacy");
        legacy["target"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("InterceptorFixture.cs", "intercepted-call marker"));
        var dataForm = interceptors.First(i =>
            i["interceptorMethod"]!.Value<string>() == "InterceptorImpls.InterceptViaData");
        dataForm["target"]!["form"]!.Value<string>().Should().Be("data");
        dataForm["target"]!["raw"]!.Value<string>().Should().Be("T0tally-0paque-Data==");
    }

    // Mirrors InterceptorTests.MaxResultsOutOfRange_ReturnsInvalidParameter.
    [Fact]
    public async Task FindInterceptors_MaxResultsOutOfRange_ReturnsInvalidParameter()
    {
        await CallAndGetErrorAsync("find_interceptors", new { maxResults = 0 }, codeContains: ErrorCodes.InvalidParameter);
    }

    // Mirrors FineGrainedSearchTests.FindTypeInstantiations_FindsExplicitAndImplicitNew.
    [Fact]
    public async Task FindTypeInstantiations_FindsExplicitAndImplicitNew()
    {
        var data = await CallAndGetDataAsync("find_type_instantiations", new { typeName = "FineGrainedWidget" });
        var matches = (data["matches"] as JArray)!;
        matches.Should().HaveCount(2, "Make uses `new FineGrainedWidget()` and MakeImplicit uses `new()`");
        matches.Should().OnlyContain(m => m["kind"]!.Value<string>() == "objectCreation");
    }

    // Mirrors FineGrainedSearchTests.FindPatternUsages_FindsIsAsAndSwitchTypePattern.
    [Fact]
    public async Task FindPatternUsages_FindsIsAsAndTypePattern()
    {
        var data = await CallAndGetDataAsync("find_pattern_usages", new { typeName = "FineGrainedWidget" });
        var kinds = (data["matches"] as JArray)!.Select(m => m["kind"]!.Value<string>()).ToList();
        kinds.Should().Contain("isExpression");
        kinds.Should().Contain("asExpression");
        kinds.Should().Contain("typePattern");
    }

    // Mirrors FineGrainedSearchTests.FindThrowSites_FindsTheThrow_Exact.
    [Fact]
    public async Task FindThrowSites_Exact_FindsTheThrow()
    {
        var data = await CallAndGetDataAsync("find_throw_sites",
            new { typeName = "FineGrainedException", includeDerived = false });
        (data["matches"] as JArray)!.Select(m => m["kind"]!.Value<string>())
            .Should().ContainSingle().Which.Should().Be("throw");
    }

    // Mirrors FineGrainedSearchTests.FindCatchBlocks_IncludeDerived_AlsoFindsTheBaseCatch.
    [Fact]
    public async Task FindCatchBlocks_IncludeDerived_AlsoFindsBaseCatch()
    {
        var data = await CallAndGetDataAsync("find_catch_blocks",
            new { typeName = "FineGrainedException", includeDerived = true });
        var lines = (data["matches"] as JArray)!.Select(m => m["lineText"]!.Value<string>()).ToList();
        lines.Should().Contain(t => t!.Contains("catch (FineGrainedException)"));
        lines.Should().Contain(t => t!.Contains("catch (System.Exception)"));
    }

    // Mirrors FineGrainedSearchTests.UnknownType_ReturnsTypeNotFound.
    [Fact]
    public async Task FindTypeInstantiations_UnknownType_ReturnsTypeNotFound()
    {
        await CallAndGetErrorAsync("find_type_instantiations",
            new { typeName = "NoSuchTypeAnywhere" }, codeContains: ErrorCodes.TypeNotFound);
    }

    // Mirrors SimilarCodeTests.MethodQuery_FindsRenamedTwin_ExcludesSelfAndUnrelated.
    [Fact]
    public async Task FindSimilarCode_FindsRenamedTwin_ExcludesSelfAndUnrelated()
    {
        var data = await CallAndGetDataAsync("find_similar_code",
            new { typeName = "SimilarCodeHost", methodName = "SumOfSquaresAlpha" });

        var results = (data["results"] as JArray)!;
        var names = results.Select(r => r["method"]!.Value<string>()).ToList();
        names.Should().Contain("SimilarCodeHost.SumOfCubesBeta", "Beta is Alpha with every identifier renamed");
        names.Should().NotContain("SimilarCodeHost.SumOfSquaresAlpha", "the query method must be excluded");
        names.Should().NotContain("SimilarCodeHost.CompletelyDifferent");
        var beta = results.First(r => r["method"]!.Value<string>() == "SimilarCodeHost.SumOfCubesBeta");
        beta["score"]!.Value<double>().Should().Be(1.0);
        beta["statementCount"]!.Value<int>().Should().Be(3);
    }

    // Mirrors SimilarCodeTests.BothQueryModes_ReturnsInvalidParameter.
    [Fact]
    public async Task FindSimilarCode_BothQueryModes_ReturnsInvalidParameter()
    {
        var error = await CallAndGetErrorAsync("find_similar_code",
            new { typeName = "SimilarCodeHost", methodName = "SumOfSquaresAlpha", snippet = "var x = 1;" },
            codeContains: ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>()!.Should().Contain("not both");
    }

    // Mirrors StackTraceResolutionTests.MangledAsyncFrame_DemanglesAndResolvesToTheMethodDeclaration.
    [Fact]
    public async Task ResolveStackTrace_MangledAsyncFrame_DemanglesAndResolves()
    {
        var data = await CallAndGetDataAsync("resolve_stack_trace", new
        {
            stackTrace = "   at SharpLensMcp.Tests.Fixtures.StackTraceSource+<ThrowingAsync>d__2.MoveNext()"
        });
        var frames = (data["frames"] as JArray)!;
        frames.Should().ContainSingle();
        frames[0]["frameKind"]!.Value<string>().Should().Be("async");
        frames[0]["symbol"]!.Value<string>().Should().Be("SharpLensMcp.Tests.Fixtures.StackTraceSource.ThrowingAsync");
        frames[0]["resolved"]!.Value<bool>().Should().BeTrue();
        frames[0]["location"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("StackTraceFixture.cs", "private static async Task ThrowingAsync()"));
    }

    // Mirrors StackTraceResolutionTests.ContextLinesOutOfRange_ReturnsInvalidParameter.
    [Fact]
    public async Task ResolveStackTrace_ContextLinesOutOfRange_ReturnsInvalidParameter()
    {
        var error = await CallAndGetErrorAsync("resolve_stack_trace",
            new { stackTrace = "at X.Y()", contextLines = 21 }, codeContains: ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>()!.Should().Contain("between 0 and 20");
    }

    // Mirrors TestStubTests.SyncMethod_DetectsXunit_AndStubCompiles.
    [Fact]
    public async Task GenerateTestStub_SyncMethod_DetectsXunitAndEmitsFactStub()
    {
        var data = await CallAndGetDataAsync("generate_test_stub",
            new { typeName = "TestStubTarget", methodName = "Add" });
        data["framework"]!.Value<string>().Should().Be("xunit");
        data["frameworkDetected"]!.Value<bool>().Should().BeTrue();
        data["testClassName"]!.Value<string>().Should().Be("TestStubTargetTests");
        var code = data["testCode"]!.Value<string>()!;
        code.Should().Contain("[Fact]");
        code.Should().Contain("public void Add_Behavior()");
        code.Should().Contain("var sut = new global::SharpLensMcp.Tests.Fixtures.TestStubTarget();");
    }

    // Mirrors TestStubTests.UnknownMethod_PropagatesSymbolNotFound.
    [Fact]
    public async Task GenerateTestStub_UnknownMethod_ReturnsSymbolNotFound()
    {
        await CallAndGetErrorAsync("generate_test_stub",
            new { typeName = "TestStubTarget", methodName = "NoSuchMethod" }, codeContains: ErrorCodes.SymbolNotFound);
    }

    // Mirrors DiffApiSurfaceToolTests.DiffAgainstHead_SrcProject_ReportsNoBreakingChanges.
    [Fact]
    public async Task DiffApiSurface_AgainstHead_SrcProject_ReportsNoBreakingChanges()
    {
        var data = await CallAndGetDataAsync("diff_api_surface",
            new { baselineRef = "HEAD", projectName = "SharpLensMcp" });
        data["baselineRef"]!.Value<string>().Should().Be("HEAD");
        var compared = (data["projectsCompared"] as JArray)!;
        compared.Should().ContainSingle();
        compared[0]["project"]!.Value<string>().Should().Be("SharpLensMcp");
        data["breakingCount"]!.Value<int>().Should().Be(0,
            "no public API at HEAD may be removed or changed");
        (data["breaking"] as JArray)!.Should().BeEmpty();
    }

    // Mirrors DiffApiSurfaceToolTests.UnknownBaselineRef_ReturnsInvalidParameter.
    [Fact]
    public async Task DiffApiSurface_UnknownBaselineRef_ReturnsInvalidParameter()
    {
        var error = await CallAndGetErrorAsync("diff_api_surface",
            new { baselineRef = "definitely-not-a-ref-xyz", projectName = "SharpLensMcp" },
            codeContains: ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>()!.Should().Contain("definitely-not-a-ref-xyz");
    }

    // Mirrors DiscoveryToolTests.GetGeneratedCode_WithNonExistentFile_ReturnsFileNotFound.
    [Fact]
    public async Task GetGeneratedCode_NonExistentFile_ReturnsFileNotFound()
    {
        await CallAndGetErrorAsync("get_generated_code",
            new { projectName = "SharpLensMcp", generatedFileName = "NonExistentFile.g.cs" },
            codeContains: ErrorCodes.FileNotFound);
    }

    // Mirrors TypeReorganizationTests.MoveTypeToFile_ComputesBothFileContents_WithoutTouchingDisk.
    [Fact]
    public async Task MoveTypeToFile_ComputesBothContents_GenerationOnly()
    {
        var data = await CallAndGetDataAsync("move_type_to_file", new { typeName = "MoveCandidateBeta" });
        data["appliesEditsAutomatically"]!.Value<bool>().Should().BeFalse(
            "the tool is generation-only and must say so");
        data["newFilePath"]!.Value<string>()!.Should().EndWith("MoveCandidateBeta.cs");
        data["newFileContent"]!.Value<string>()!.Should().Contain("class MoveCandidateBeta");
        var updatedOrigin = data["updatedOriginContent"]!.Value<string>()!;
        updatedOrigin.Should().NotContain("class MoveCandidateBeta");
        updatedOrigin.Should().Contain("class MoveCandidateAlpha");
    }

    // Mirrors TypeReorganizationTests.MoveTypeToFile_NestedType_ReturnsInvalidParameter.
    [Fact]
    public async Task MoveTypeToFile_NestedType_ReturnsInvalidParameter()
    {
        await CallAndGetErrorAsync("move_type_to_file",
            new { typeName = "NestedHelper" }, codeContains: ErrorCodes.InvalidParameter);
    }

    // Mirrors TypeReorganizationTests.SplitType_ComputesPartialHalves_WithoutTouchingDisk.
    [Fact]
    public async Task SplitType_ComputesPartialHalves_GenerationOnly()
    {
        var data = await CallAndGetDataAsync("split_type",
            new { typeName = "SplitCandidate", memberNames = new[] { "MoveMe1", "MoveMe2" } });
        data["appliesEditsAutomatically"]!.Value<bool>().Should().BeFalse();
        data["newFilePath"]!.Value<string>()!.Should().EndWith("SplitCandidate.Partial.cs");
        (data["movedMembers"] as JArray)!.Select(m => m["name"]!.Value<string>())
            .Should().BeEquivalentTo("MoveMe1", "MoveMe2");
        var newFileContent = data["newFileContent"]!.Value<string>()!;
        newFileContent.Should().Contain("partial class SplitCandidate");
        newFileContent.Should().Contain("MoveMe1");
        data["updatedOriginContent"]!.Value<string>()!.Should().NotContain("MoveMe1");
    }

    // Mirrors TypeReorganizationTests.SplitType_UnknownMemberName_ReturnsSymbolNotFound.
    [Fact]
    public async Task SplitType_UnknownMemberName_ReturnsSymbolNotFound()
    {
        var error = await CallAndGetErrorAsync("split_type",
            new { typeName = "SplitCandidate", memberNames = new[] { "MoveMe1", "NoSuchMember" } },
            codeContains: ErrorCodes.SymbolNotFound);
        error["message"]!.Value<string>()!.Should().Contain("NoSuchMember");
    }

    // Mirrors UnusedRemovalToolTests.ComputesRemovalsAndNewlyUnusedUsing_WithoutTouchingDisk.
    [Fact]
    public async Task RemoveUnusedCode_ComputesRemovals_GenerationOnly()
    {
        var data = await CallAndGetDataAsync("remove_unused_code", new
        {
            projectName = "SharpLensMcp.Tests",
            symbolNames = new[] { "DeadHelperOne", "DeadHelperTwo", "UnusedRemovalGhost" }
        });
        data["appliesEditsAutomatically"]!.Value<bool>().Should().BeFalse();
        (data["removed"] as JArray)!.Select(r => r["name"]!.Value<string>())
            .Should().BeEquivalentTo("DeadHelperOne", "DeadHelperTwo", "UnusedRemovalGhost");
        var files = (data["files"] as JArray)!;
        files.Should().ContainSingle();
        var content = files[0]["updatedContent"]!.Value<string>()!;
        content.Should().NotContain("DeadHelperOne");
        content.Should().NotContain("UnusedRemovalGhost");
    }

    // Mirrors CodeActionTests.EncapsulateField_OnBareCounter... (preview path).
    [Fact]
    public async Task EncapsulateField_OnBareCounter_PreviewsPropertyWrapping()
    {
        var (file, line, col) = await LocateSymbolAsync("BareCounter", kind: "Field");
        var data = await CallAndGetDataAsync("encapsulate_field",
            new { filePath = file, line, column = col, preview = true });
        data["preview"]!.Value<bool>().Should().BeTrue();
        data["actionTitle"]!.Value<string>()!.Should().Contain("Encapsulate");
        var newText = (data["changedFiles"] as JArray)![0]["newText"]!.Value<string>()!;
        newText.Should().Contain("private int bareCounter");
        newText.Should().Contain("public int BareCounter");
    }

    // Mirrors CodeActionTests.InlineVariable_OnTempInGreetingFor... (preview path).
    [Fact]
    public async Task InlineVariable_OnTempInGreetingFor_PreviewsInlinedLiteral()
    {
        var (file, _, _) = await LocateSymbolAsync("GreetingFor", kind: "Method");
        var lines = File.ReadAllLines(file);
        var tempLine = Array.FindIndex(lines, l => l.Contains("var temp = "));
        tempLine.Should().BeGreaterThan(-1);
        var tempCol = lines[tempLine].IndexOf("temp", StringComparison.Ordinal);

        var data = await CallAndGetDataAsync("inline_variable",
            new { filePath = file, line = tempLine, column = tempCol, preview = true });
        data["preview"]!.Value<bool>().Should().BeTrue();
        data["actionTitle"]!.Value<string>()!.Should().Contain("Inline");
        var newText = (data["changedFiles"] as JArray)![0]["newText"]!.Value<string>()!;
        newText.Should().Contain("return \"Hello, \" + name");
        newText.Should().NotContain("var temp = ");
    }

    // Mirrors CodeActionTests.ExtractVariable_OnExpressionInCompute... (preview path).
    [Fact]
    public async Task ExtractVariable_OnExpressionInCompute_PreviewsIntroducedLocal()
    {
        var (file, _, _) = await LocateSymbolAsync("Compute", kind: "Method",
            r => r["containingType"]?.Value<string>()?.Contains("RefactoringTarget") == true);
        var lines = File.ReadAllLines(file);
        var returnLine = Array.FindIndex(lines, l => l.Contains("return a * 2 + 7;"));
        returnLine.Should().BeGreaterThan(-1);
        var exprStart = lines[returnLine].IndexOf("a * 2 + 7", StringComparison.Ordinal);
        var exprEnd = exprStart + "a * 2 + 7".Length;

        var data = await CallAndGetDataAsync("extract_variable", new
        {
            filePath = file, line = returnLine, column = exprStart,
            endLine = returnLine, endColumn = exprEnd, preview = true
        });
        data["preview"]!.Value<bool>().Should().BeTrue();
        var newText = (data["changedFiles"] as JArray)![0]["newText"]!.Value<string>()!;
        newText.Should().Contain("a * 2 + 7");
    }

    // Mirrors CodeActionTests.EncapsulateField_AtUsingsLine_ReturnsSymbolNotFound.
    [Fact]
    public async Task EncapsulateField_AtUsingsLine_ReturnsSymbolNotFound()
    {
        await CallAndGetErrorAsync("encapsulate_field",
            new { filePath = Fixture.RoslynServicePath, line = 10, column = 10, preview = true },
            codeContains: ErrorCodes.SymbolNotFound);
    }

    // The tools below are unit-tested against in-memory workspaces whose inputs
    // (deliberate naming violations / dead branches / unresolved names / missing
    // members / C# 14 extension blocks) cannot exist in the shared, clean,
    // SDK-8-compiled solution without breaking it. Their error paths, however,
    // are deterministic through the wire — dispatch, param binding, and the
    // error envelope are exercised here. (Mirrors each tool's *_ReturnsXxx unit test.)

    [Fact]
    public async Task FindNamingViolations_MaxResultsOutOfRange_ReturnsInvalidParameter()
    {
        await CallAndGetErrorAsync("find_naming_violations",
            new { maxResults = 0 }, codeContains: ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task GetExtensionMethods_UnknownType_ReturnsTypeNotFound()
    {
        await CallAndGetErrorAsync("get_extension_methods",
            new { typeName = "NoSuchTypeAnywhere" }, codeContains: ErrorCodes.TypeNotFound);
    }

    [Fact]
    public async Task GetDocumentation_UnknownType_ReturnsTypeNotFound()
    {
        await CallAndGetErrorAsync("get_documentation",
            new { typeName = "NoSuchTypeAnywhere" }, codeContains: ErrorCodes.TypeNotFound);
    }

    [Fact]
    public async Task ImplementMissingMembers_AtUsingsLine_ReturnsSymbolNotFound()
    {
        await CallAndGetErrorAsync("implement_missing_members",
            new { filePath = Fixture.RoslynServicePath, line = 10, column = 10, preview = true },
            codeContains: ErrorCodes.SymbolNotFound);
    }

    [Fact]
    public async Task FindDeadBranches_NeitherScope_ReturnsInvalidParameter()
    {
        await CallAndGetErrorAsync("find_dead_branches",
            new { }, codeContains: ErrorCodes.InvalidParameter);
    }

    // find_dead_branches has no SUCCESS wire test: an on-disk fixture with
    // unreachable statements would itself trip the build's clean-warnings
    // invariant, so its positive path is covered by the in-memory DeadBranchTests.

    [Fact]
    public async Task FindNamingViolations_OnTestProject_Succeeds()
    {
        var data = await CallAndGetDataAsync("find_naming_violations",
            new { projectName = "SharpLensMcp.Tests" });
        // The repo declares no dotnet_naming_* rules, so the conventional defaults apply.
        data["rulesSource"]!.Value<string>().Should().Be("defaults",
            "no .editorconfig naming rules are configured, so the fallback conventions are used");
        (data["violations"] as JArray).Should().NotBeNull("the response always carries a violations array");
    }

    [Fact]
    public async Task GetExtensionMethods_OnAKnownType_Succeeds()
    {
        // RoslynService resolves to its fully-qualified name, and no solution source
        // declares an extension method targeting it (extensions live only in test
        // fixtures), so the groups array is present and empty.
        var data = await CallAndGetDataAsync("get_extension_methods", new { typeName = "RoslynService" });
        data["typeName"]!.Value<string>().Should().Be("SharpLensMcp.RoslynService");
        (data["groups"] as JArray)!.Should().BeEmpty(
            "no extension method in the solution source targets RoslynService");
    }

    [Fact]
    public async Task GetDocumentation_OnADocumentedMethod_Succeeds()
    {
        // ValidateCodeAsync carries an XML <summary> doc comment in source.
        var data = await CallAndGetDataAsync("get_documentation",
            new { typeName = "RoslynService", memberName = "ValidateCodeAsync" });
        data["documentation"].Should().NotBeNull("a documented member must surface its structured docs");
        data["documentation"]!["summary"]!.Value<string>().Should().Be(
            "Validates if code would compile without writing to disk.",
            "the structured summary must round-trip ValidateCodeAsync's exact <summary> text");
    }

    // get_code_fixes / apply_code_fix / fix_all / add_missing_imports need a real
    // diagnostic at a position, which the clean shared solution doesn't carry; their
    // file-not-in-solution resolution path is deterministic through the wire.
    private static string NotInSolutionPath =>
        Path.Combine(Path.GetTempPath(), "not-in-solution-wire-probe.cs");

    [Fact]
    public async Task AddMissingImports_FileNotInSolution_ReturnsError()
    {
        await CallAndGetErrorAsync("add_missing_imports",
            new { filePath = NotInSolutionPath }, codeContains: ErrorCodes.FileNotInSolution);
    }

    [Fact]
    public async Task GetCodeFixes_FileNotInSolution_ReturnsError()
    {
        await CallAndGetErrorAsync("get_code_fixes",
            new { filePath = NotInSolutionPath, diagnosticId = "CS0103", line = 0, column = 0 },
            codeContains: ErrorCodes.FileNotInSolution);
    }

    [Fact]
    public async Task ApplyCodeFix_FileNotInSolution_ReturnsError()
    {
        await CallAndGetErrorAsync("apply_code_fix",
            new { filePath = NotInSolutionPath, diagnosticId = "CS0103", line = 0, column = 0, preview = true },
            codeContains: ErrorCodes.FileNotInSolution);
    }

    [Fact]
    public async Task FixAll_FileScope_FileNotInSolution_ReturnsError()
    {
        // file scope + a not-in-solution path reaches path resolution and returns
        // FileNotInSolution. (The previous form passed scope="document", an INVALID
        // scope, so it asserted INVALID_PARAMETER and never exercised the path it named.)
        await CallAndGetErrorAsync("fix_all",
            new { filePath = NotInSolutionPath, diagnosticId = "CS0219", scope = "file" },
            codeContains: ErrorCodes.FileNotInSolution);
    }

    [Fact]
    public async Task FixAll_InvalidScope_ReturnsInvalidParameter()
    {
        // An unrecognized scope is rejected before any path/diagnostic work.
        await CallAndGetErrorAsync("fix_all",
            new { filePath = NotInSolutionPath, diagnosticId = "CS0219", scope = "document" },
            codeContains: ErrorCodes.InvalidParameter);
    }
}
