using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// By-name type resolution across source and referenced (framework) types:
//   - a simple name with no exact source match resolves to the exact framework type
//     (the tools' documented examples: InvalidOperationException, IOException,
//     StringBuilder, Object), and beats a merely partial source match (Exception
//     resolves to System.Exception, not App.AppException);
//   - an exact source match wins over a same-named framework type, never silently:
//     the resolution carries the shadowed framework types and a notice;
//   - when the source match is itself ambiguous, the framework type is a candidate;
//   - a partial match never resolves: TYPE_NOT_FOUND lists it as a suggestion.
public class FrameworkTypeResolutionTests
{
    internal const string Code = @"
namespace App
{
    public class AppException : System.Exception { }
    public class Service { }
    public class Timer { }
    public class Thrower
    {
        public void Boom()
        {
            try
            {
                throw new System.InvalidOperationException(""x"");
            }
            catch (System.IO.IOException)
            {
            }
        }

        public void Fail() => throw new AppException();
    }
}";

    internal const string SecondTimer = @"
namespace Other
{
    public class Timer { }
}";

    internal const string TimerNotice =
        "'Timer' resolved to App.Timer; also matches System.Threading.Timer — pass the fully-qualified name to target it.";

    private static RoslynService CreateService(bool withSecondTimer = false)
    {
        var (workspace, _) = withSecondTimer
            ? TestHelpers.CreateWorkspaceWithMultipleDocuments((Code, "App.cs"), (SecondTimer, "Other.cs"))
            : TestHelpers.CreateWorkspaceWithMultipleDocuments((Code, "App.cs"));
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    [Theory]
    [InlineData("InvalidOperationException", "System.InvalidOperationException")]
    [InlineData("IOException", "System.IO.IOException")]
    [InlineData("StringBuilder", "System.Text.StringBuilder")]
    [InlineData("Object", "object")]
    [InlineData("Exception", "System.Exception")]
    public async Task FrameworkSimpleName_ResolvesWhenNoSourceTypeHasIt(string typeName, string expected)
    {
        var resolution = await CreateService().ResolveTypeByNameAsync(typeName);
        resolution.Error.Should().BeNull();
        resolution.Type!.ToDisplayString().Should().Be(expected);
        resolution.ShadowedMatches.Should().BeEmpty();
        resolution.ShadowNotice.Should().BeNull();
    }

    [Fact]
    public async Task BareName_NeverDenotesANestedFrameworkType()
    {
        // Outside its container a nested type is only reachable qualified, so a bare
        // name matches top-level framework types alone (the SDK's nested
        // JSType.Object must not make "Object" ambiguous).
        var service = CreateService();
        var bare = await service.ResolveTypeByNameAsync("Enumerator");
        bare.Type.Should().BeNull();
        bare.Error.Should().BeNull();

        var qualified = await service.ResolveTypeByNameAsync("List<T>.Enumerator");
        qualified.Error.Should().BeNull();
        qualified.Type!.ToDisplayString().Should().Be("System.Collections.Generic.List<T>.Enumerator");
    }

    [Fact]
    public async Task FindThrowSites_InvalidOperationException_FindsTheThrow()
    {
        var json = JObject.FromObject(await CreateService().FindThrowSitesAsync("InvalidOperationException"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["typeName"]!.Value<string>().Should().Be("System.InvalidOperationException");
        ((JArray)json["data"]!["matches"]!).Select(m => m["lineText"]!.Value<string>())
            .Should().Equal("throw new System.InvalidOperationException(\"x\");");
    }

    [Fact]
    public async Task FindCatchBlocks_IOException_FindsTheCatch()
    {
        var json = JObject.FromObject(await CreateService().FindCatchBlocksAsync("IOException"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["typeName"]!.Value<string>().Should().Be("System.IO.IOException");
        ((JArray)json["data"]!["matches"]!).Count.Should().Be(1);
    }

    [Fact]
    public async Task FindThrowSites_Exception_IsTheFrameworkTypeNotThePartialSourceMatch()
    {
        var json = JObject.FromObject(await CreateService().FindThrowSitesAsync("Exception"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["typeName"]!.Value<string>().Should().Be("System.Exception");
        ((JArray)json["data"]!["matches"]!).Select(m => m["lineText"]!.Value<string>())
            .Should().Equal(
                "throw new System.InvalidOperationException(\"x\");",
                "public void Fail() => throw new AppException();");
    }

    [Fact]
    public async Task GetInstantiationOptions_StringBuilder()
    {
        var json = JObject.FromObject(await CreateService().GetInstantiationOptionsAsync("StringBuilder"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["typeName"]!.Value<string>().Should().Be("System.Text.StringBuilder");
    }

    [Fact]
    public async Task SourceExactMatch_WinsAndDisclosesTheShadowedFrameworkType()
    {
        var resolution = await CreateService().ResolveTypeByNameAsync("Timer");
        resolution.Error.Should().BeNull();
        resolution.Type!.ToDisplayString().Should().Be("App.Timer");
        resolution.ShadowedMatches.Should().Equal("System.Threading.Timer");
        resolution.ShadowNotice.Should().Be(TimerNotice);
    }

    [Fact]
    public async Task SourceExactMatch_WithoutFrameworkNamesake_ShadowsNothing()
    {
        var resolution = await CreateService().ResolveTypeByNameAsync("Service");
        resolution.Type!.ToDisplayString().Should().Be("App.Service");
        resolution.ShadowedMatches.Should().BeEmpty();
        resolution.ShadowNotice.Should().BeNull();
    }

    [Fact]
    public async Task FullyQualifiedName_ShadowsNothing()
    {
        var resolution = await CreateService().ResolveTypeByNameAsync("App.Timer");
        resolution.Type!.ToDisplayString().Should().Be("App.Timer");
        resolution.ShadowedMatches.Should().BeEmpty();
    }

    [Fact]
    public async Task AmbiguousSourceMatch_ListsTheFrameworkTypeToo()
    {
        var json = JObject.FromObject(await CreateService(withSecondTimer: true).GetTypeMembersAsync("Timer"));
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousType);
        ((JArray)json["error"]!["Context"]!["candidates"]!).Select(c => c.Value<string>())
            .Should().Equal("App.Timer", "Other.Timer", "System.Threading.Timer");
    }

    [Fact]
    public async Task PartialMatch_NeverResolves_ListsSuggestions()
    {
        var json = JObject.FromObject(await CreateService().GetTypeMembersAsync("Ser"));
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.TypeNotFound);
        json["error"]!["Hint"]!.Value<string>().Should().Be(
            "No type is named 'Ser'. Partial matches: App.Service. Repeat the call with one of them.");
        ((JArray)json["error"]!["Context"]!["suggestions"]!).Select(s => s.Value<string>())
            .Should().Equal("App.Service");
    }

    [Fact]
    public async Task MemberResolution_CarriesTheTypeShadowing()
    {
        var resolution = await CreateService().ResolveMemberByNameAsync("Timer", null, null);
        resolution.Symbol!.ToDisplayString().Should().Be("App.Timer");
        resolution.Type!.ShadowNotice.Should().Be(TimerNotice);
    }

    [Fact]
    public async Task MemberNotFoundOnAShadowingType_ErrorCarriesTheNotice()
    {
        var (_, error) = await CreateService().ResolveSymbolByNameAsync("Timer", "Change");
        var json = JObject.FromObject(error!);
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        json["error"]!["Hint"]!.Value<string>().Should().Be(
            "Use get_type_members to see available members. " + TimerNotice);
        ((JArray)json["error"]!["Context"]!["shadowedTypes"]!).Select(s => s.Value<string>())
            .Should().Equal("System.Threading.Timer");
    }

    [Fact]
    public async Task NameAddress_CarriesTheTypeShadowing()
    {
        var (address, error) = await CreateService().ResolveAddressingAsync(null, null, null, "Timer", null, null);
        error.Should().BeNull();
        address!.Symbol!.ToDisplayString().Should().Be("App.Timer");
        address.TypeResolution!.ShadowNotice.Should().Be(TimerNotice);
    }
}
