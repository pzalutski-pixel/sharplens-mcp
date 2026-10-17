using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// By-name type resolution accepts every spelling a caller sees for a type: the C#
// display name the tools print (Lib.OuterA.Builder, Lib.Repo<T>), the metadata name
// (Lib.OuterA+Builder, Lib.Repo`1), a dotted suffix of either (OuterA.Builder,
// Repo<T>), a global:: prefix, concrete type arguments (Repo<int> → Repo<T>) and the
// C# keyword aliases (int, object). Every AMBIGUOUS_TYPE candidate is itself a
// spelling that resolves. Exercised through get_type_members and
// check_type_compatibility, which resolve via FindTypeByNameAsync.
public class TypeNameSpellingTests
{
    internal const string Code = @"
namespace Lib
{
    public class OuterA { public class Builder { public void Make() { } } }
    public class OuterB { public class Builder { public void Make() { } } }
    public class Repo<T> { public T? Get() => default; }
    public class Repo { public void Get() { } }
    public class Service { }
}";

    private static RoslynService CreateService()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Nested.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    private static async Task<string> ResolvedTypeNameAsync(RoslynService service, string typeName)
    {
        var json = JObject.FromObject(await service.GetTypeMembersAsync(typeName));
        json["success"]!.Value<bool>().Should().BeTrue($"'{typeName}' must resolve; got {json}");
        return json["data"]!["typeName"]!.Value<string>()!;
    }

    [Theory]
    [InlineData("Lib.OuterA.Builder", "Lib.OuterA.Builder")]
    [InlineData("OuterA.Builder", "Lib.OuterA.Builder")]
    [InlineData("Lib.OuterA+Builder", "Lib.OuterA.Builder")]
    [InlineData("global::Lib.OuterB.Builder", "Lib.OuterB.Builder")]
    [InlineData("Lib.Repo<T>", "Lib.Repo<T>")]
    [InlineData("Repo<T>", "Lib.Repo<T>")]
    [InlineData("Lib.Repo`1", "Lib.Repo<T>")]
    [InlineData("Lib.Repo<int>", "Lib.Repo<T>")]
    [InlineData("Lib.Repo", "Lib.Repo")]
    [InlineData("System.Collections.Generic.List<T>", "System.Collections.Generic.List<T>")]
    [InlineData("System.Collections.Generic.Dictionary<,>", "System.Collections.Generic.Dictionary<TKey, TValue>")]
    [InlineData("string", "string")]
    [InlineData("object", "object")]
    public async Task Spelling_ResolvesToTheType(string typeName, string expected)
    {
        (await ResolvedTypeNameAsync(CreateService(), typeName)).Should().Be(expected);
    }

    [Theory]
    [InlineData("Builder", new[] { "Lib.OuterA.Builder", "Lib.OuterB.Builder" })]
    [InlineData("Repo", new[] { "Lib.Repo", "Lib.Repo<T>" })]
    public async Task AmbiguousName_ListsCandidatesThatEachResolve(string typeName, string[] expected)
    {
        var service = CreateService();
        var json = JObject.FromObject(await service.GetTypeMembersAsync(typeName));
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousType);
        var candidates = (json["error"]!["Context"]!["candidates"] as JArray)!
            .Select(c => c.Value<string>()!)
            .ToList();
        candidates.Should().Equal(expected);

        foreach (var candidate in candidates)
        {
            (await ResolvedTypeNameAsync(service, candidate)).Should().Be(candidate,
                "the AMBIGUOUS_TYPE hint tells the caller to repeat the call with a candidate");
        }
    }

    [Theory]
    [InlineData("int[]")]
    [InlineData("int?")]
    [InlineData("Lib.Repo<T")]
    public async Task NonTypeNameSpelling_ReturnsTypeNotFound(string typeName)
    {
        var json = JObject.FromObject(await CreateService().GetTypeMembersAsync(typeName));
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.TypeNotFound);
    }

    [Fact]
    public async Task KeywordAliases_ClassifyAsTheirSpecialTypes()
    {
        var service = CreateService();

        var widening = JObject.FromObject(await service.CheckTypeCompatibilityAsync("int", "long"));
        widening["success"]!.Value<bool>().Should().BeTrue(widening.ToString());
        widening["data"]!["sourceType"]!.Value<string>().Should().Be("int");
        widening["data"]!["targetType"]!.Value<string>().Should().Be("long");
        widening["data"]!["conversionKind"]!.Value<string>().Should().Be("ImplicitNumeric");

        var toObject = JObject.FromObject(await service.CheckTypeCompatibilityAsync("Service", "object"));
        toObject["success"]!.Value<bool>().Should().BeTrue(toObject.ToString());
        toObject["data"]!["sourceType"]!.Value<string>().Should().Be("Lib.Service");
        toObject["data"]!["targetType"]!.Value<string>().Should().Be("object");
        toObject["data"]!["conversionKind"]!.Value<string>().Should().Be("ImplicitReference");
    }
}
