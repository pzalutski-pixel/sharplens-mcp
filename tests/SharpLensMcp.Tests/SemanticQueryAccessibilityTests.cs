using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// semantic_query's accessibility filter compares against DeclaredAccessibility's name
// (ProtectedOrInternal / ProtectedAndInternal). The schema documents those exact values,
// and any other value is INVALID_PARAMETER.
public class SemanticQueryAccessibilityTests
{
    private const string Code = @"
namespace Demo
{
    public class Box
    {
        protected internal int Visible() => 1;
        public int Other() => 2;
    }
}";

    private static RoslynService Loaded()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Box.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    private static async Task<List<string>> MethodsWithAccessibility(RoslynService service, string accessibility)
    {
        var result = await service.SemanticQueryAsync(
            kinds: new List<string> { "Method" }, isAsync: null, namespaceFilter: null,
            accessibility: accessibility, isStatic: null, type: null, returnType: null,
            attributes: null, parameterIncludes: null, parameterExcludes: null, maxResults: null);
        return (JObject.FromObject(result)["data"]!["results"] as JArray)!
            .Select(r => r["name"]!.Value<string>()!).ToList();
    }

    [Fact]
    public async Task SchemaDocumentedValue_MatchesProtectedInternalMember()
    {
        var service = Loaded();

        var matched = await MethodsWithAccessibility(service, "ProtectedOrInternal");
        matched.Should().Contain("Visible",
            "the documented ProtectedOrInternal value matches a protected-internal member");
        matched.Should().NotContain("Other", "a public method is not protected-internal");

        // A value outside the documented set is a caller error, never a silent empty result.
        var invalid = await service.SemanticQueryAsync(
            kinds: new List<string> { "Method" }, isAsync: null, namespaceFilter: null,
            accessibility: "ProtectedInternal", isStatic: null, type: null, returnType: null,
            attributes: null, parameterIncludes: null, parameterExcludes: null, maxResults: null);
        var error = JObject.FromObject(invalid)["error"]!;
        error["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        error["Context"]!["parameter"]!.Value<string>().Should().Be("accessibility");
    }
}
