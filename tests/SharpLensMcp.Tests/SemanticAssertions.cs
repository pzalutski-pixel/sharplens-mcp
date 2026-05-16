using FluentAssertions;
using Newtonsoft.Json.Linq;

namespace SharpLensMcp.Tests;

// Exact-content assertions for tool responses. Tests should reach for these
// instead of writing ad-hoc `.Should().NotBeNull()` checks that pass on empty.
internal static class SemanticAssertions
{
    public static JToken AssertSymbolFound(JArray results, string expectedName, string? expectedKind = null)
    {
        results.Should().NotBeNullOrEmpty(
            $"expected at least one result for symbol '{expectedName}'");

        var match = results.FirstOrDefault(r =>
            r["name"]?.Value<string>() == expectedName ||
            r["fullyQualifiedName"]?.Value<string>()?.EndsWith("." + expectedName) == true);

        match.Should().NotBeNull(
            $"expected '{expectedName}' in results but got: {string.Join(", ", results.Select(r => r["name"]?.Value<string>()))}");

        if (expectedKind != null)
        {
            match!["kind"]?.Value<string>().Should().Be(expectedKind,
                $"expected '{expectedName}' to be classified as '{expectedKind}'");
        }

        return match!;
    }

    public static void AssertReferencesInclude(JArray refs, string filePathSuffix, int line)
    {
        refs.Should().NotBeNullOrEmpty("expected at least one reference");

        var match = refs.FirstOrDefault(r =>
            r["filePath"]?.Value<string>()?.Replace('\\', '/').EndsWith(filePathSuffix.Replace('\\', '/')) == true &&
            r["line"]?.Value<int>() == line);

        match.Should().NotBeNull(
            $"expected a reference at line {line} of '{filePathSuffix}' but got: " +
            string.Join("; ", refs.Select(r => $"{r["filePath"]}:{r["line"]}")));
    }

    public static void AssertReferenceKindsInclude(JArray refs, params string[] expectedKinds)
    {
        refs.Should().NotBeNullOrEmpty();
        var seen = refs.Select(r => r["kind"]?.Value<string>()).Where(k => k != null).Distinct().ToHashSet();
        foreach (var kind in expectedKinds)
        {
            seen.Should().Contain(kind,
                $"expected at least one reference of kind '{kind}', got: {string.Join(", ", seen)}");
        }
    }

    public static void AssertPreviewContains(JToken data, string substring)
    {
        data.Should().NotBeNull();
        var serialized = data.ToString();
        serialized.Should().Contain(substring,
            $"expected preview output to contain '{substring}' but got: {serialized}");
    }

    public static JToken AssertMemberFound(JArray members, string expectedName, string? expectedKind = null)
    {
        members.Should().NotBeNullOrEmpty(
            $"expected at least one member named '{expectedName}'");

        var match = members.FirstOrDefault(m => m["name"]?.Value<string>() == expectedName);
        match.Should().NotBeNull($"expected member '{expectedName}' in result");

        if (expectedKind != null)
        {
            match!["kind"]?.Value<string>().Should().Be(expectedKind);
        }

        return match!;
    }
}
