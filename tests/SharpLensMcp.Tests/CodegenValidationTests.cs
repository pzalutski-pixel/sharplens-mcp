using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// The codegen tools build syntax (SyntaxGenerator/SyntaxFactory) instead of
// concatenating strings — these tests lock the resulting guarantee: every
// generated text COMPILES. Member snippets are wrapped in the minimal type
// shell they are destined for, because constructors and operators are only
// valid inside their own type.
public class CodegenValidationTests : RoslynServiceTestBase
{
    private async Task AssertCompilesStandalone(string code, string what)
    {
        var validation = await Service.ValidateCodeAsync(code, standalone: true);
        var data = GetData(validation);
        data["compiles"]!.Value<bool>().Should().BeTrue(
            $"{what} must compile; errors: {data["errors"]}");
    }

    [Fact]
    public async Task GeneratedConstructor_CompilesInsideItsType()
    {
        var result = await Service.GenerateConstructorAsync(
            null, null, null, includeProperties: true, typeName: "GenCtorWithProperties");
        var constructorCode = GetData(result)["constructorCode"]!.Value<string>()!;

        constructorCode.Should().Contain("GenCtorWithProperties(string name, int age)");
        await AssertCompilesStandalone(
            "public class GenCtorWithProperties\n{\n" +
            "    public string Name { get; set; } = \"\";\n    public int Age { get; set; }\n" +
            constructorCode + "\n}",
            "the generated constructor");
    }

    [Fact]
    public async Task GeneratedConstructor_DisambiguatesCollidingParameterNames()
    {
        var result = await Service.GenerateConstructorAsync(
            null, null, null, includeProperties: true, typeName: "CtorCollisionTarget");
        var data = GetData(result);
        var constructorCode = data["constructorCode"]!.Value<string>()!;

        var paramNames = (data["parameters"] as JArray)!
            .Select(p => p["name"]!.Value<string>()).ToList();
        paramNames.Should().BeEquivalentTo(new[] { "value", "value2" },
            "the _value field and Value property both camel-case to 'value' — the second must be suffixed");
        paramNames.Should().OnlyHaveUniqueItems("duplicate parameter names do not compile");

        await AssertCompilesStandalone(
            "public class CtorCollisionTarget\n{\n    private readonly int _value;\n    public int Value { get; set; }\n" +
            constructorCode + "\n}",
            "the generated constructor with disambiguated parameters");
    }

    [Fact]
    public async Task GeneratedCoalescingConstructor_CompilesInsideItsType()
    {
        var result = await Service.GenerateConstructorAsync(
            null, null, null, initializeToDefault: true, typeName: "GenCtorWithNullableField");
        var constructorCode = GetData(result)["constructorCode"]!.Value<string>()!;

        constructorCode.Should().Contain("OptionalName = optionalName ?? default");
        await AssertCompilesStandalone(
            "public class GenCtorWithNullableField\n{\n    public string? OptionalName;\n" +
            constructorCode + "\n}",
            "the generated coalescing constructor");
    }

    [Fact]
    public async Task GeneratedEqualityMembers_CompileInsideTheirType()
    {
        var result = await Service.GenerateEqualityMembersAsync(
            null, null, null, includeOperators: true, typeName: "EqualityMembersTarget");
        var generated = GetData(result)["generatedCode"]!.Value<string>()!;

        await AssertCompilesStandalone(
            "using System;\npublic class EqualityMembersTarget\n{\n    public int Value;\n" +
            generated + "\n}",
            "the generated equality members (operators included)");
    }

