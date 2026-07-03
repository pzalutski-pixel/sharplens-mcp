using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// generate_equality_members must reject records (they already have value
// equality), name a generic type with its type parameters, and chain base.Equals
// when the base has value semantics. Read-only: asserts the preview's generatedCode.
public class GenerateEqualityMembersG1Tests
{
    private static async Task<JObject> Preview(string code, string typeName)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "G1.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var result = await service.GenerateEqualityMembersAsync(
            filePath: null, line: null, column: null, includeOperators: true, preview: true, typeName: typeName);
        return JObject.FromObject(result);
    }

    [Fact]
    public async Task Record_IsRejected()
    {
        var json = await Preview("namespace P { public record Rec(int X); }", "Rec");
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Contain("record");
    }

    [Fact]
    public async Task GenericType_NamesTheConstructedTypeWithItsTypeParameters()
    {
        var json = await Preview("namespace P { public class Box<T> { public T Value = default!; } }", "Box");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var generated = json["data"]!["generatedCode"]!.Value<string>()!;
        generated.Should().Contain("Box<T>",
            "the generic type's Equals/`is` pattern must name Box<T>, not a bare Box that would not compile");
    }

    [Fact]
    public async Task DerivedFromValueEqualityBase_ChainsBaseEquals()
    {
        const string code = """
            namespace P
            {
                public class BaseState
                {
                    public int Id;
                    public override bool Equals(object? obj) => obj is BaseState b && Id == b.Id;
                    public override int GetHashCode() => Id.GetHashCode();
                }
                public class Derived : BaseState
                {
                    public string Name = "";
                }
            }
            """;
        var json = await Preview(code, "Derived");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var generated = json["data"]!["generatedCode"]!.Value<string>()!;
        generated.Should().Contain("base.Equals(other)",
            "a base with overridden value equality must be chained into the derived Equals");
    }
}
