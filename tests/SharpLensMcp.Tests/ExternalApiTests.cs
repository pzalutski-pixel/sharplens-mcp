using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Verifies get_external_type_info against the BCL and the project's source types.
// External-assembly resolution is what closes the AI-hallucination gap for NuGet/BCL APIs.
public class ExternalApiTests : RoslynServiceTestBase
{
    [Fact]
    public async Task GetExternalTypeInfo_OnSystemString_ReturnsFullTypeShapeAndMemberShape()
    {
        var result = await Service.GetExternalTypeInfoAsync("System.String");

        AssertSuccess(result);
        var data = GetData(result);
        data["typeName"]?.Value<string>().Should().Be("string");
        // String lives in System.Private.CoreLib on modern .NET; older runtimes named it differently.
        data["assembly"]?.Value<string>().Should().NotBeNullOrEmpty();
        data["typeKind"]?.Value<string>().Should().Be("Class");
        // String is sealed, not abstract, not static. baseType is object. These flags
        // were never asserted — a regression that drops them would have gone unnoticed.
        data["isSealed"]?.Value<bool>().Should().BeTrue("System.String is sealed");
        data["isAbstract"]?.Value<bool>().Should().BeFalse();
        data["isStatic"]?.Value<bool>().Should().BeFalse();
        data["baseType"]?.Value<string>().Should().Be("object",
            "the impl returns the display string 'object', not 'System.Object'");

        // String implements at least IComparable, IEnumerable<char>, ICloneable, IConvertible.
        // Lock a few well-known interfaces — a regression that drops the interfaces field
        // would otherwise pass.
        var interfaces = data["interfaces"] as JArray;
        interfaces.Should().NotBeNullOrEmpty();
        var interfaceNames = interfaces!.Select(i => i.Value<string>()).ToList();
        interfaceNames.Should().Contain(s => s != null && s.Contains("IComparable"));
        interfaceNames.Should().Contain(s => s != null && s.Contains("IEnumerable"));

        var members = data["members"] as JArray;
        members.Should().NotBeNullOrEmpty();
        var names = members!.Select(m => m["name"]?.Value<string>()).ToList();
        names.Should().Contain("Length");
        names.Should().Contain("Substring");
        names.Should().Contain("Trim");

        // Per-member shape — Length is a public instance property.
        var length = members!.First(m => m["name"]?.Value<string>() == "Length");
        length["kind"]?.Value<string>().Should().Be("Property",
            "Length is a property, not a method or field");
        length["accessibility"]?.Value<string>().Should().Be("Public");
        length["isStatic"]?.Value<bool>().Should().BeFalse();
        length["isObsolete"]?.Value<bool>().Should().BeFalse();
        length["signature"]?.Value<string>().Should().Contain("Length");

        // At least one well-known member should carry a non-empty XML doc summary.
        members!.Any(m => !string.IsNullOrEmpty(m["xmlDoc"]?.Value<string>()))
            .Should().BeTrue("System.String members ship with XML docs in the .NET runtime reference assemblies");
    }

    [Fact]
    public async Task GetExternalTypeInfo_OnUnknownType_ReturnsTypeNotFoundWithFqnHint()
    {
        var result = await Service.GetExternalTypeInfoAsync("System.Garbage.DoesNotExist");
        AssertError(result, ErrorCodes.TypeNotFound);
        // Lock the hint message — the no-assembly-filter branch points the caller
        // at the FQN+backtick-arity convention (ExternalApi.cs:70).
        var json = JObject.FromObject(result);
        json["error"]?["hint"]?.Value<string>().Should().Contain("fully-qualified name",
            "the FQN hint guides callers away from short names like 'String'");
        json["error"]?["hint"]?.Value<string>().Should().Contain("backtick-arity",
            "the hint must mention backtick-arity for generic types");
    }