    [Fact]
    public async Task GeneratedEqualityMembers_ForStruct_AreValueTypeCorrect()
    {
        var result = await Service.GenerateEqualityMembersAsync(
            null, null, null, includeOperators: true, typeName: "EqualityMembersStructTarget");
        var generated = GetData(result)["generatedCode"]!.Value<string>()!;

        generated.Should().Contain("public bool Equals(EqualityMembersStructTarget other)",
            "a struct's strongly-typed Equals takes the value non-nullable, not Nullable<T>");
        generated.Should().NotContain("Equals(EqualityMembersStructTarget? other)",
            "the boxed Nullable<T> parameter was the bug — a struct param is never null");
        generated.Should().NotContain("ReferenceEquals",
            "ReferenceEquals boxes a struct and is always false — it must not appear for a value type");
        generated.Should().Contain("operator ==(EqualityMembersStructTarget left, EqualityMembersStructTarget right)",
            "value-type operators take the operands non-nullable");
        generated.Should().Contain("left.Equals(right)",
            "value-type operators compare via the instance Equals, not the boxing static object.Equals(left, right)");

        await AssertCompilesStandalone(
            "using System;\npublic struct EqualityMembersStructTarget\n{\n    public int Amount;\n    public string Currency;\n" +
            generated + "\n}",
            "the generated struct equality members");
    }

    [Fact]
    public async Task GeneratedInterface_CompilesStandalone()
    {
        var result = await Service.ExtractInterfaceAsync(
            null, null, null, interfaceName: "INameAddressingTarget",
            includeMemberNames: null, typeName: "NameAddressingTarget");
        var interfaceCode = GetData(result)["interfaceCode"]!.Value<string>()!;

        interfaceCode.Should().Contain("public interface INameAddressingTarget");
        await AssertCompilesStandalone(interfaceCode, "the generated interface");
    }

    [Fact]
    public async Task GeneratedInterface_ForGenericType_DeclaresTypeParameterAndConstraint()
    {
        var result = await Service.ExtractInterfaceAsync(
            null, null, null, interfaceName: "IGenericInterfaceTarget",
            includeMemberNames: null, typeName: "GenericInterfaceTarget");
        var interfaceCode = GetData(result)["interfaceCode"]!.Value<string>()!;

        interfaceCode.Should().Contain("interface IGenericInterfaceTarget<T>",
            "a generic source type extracts to a generic interface — without <T> its members reference an undeclared T");
        interfaceCode.Should().Contain("where T : class",
            "the type parameter constraint must travel onto the interface");
        await AssertCompilesStandalone(interfaceCode, "the generated generic interface");
    }

    [Fact]
    public async Task ValidateCode_ErrorOnFirstLine_ReportsSnippetRelativeColumn()
    {
        // The wrapper indents the snippet; a diagnostic on the snippet's first line
        // must still report a snippet-relative column. `undefinedVar` begins at column
        // 8 ("int x = " is 8 chars), not 8 + the wrapper indent.
        var result = await Service.ValidateCodeAsync("int x = undefinedVar;");

        var data = GetData(result);
        var errors = (data["errors"] as JArray)!;
        var first = errors.First(e => e["id"]!.Value<string>() == "CS0103");
        first["line"]!.Value<int>().Should().Be(0, "the error is on the snippet's first line");
        first["column"]!.Value<int>().Should().Be(8,
            "column is snippet-relative ('int x = ' is 8 chars), not offset by the wrapper indent");
    }

    [Fact]
    public async Task ValidateCode_SuggestedNextTools_AreRealToolNames()
    {
        // suggestedNextTools must lead with an actual tool name, not prose. meta is a
        // ResponseMetadata class → PascalCase SuggestedNextTools in-process.
        var valid = await Service.ValidateCodeAsync("int x = 1;");
        GetData(valid)["compiles"]!.Value<bool>().Should().BeTrue();
        var validTools = (JObject.FromObject(valid)["meta"]!["SuggestedNextTools"] as JArray)!;
        validTools[0].Value<string>().Should().StartWith("sync_documents");

        var invalid = await Service.ValidateCodeAsync("int x = undefinedVar;");
        var invalidTools = (JObject.FromObject(invalid)["meta"]!["SuggestedNextTools"] as JArray)!;
        invalidTools[0].Value<string>().Should().StartWith("validate_code");
    }
}
