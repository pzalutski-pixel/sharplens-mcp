using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// generate_test_stub against TestStubFixture: the stub must COMPILE in the
// test project's context (the strongest content lock a generator can have),
// detect xUnit in this repo, go async for Task-returning targets, and mark
// interface parameters with the substitution TODO.
public class TestStubTests : RoslynServiceTestBase
{
    private string FixturePath => Path.Combine(
        Path.GetDirectoryName(SolutionPath)!, "tests", "SharpLensMcp.Tests", "Fixtures", "TestStubFixture.cs");

    private async Task<JToken> StubFor(string methodName)
    {
        var result = await Service.GenerateTestStubAsync("TestStubTarget", methodName);
        return GetData(result);
    }

    private async Task AssertCompilesInTestProject(string testCode)
    {
        var validation = await Service.ValidateCodeAsync(testCode, contextFilePath: FixturePath);
        var data = GetData(validation);
        data["compiles"]!.Value<bool>().Should().BeTrue(
            $"the generated stub must compile against the test project; errors: {data["errors"]}");
    }

    [Fact]
    public async Task SyncMethod_DetectsXunit_AndStubCompiles()
    {
        var data = await StubFor("Add");

        data["framework"]!.Value<string>().Should().Be("xunit",
            "this repo's test project references xUnit");
        data["frameworkDetected"]!.Value<bool>().Should().BeTrue();
        data["testClassName"]!.Value<string>().Should().Be("TestStubTargetTests");
        data["appliesEditsAutomatically"]!.Value<bool>().Should().BeFalse();
        data["suggestedFilePath"]!.Value<string>().Should().EndWith("TestStubTargetTests.cs");

        var code = data["testCode"]!.Value<string>()!;
        code.Should().Contain("[Fact]");
        code.Should().Contain("public void Add_Behavior()");
        code.Should().Contain("var sut = new global::SharpLensMcp.Tests.Fixtures.TestStubTarget();",
            "the SUT is emitted fully qualified so no using for its namespace is assumed");
        code.Should().Contain("var result = sut.Add(left: default, right: default);");

        await AssertCompilesInTestProject(code);
    }

    [Fact]
    public async Task AsyncMethod_ProducesAsyncTaskTest_AndStubCompiles()
    {
        var data = await StubFor("FetchAsync");

        var code = data["testCode"]!.Value<string>()!;
        code.Should().Contain("public async global::System.Threading.Tasks.Task FetchAsync_Behavior()",
            "the async stub's Task return is fully qualified — the stub never imports System.Threading.Tasks");
        code.Should().Contain("var result = await sut.FetchAsync(key: \"\");");

        await AssertCompilesInTestProject(code);
    }

    [Fact]
    public async Task InterfaceParameter_GetsSubstitutionTodo()
    {
        var data = await StubFor("Validate");

        var code = data["testCode"]!.Value<string>()!;
        code.Should().Contain("input: /* TODO: substitute IComparable */ default!");

        await AssertCompilesInTestProject(code);
    }

    [Fact]
    public async Task RefParameter_DeclaresAWritableLocal_AndStubCompiles()
    {
        var data = await StubFor("Accumulate");

        var code = data["testCode"]!.Value<string>()!;
        code.Should().Contain("int totalRef = default;",
            "a ref parameter needs a writable local to pass by reference");
        code.Should().Contain("sut.Accumulate(total: ref totalRef);");

        await AssertCompilesInTestProject(code);
    }

    [Fact]
    public async Task CustomAwaitableReturn_GoesAsyncAndAwaits_AndStubCompiles()
    {
        var data = await StubFor("DoCustomAsync");

        var code = data["testCode"]!.Value<string>()!;
        code.Should().Contain("public async global::System.Threading.Tasks.Task DoCustomAsync_Behavior()",
            "a custom awaitable return must still produce an async test");
        code.Should().Contain("var result = await sut.DoCustomAsync();",
            "CustomAwaiter.GetResult() returns an int, so awaiting the custom awaitable yields a value that must be captured, not discarded");

        await AssertCompilesInTestProject(code);
    }

    [Fact]
    public async Task KeywordNamedParameters_AreEscapedInNamedArguments_AndStubCompiles()
    {
        var data = await StubFor("Reserved");

        var code = data["testCode"]!.Value<string>()!;
        code.Should().Contain("@class: default",
            "a parameter named with a reserved keyword must be @-escaped in the named argument");
        code.Should().Contain("@event: default");

        await AssertCompilesInTestProject(code);
    }

    [Fact]
    public async Task GenericMethod_EmitsExplicitTypeArguments_AndStubCompiles()
    {
        var data = await StubFor("Echo");

        var code = data["testCode"]!.Value<string>()!;
        code.Should().Contain("sut.Echo<object>(",
            "a generic method needs explicit type arguments at the call site or it won't compile (CS0411)");

        await AssertCompilesInTestProject(code);
    }

    [Fact]
    public async Task GenericConstraintInForeignNamespace_FullyQualifiesTypeArgument_AndStubCompiles()
    {
        var data = await StubFor("Pick");

        var code = data["testCode"]!.Value<string>()!;
        code.Should().Contain("global::SharpLensMcp.Tests.ForeignConstraints.IForeignConstraint",
            "the constraint type lives in an unimported namespace, so the type argument must be fully qualified");

        // The strongest lock: the stub must compile despite the foreign-namespace
        // constraint — a MinimallyQualified name would have failed with CS0246.
        await AssertCompilesInTestProject(code);
    }

    [Fact]
    public async Task RefParameterInForeignNamespace_FullyQualifiesLocal_AndStubCompiles()
    {
        var data = await StubFor("Mutate");

        var code = data["testCode"]!.Value<string>()!;
        code.Should().Contain("global::SharpLensMcp.Tests.ForeignConstraints.IForeignConstraint",
            "the ref-local type lives in an unimported namespace, so it must be fully qualified");

        // The strongest lock: the stub compiles despite the foreign-namespace ref param —
        // a MinimallyQualified ref-local type would have failed with CS0246.
        await AssertCompilesInTestProject(code);
    }

    [Fact]
    public async Task UnknownMethod_PropagatesSymbolNotFound()
    {
        var result = await Service.GenerateTestStubAsync("TestStubTarget", "NoSuchMethod");

        AssertError(result, ErrorCodes.SymbolNotFound);
    }
}