    [Fact]
    public async Task GetExternalTypeInfo_WithEmptyTypeName_ReturnsInvalidParameter()
    {
        // ExternalApi.cs:21-28: whitespace-or-empty typeName → InvalidParameter.
        var result = await Service.GetExternalTypeInfoAsync("");
        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]?["message"]?.Value<string>().Should().Contain("typeName is required");
    }

    [Fact]
    public async Task GetExternalTypeInfo_WithWrongAssemblyName_ReturnsTypeNotFoundWithAssemblyHint()
    {
        // The hint message differentiates: with assemblyName, mention the specific
        // assembly (ExternalApi.cs:69). Otherwise the generic FQN hint.
        var result = await Service.GetExternalTypeInfoAsync(
            "System.String", assemblyName: "Does.Not.Exist.Assembly");
        AssertError(result, ErrorCodes.TypeNotFound);
        var json = JObject.FromObject(result);
        json["error"]?["hint"]?.Value<string>().Should().Contain("Does.Not.Exist.Assembly",
            "the assembly-specific hint must echo back the bad assembly name");
    }

    [Fact]
    public async Task GetExternalTypeInfo_WithIncludeXmlDocsFalse_OmitsXmlDocFields()
    {
        // includeXmlDocs=false sets xmlDoc to null on the type AND every member
        // (ExternalApi.cs:101, 109). Otherwise the field carries the doc summary.
        var withDocs = await Service.GetExternalTypeInfoAsync(
            "System.String", includeXmlDocs: true);
        var withoutDocs = await Service.GetExternalTypeInfoAsync(
            "System.String", includeXmlDocs: false);

        var dataWith = GetData(withDocs);
        var dataWithout = GetData(withoutDocs);

        // With docs: at least one member has a non-empty xmlDoc.
        var membersWith = (dataWith["members"] as JArray)!;
        membersWith.Any(m => !string.IsNullOrEmpty(m["xmlDoc"]?.Value<string>()))
            .Should().BeTrue("BCL members ship with XML docs");

        // Without docs: every member's xmlDoc is null, and the type-level xmlDoc is null.
        dataWithout["xmlDoc"]?.Type.Should().Be(JTokenType.Null,
            "type-level xmlDoc must be null when includeXmlDocs=false");
        var membersWithout = (dataWithout["members"] as JArray)!;
        foreach (var m in membersWithout)
        {
            m["xmlDoc"]?.Type.Should().Be(JTokenType.Null,
                "every member's xmlDoc must be null when includeXmlDocs=false");
        }
    }

    [Fact]
    public async Task GetExternalTypeInfo_RespectsMaxMembers()
    {
        // List`1 has many public members; max:5 must cap returnedCount while totalCount stays honest.
        var result = await Service.GetExternalTypeInfoAsync(
            "System.Collections.Generic.List`1",
            maxMembers: 5);

        AssertSuccess(result);
        var data = GetData(result);
        var members = data["members"] as JArray;
        members.Should().NotBeNull();
        members!.Count.Should().BeLessOrEqualTo(5, "maxMembers cap must be enforced");

        var meta = JObject.FromObject(result)["meta"];
        meta?["totalCount"]?.Value<int>().Should().BeGreaterThan(5,
            "List<T>'s public surface is much larger than 5; totalCount must reflect the true count");
        meta?["returnedCount"]?.Value<int>().Should().BeLessOrEqualTo(5);
    }

    [Fact]
    public async Task GetExternalTypeInfo_OnSourceType_ResolvesToSharpLensMcpAssembly()
    {
        // The tool name says "external" but resolution should also succeed for source types
        // in the loaded solution. Verifies no source/metadata split in the API and locks
        // that the response carries the source assembly name, not "<unknown>".
        var result = await Service.GetExternalTypeInfoAsync("SharpLensMcp.RoslynService");

        AssertSuccess(result);
        var data = GetData(result);
        data["typeName"]?.Value<string>().Should().EndWith("RoslynService");
        data["assembly"]?.Value<string>().Should().Be("SharpLensMcp",
            "source-type resolution must report the owning project assembly, not <unknown>");
        data["typeKind"]?.Value<string>().Should().Be("Class");
        var members = data["members"] as JArray;
        members.Should().NotBeNullOrEmpty();
        // At least one well-known public method must surface (LoadSolutionAsync, etc.).
        members!.Select(m => m["name"]?.Value<string>()).Should()
            .Contain("LoadSolutionAsync",
                "the source type's public methods must appear");
    }

    [Fact]
    public async Task GetExternalTypeInfo_OnGenericInterface_ReturnsItsMembers()
    {
        var result = await Service.GetExternalTypeInfoAsync("System.Collections.Generic.IEnumerable`1");
        AssertSuccess(result);
        var data = GetData(result);
        data["typeKind"]?.Value<string>().Should().Be("Interface");
        var members = data["members"] as JArray;
        members.Should().NotBeNullOrEmpty();
        members!.Any(m => m["name"]?.Value<string>() == "GetEnumerator")
            .Should().BeTrue("IEnumerable<T> exposes GetEnumerator");
    }

    [Fact]
    public async Task GetExternalTypeInfo_OnBclNestedType_ResolvesViaDottedNameFallback()
    {
        // Nested types in metadata use '+' (Outer+Inner). When the caller passes
        // the natural C# dotted form (Outer.Inner), GetTypeByMetadataName returns
        // null and FindTypeByDottedName walks namespaces + nested types to resolve
        // it (ExternalApi.cs:144-179). System.Environment.SpecialFolder is a
        // public BCL nested enum that exercises this fallback.
        var result = await Service.GetExternalTypeInfoAsync("System.Environment.SpecialFolder");
        AssertSuccess(result);
        var data = GetData(result);
        data["typeName"]!.Value<string>()!.Should().Contain("SpecialFolder");
        data["typeKind"]!.Value<string>().Should().Be("Enum");
    }
}
