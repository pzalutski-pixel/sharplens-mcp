using System.Text.Json;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// The published schemas name every value their kind parameters accept, from the same
// vocabularies the tools label results with: get_type_members(_batch) memberKind lists
// every MemberKind, search_symbols kind lists every type kind, member kind and Namespace,
// and semantic_query kinds every type and member kind.
public class KindVocabularySchemaTests
{
    private static readonly string[] TypeKinds = { "Class", "Record", "Struct", "RecordStruct", "Interface", "Enum", "Delegate" };

    private static readonly string[] MemberKinds = MemberClassifier.KindNames.Where(k => k != nameof(MemberKind.NestedType)).ToArray();

    // The words of a parameter's description, so "Record" is found only as a word of its
    // own and never inside "RecordStruct".
    private static HashSet<string> DescriptionWords(string tool, string parameter)
    {
        var schemas = JArray.Parse(JsonSerializer.Serialize(ToolRegistry.BuildToolSchemas(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var description = schemas.Single(s => s["name"]!.Value<string>() == tool)
            ["inputSchema"]!["properties"]![parameter]!["description"]!.Value<string>()!;
        return System.Text.RegularExpressions.Regex.Matches(description, "[A-Za-z]+").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("get_type_members")]
    [InlineData("get_type_members_batch")]
    public void MemberKind_ListsEveryMemberKind(string tool)
    {
        var words = DescriptionWords(tool, "memberKind");

        MemberClassifier.KindNames.Should().OnlyContain(kind => words.Contains(kind));
    }

    [Fact]
    public void SearchSymbolsKind_ListsEveryKind()
    {
        var words = DescriptionWords("search_symbols", "kind");

        TypeKinds.Concat(MemberKinds).Append("Namespace").Should().OnlyContain(kind => words.Contains(kind));
    }

    [Fact]
    public void SemanticQueryKinds_ListsEveryTypeAndMemberKind()
    {
        var words = DescriptionWords("semantic_query", "kinds");

        TypeKinds.Concat(MemberKinds).Should().OnlyContain(kind => words.Contains(kind));
    }
}
