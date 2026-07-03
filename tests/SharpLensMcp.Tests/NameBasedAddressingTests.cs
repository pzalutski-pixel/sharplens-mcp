using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Name-based addressing (typeName/memberName/overloadIndex as the alternative
// to filePath/line/column) across the six tools that accept it, plus the
// shared resolution helpers in RoslynService.Resolution.cs. The fixture types
// are referenced by NAME STRINGS ONLY in this file — using the types in test
// code would add real references and change the rename previews' change counts.
public class NameBasedAddressingTests : RoslynServiceTestBase
{
    [Fact]
    public async Task RenameByName_PreviewMatchesPositionalPreview()
    {
        var searchResult = await Service.SearchSymbolsAsync("UniqueComputeValue", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty("UniqueComputeValue is declared on NameAddressingTarget");
        // The query also substring-matches CallUniqueComputeValue — pick the exact symbol.
        var loc = symbols!.First(s => s["name"]!.Value<string>() == "UniqueComputeValue")["location"]!;

        var positional = await Service.RenameSymbolAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            newName: "RenamedComputeValue",
            preview: true,
            verbosity: "full");

        var byName = await Service.RenameSymbolAsync(
            null, null, null,
            newName: "RenamedComputeValue",
            preview: true,
            verbosity: "full",
            typeName: "NameAddressingTarget",
            memberName: "UniqueComputeValue");

        var positionalData = GetData(positional);
        var byNameData = GetData(byName);

        byNameData["symbolName"]!.Value<string>().Should().Be("UniqueComputeValue");
        byNameData["symbolKind"]!.Value<string>().Should().Be("Method");

        // The fixture declares the method and calls it once from the same file —
        // exactly one changed file. The diff may merge the two nearby edits into
        // one text-change chunk, so count the renamed occurrences in the emitted
        // newText instead of the chunk count: declaration + call site = 2.
        var changes = byNameData["changes"] as JArray;
        changes.Should().NotBeNull();
        changes!.Count.Should().Be(1, "declaration and call site live in the one fixture file");
        changes[0]["filePath"]!.Value<string>().Should().EndWith("NameAddressingFixture.cs");
        var renamedOccurrences = changes[0]["changes"]!
            .Sum(c => CountOccurrences(c["newText"]!.Value<string>()!, "RenamedComputeValue"));
        renamedOccurrences.Should().Be(2,
            "the rename hits the declaration and the CallUniqueComputeValue call site");

        JToken.DeepEquals(positionalData["changes"], byNameData["changes"]).Should().BeTrue(
            "name-based addressing must resolve to the same symbol the positional call targets");
        positionalData["symbolName"]!.Value<string>().Should().Be(
            byNameData["symbolName"]!.Value<string>());
    }

    [Fact]
    public async Task RenameByName_TypeOnly_TargetsTheTypeItself()
    {
        var result = await Service.RenameSymbolAsync(
            null, null, null,
            newName: "RenamedAddressingTarget",
            preview: true,
            typeName: "NameAddressingTarget");

        var data = GetData(result);
        data["symbolName"]!.Value<string>().Should().Be("NameAddressingTarget");
        data["symbolKind"]!.Value<string>().Should().Be("NamedType",
            "with no memberName the typeName addresses the type declaration");
    }

    [Fact]
    public async Task RenameByName_OverloadedMemberWithoutIndex_ReturnsAmbiguousMatchWithCandidates()
    {
        var result = await Service.RenameSymbolAsync(
            null, null, null,
            newName: "DescribeRenamed",
            preview: true,
            typeName: "NameAddressingTarget",
            memberName: "Describe");

        AssertError(result, ErrorCodes.AmbiguousMatch);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("2 overloads");

        var candidates = json["error"]!["Context"]!["candidates"] as JArray;
        candidates.Should().NotBeNull("the ambiguity error must list the candidates to pick from");
        candidates!.Count.Should().Be(2);
        candidates[0]["overloadIndex"]!.Value<int>().Should().Be(0);
        candidates[0]["signature"]!.Value<string>().Should().Contain("Describe()");
        candidates[1]["overloadIndex"]!.Value<int>().Should().Be(1);
        candidates[1]["signature"]!.Value<string>().Should().Contain("Describe(int)");
    }

    [Fact]
    public async Task RenameByName_OverloadIndexOutOfRange_ReturnsInvalidParameter()
    {
        var result = await Service.RenameSymbolAsync(
            null, null, null,
            newName: "DescribeRenamed",
            preview: true,
            typeName: "NameAddressingTarget",
            memberName: "Describe",
            overloadIndex: 5);

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("Invalid overloadIndex 5");
        json["error"]!["Message"]!.Value<string>().Should().Contain("0 to 1");
    }

    [Fact]
    public async Task ResolveSymbolByName_OverloadIndexSelectsExactOverload()
    {
        var (parameterless, error0) = await Service.ResolveSymbolByNameAsync(
            "NameAddressingTarget", "Describe", overloadIndex: 0);
        error0.Should().BeNull();
        parameterless!.ToDisplayString().Should().Be("SharpLensMcp.Tests.Fixtures.NameAddressingTarget.Describe()");

        var (withInt, error1) = await Service.ResolveSymbolByNameAsync(
            "NameAddressingTarget", "Describe", overloadIndex: 1);
        error1.Should().BeNull();
        withInt!.ToDisplayString().Should().Be("SharpLensMcp.Tests.Fixtures.NameAddressingTarget.Describe(int)");
    }

    [Fact]
    public async Task ResolveSymbolByName_ConstructorViaTypeSimpleName()
    {
        // ChangeSignatureCtorHolder declares two constructors; addressing them by
        // the type's simple name must demand an overloadIndex and then select by
        // declaration order: (int, string) first, (int) second.
        var (_, ambiguous) = await Service.ResolveSymbolByNameAsync(
            "ChangeSignatureCtorHolder", "ChangeSignatureCtorHolder");
        ambiguous.Should().NotBeNull("two constructors require an explicit overloadIndex");
        var json = JObject.FromObject(ambiguous!);
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousMatch);

        var (primary, error) = await Service.ResolveSymbolByNameAsync(
            "ChangeSignatureCtorHolder", ".ctor", overloadIndex: 0);
        error.Should().BeNull();
        primary!.ToDisplayString().Should().Be(
            "SharpLensMcp.Tests.Fixtures.ChangeSignatureCtorHolder.ChangeSignatureCtorHolder(int, string)");
    }

    [Fact]
    public async Task BothAddressingModes_ReturnsInvalidParameter()
    {
        var result = await Service.RenameSymbolAsync(
            RoslynServicePath, 0, 0,
            newName: "Whatever",
            preview: true,
            typeName: "NameAddressingTarget");

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("Both addressing modes");
    }

    [Fact]
    public async Task NeitherAddressingMode_ReturnsInvalidParameter()
    {
        var result = await Service.RenameSymbolAsync(
            null, null, null,
            newName: "Whatever",
            preview: true);

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("No addressing was supplied");
    }

    [Fact]
    public async Task PartialPositionalAddressing_ReturnsInvalidParameter()
    {
        var result = await Service.RenameSymbolAsync(
            RoslynServicePath, null, null,
            newName: "Whatever",
            preview: true);

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain(
            "requires all of filePath, line, and column");
    }

    [Fact]
    public async Task MemberNameWithoutTypeName_ReturnsInvalidParameter()
    {
        var result = await Service.RenameSymbolAsync(
            null, null, null,
            newName: "Whatever",
            preview: true,
            memberName: "Describe");

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("require typeName");
    }

    [Fact]
    public async Task UnknownTypeName_ReturnsTypeNotFound()
    {
        var result = await Service.RenameSymbolAsync(
            null, null, null,
            newName: "Whatever",
            preview: true,
            typeName: "NoSuchTypeAnywhereInThisSolution");

        AssertError(result, ErrorCodes.TypeNotFound);
    }

    [Fact]
    public async Task AddNullChecksByName_GuardsTheNamedMethod()
    {
        var result = await Service.AddNullChecksAsync(
            null, null, null,
            preview: true,
            typeName: "AddNullChecksTarget",
            memberName: "Process");

        var data = GetData(result);
        data["methodName"]!.Value<string>().Should().Be("Process");
        var guarded = (data["parametersWithNullChecks"] as JArray)!.Select(t => t.Value<string>()).ToList();
        guarded.Should().Equal(new[] { "input" },
            "Process(string input) has exactly one unguarded reference parameter");
        data["generatedCode"]!.Value<string>().Should().Contain("ArgumentNullException.ThrowIfNull(input);");
    }

    [Fact]
    public async Task AddNullChecksByName_WithoutMemberName_ReturnsInvalidParameter()
    {
        var result = await Service.AddNullChecksAsync(
            null, null, null,
            preview: true,
            typeName: "AddNullChecksTarget");

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("requires memberName");
    }

    [Fact]
    public async Task GenerateEqualityMembersByTypeName_ComparesTheFixtureField()
    {
        var result = await Service.GenerateEqualityMembersAsync(
            null, null, null,
            preview: true,
            typeName: "EqualityMembersTarget");

        var data = GetData(result);
        data["typeName"]!.Value<string>().Should().Be("EqualityMembersTarget");
        var compared = (data["membersCompared"] as JArray)!.Select(t => t.Value<string>()).ToList();
        compared.Should().Equal(new[] { "Value" },
            "EqualityMembersTarget declares exactly one instance field");
        data["generatedCode"]!.Value<string>().Should().Contain("hash.Add(Value);");
    }

    [Fact]
    public async Task ExtractInterfaceByTypeName_ListsAllPublicMembers()
    {
        var result = await Service.ExtractInterfaceAsync(
            null, null, null,
            interfaceName: "INameAddressingTarget",
            includeMemberNames: null,
            typeName: "NameAddressingTarget");

        var data = GetData(result);
        data["className"]!.Value<string>().Should().Be("NameAddressingTarget");
        var members = data["members"] as JArray;
        members.Should().NotBeNull();
        members!.Count.Should().Be(4,
            "NameAddressingTarget has UniqueComputeValue, CallUniqueComputeValue, and two Describe overloads");
        data["interfaceCode"]!.Value<string>().Should().Contain("UniqueComputeValue");
    }

    [Fact]
    public async Task GenerateConstructorByTypeName_EmitsPropertyParameters()
    {
        var result = await Service.GenerateConstructorAsync(
            null, null, null,
            includeProperties: true,
            typeName: "GenCtorWithProperties");

        var data = GetData(result);
        data["constructorCode"]!.Value<string>().Should().Contain(
            "GenCtorWithProperties(string name, int age)",
            "includeProperties=true turns the two settable properties into camelCase parameters");
    }

    [Fact]
    public async Task ChangeSignatureByName_PreviewReportsTheFixtureCallSites()
    {
        var result = await Service.ChangeSignatureAsync(
            null, null, null,
            changes: new List<SignatureChange>(),
            preview: true,
            typeName: "ChangeSignatureMethodHolder",
            memberName: "Concat");

        var data = GetData(result);
        data["methodName"]!.Value<string>().Should().Be("Concat");
        data["oldSignature"]!.Value<string>().Should().Be("int Concat(int first, int second, int third)");
        data["callSitesCount"]!.Value<int>().Should().Be(5,
            "ChangeSignatureFixture invokes Concat five times across its holder methods");
    }

    [Fact]
    public async Task ChangeSignatureByName_WithoutMemberName_ReturnsInvalidParameter()
    {
        var result = await Service.ChangeSignatureAsync(
            null, null, null,
            changes: new List<SignatureChange>(),
            preview: true,
            typeName: "ChangeSignatureMethodHolder");

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("requires memberName");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
