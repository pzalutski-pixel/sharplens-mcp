using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;
using static SharpLensMcp.Tests.RefactoringTestKit;

namespace SharpLensMcp.Tests;

// add_missing_imports looks a name up the way the compiler would bind it once the using
// is added: an attribute name also matches its "Attribute"-suffixed type, a missing
// extension method counts only when it applies to the receiver's type, a generic name
// only matches a type of its arity, and a name no new using can fix is reported as
// unresolvable rather than dropped.
public class MissingImportsCandidateTests
{
    private const string ImpSource = """
        namespace R;

        public class Imp
        {
            [Obsolete("x")]
            public int P { get; set; }

            public StringBuilder Sb { get; } = new StringBuilder();

            public int Count5() => 5.Count();
        }

        """;

    [Fact]
    public async Task AttributeNamesAndNonApplicableExtensions_AreClassifiedByHowTheyWouldBind()
    {
        var (service, workspace, document) = Load(ImpSource, "Imp.cs");

        var json = Json(await service.AddMissingImportsAsync(document.FilePath!));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        ((JArray)data["added"]!).Select(a => $"{a["name"]!.Value<string>()} -> {a["namespace"]!.Value<string>()}")
            .Should().Equal("Obsolete -> System", "StringBuilder -> System.Text");
        ((JArray)data["ambiguous"]!).Should().BeEmpty(
            "no namespace has a Count extension that applies to int");
        ((JArray)data["unresolvable"]!).Select(u => u.Value<string>()).Should().Equal("Count");
        var updated = data["updatedContent"]!.Value<string>()!;
        updated.Should().Be("using System;\nusing System.Text;\n" + ImpSource);

        var (_, compiled, _) = Load(updated.Replace("5.Count()", "5"), "Imp.cs");
        (await ErrorsOf(compiled)).Should().BeEmpty();
    }

    private const string SharedNamespaceSource = """
        namespace R;

        public class Texts
        {
            public StringBuilder Make() => new StringBuilder();
            public Encoding Utf8() => Encoding.UTF8;
        }

        """;

    [Fact]
    public async Task NamesSharingOneNamespace_AreAllReported_WithOneUsing()
    {
        var (service, _, document) = Load(SharedNamespaceSource, "Texts.cs");

        var json = Json(await service.AddMissingImportsAsync(document.FilePath!));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        ((JArray)json["data"]!["added"]!).Select(a => $"{a["name"]!.Value<string>()} -> {a["namespace"]!.Value<string>()}")
            .Should().Equal("Encoding -> System.Text", "StringBuilder -> System.Text");
        ((JArray)json["data"]!["unresolvable"]!).Should().BeEmpty();
        var updated = json["data"]!["updatedContent"]!.Value<string>()!;
        updated.Should().Be("using System.Text;\n" + SharedNamespaceSource);
        var (_, compiled, _) = Load(updated, "Texts.cs");
        (await ErrorsOf(compiled)).Should().BeEmpty();
    }

    private const string ArityLib = """
        namespace Lib.One { public class Pair { } }
        namespace Lib.Two { public class Pair<T> { } }
        """;

    private const string AritySource = """
        namespace R;

        public class UsesPair
        {
            public object Make() => new Pair<int>();
        }

        """;

    [Fact]
    public async Task GenericName_MatchesOnlyATypeOfItsArity()
    {
        var (service, _, documents) = LoadMany((ArityLib, "Lib.cs"), (AritySource, "UsesPair.cs"));

        var json = Json(await service.AddMissingImportsAsync(documents[1].FilePath!));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        ((JArray)json["data"]!["added"]!).Select(a => $"{a["name"]!.Value<string>()} -> {a["namespace"]!.Value<string>()}")
            .Should().Equal("Pair -> Lib.Two");
        json["data"]!["updatedContent"]!.Value<string>().Should().Be("using Lib.Two;\n" + AritySource);
    }
}
