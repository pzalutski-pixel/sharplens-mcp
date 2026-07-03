using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks generate_equality_members member selection and hash generation: auto-
// property backing fields never leak into generated code, computed get-only
// properties are excluded, and types with more than eight members hash ALL of
// them (HashCode.Combine caps at eight arguments).
public class GenerateEqualityMembersSelectionTests
{
    private static (RoslynService service, string filePath, int line, int column) CreateService(string code, string className)
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "Equatable.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var lines = code.Split('\n');
        var line = Array.FindIndex(lines, l => l.Contains("class " + className, StringComparison.Ordinal));
        line.Should().BeGreaterThan(-1);
        var column = lines[line].IndexOf(className, StringComparison.Ordinal);
        return (service, document.FilePath!, line, column);
    }

    [Fact]
    public async Task AutoProperties_CompareTheProperty_NeverTheBackingField()
    {
        const string code = @"
namespace Production;

public class Point
{
    public int X { get; set; }
    public int Y { get; init; }
}";
        var (service, filePath, line, column) = CreateService(code, "Point");

        var result = await service.GenerateEqualityMembersAsync(filePath, line, column, includeOperators: false);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var generated = json["data"]!["generatedCode"]!.Value<string>()!;
        generated.Should().NotContain("k__BackingField",
            "compiler-generated backing field names are not valid C# and must never appear in generated code");
        generated.Should().Contain("X == other.X");
        generated.Should().Contain("Y == other.Y");

        var compared = (json["data"]!["membersCompared"] as JArray)!
            .Select(m => m.Value<string>()).ToList();
        compared.Should().BeEquivalentTo(new[] { "X", "Y" },
            "exactly the two auto-properties are compared, each once");
    }

    [Fact]
    public async Task ComputedGetOnlyProperty_IsExcludedFromComparison()
    {
        const string code = @"
namespace Production;

public class Rectangle
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Area => Width * Height;
}";
        var (service, filePath, line, column) = CreateService(code, "Rectangle");

        var result = await service.GenerateEqualityMembersAsync(filePath, line, column, includeOperators: false);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var compared = (json["data"]!["membersCompared"] as JArray)!
            .Select(m => m.Value<string>()).ToList();
        compared.Should().BeEquivalentTo(new[] { "Width", "Height" },
            "Area derives from Width and Height — comparing it is redundant and it has no backing store");
    }

    [Fact]
    public async Task BaseOverridesEqualsButNotGetHashCode_ChainsEqualsNotHash()
    {
        // HashlessBase has value-semantics Equals but an identity GetHashCode.
        // The derived equality must chain base.Equals (so base state is compared)
        // yet must NOT fold base.GetHashCode() into the hash — an identity hash is
        // inconsistent with the base's own Equals and would break the contract.
        const string code = @"
namespace Production;

public class HashlessBase
{
    public int BaseValue;
    public override bool Equals(object? obj) => obj is HashlessBase o && BaseValue == o.BaseValue;
}

public class HashlessDerived : HashlessBase
{
    public int DerivedValue;
}";
        var (service, filePath, line, column) = CreateService(code, "HashlessDerived");

        var result = await service.GenerateEqualityMembersAsync(filePath, line, column, includeOperators: false);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var generated = json["data"]!["generatedCode"]!.Value<string>()!;
        generated.Should().Contain("base.Equals(other)",
            "the base overrides Equals, so the generated value equality must chain it");
        generated.Should().NotContain("base.GetHashCode()",
            "the base does not override GetHashCode — folding its identity hash would break the Equals/GetHashCode contract");
        generated.Should().Contain("hash.Add(DerivedValue);",
            "the derived member still feeds the hash");
    }

    [Fact]
    public async Task TypeWithExistingEquals_IsRefused()
    {
        const string code = @"
namespace Production;

public class HasEquals
{
    public int X;
    public override bool Equals(object? obj) => obj is HasEquals o && X == o.X;
    public override int GetHashCode() => X;
}";
        var (service, filePath, line, column) = CreateService(code, "HasEquals");

        var result = await service.GenerateEqualityMembersAsync(filePath, line, column, includeOperators: false);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse(
            "regenerating equality on a type that already declares it would duplicate the members (CS0111)");
        json["error"]!["Code"]!.Value<string>().Should().Be("INVALID_PARAMETER");
        json["error"]!["Message"]!.Value<string>().Should().Contain("already declares");
    }

    [Fact]
    public async Task DerivedWithNoOwnMembers_GeneratesBaseOnlyEquality()
    {
        const string code = @"
namespace Production;

public class ValueBase
{
    public int A;
    public override bool Equals(object? obj) => obj is ValueBase o && A == o.A;
    public override int GetHashCode() => A;
}

public class DerivedNoMembers : ValueBase
{
}";
        var (service, filePath, line, column) = CreateService(code, "DerivedNoMembers");

        var result = await service.GenerateEqualityMembersAsync(filePath, line, column, includeOperators: false);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var generated = json["data"]!["generatedCode"]!.Value<string>()!;
        generated.Should().Contain("base.Equals(other)",
            "a derived type whose only comparable state is the value-equality base chains base.Equals");
        generated.Should().NotContain("No instance fields",
            "it must generate base-only equality, not report no members to compare");
    }

    [Fact]
    public async Task MoreThanEightMembers_AllFeedTheHash()
    {
        const string code = @"
namespace Production;

public class Wide
{
    public int A { get; set; }
    public int B { get; set; }
    public int C { get; set; }
    public int D { get; set; }
    public int E { get; set; }
    public int F { get; set; }
    public int G { get; set; }
    public int H { get; set; }
    public int I { get; set; }
    public int J { get; set; }
}";
        var (service, filePath, line, column) = CreateService(code, "Wide");

        var result = await service.GenerateEqualityMembersAsync(filePath, line, column, includeOperators: false);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var generated = json["data"]!["generatedCode"]!.Value<string>()!;
        foreach (var member in new[] { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J" })
        {
            generated.Should().Contain($"hash.Add({member});",
                $"every member Equals compares must feed the hash — '{member}' was previously dropped by the eight-argument Combine cap");
            generated.Should().Contain($"{member} == other.{member}");
        }
        generated.Should().Contain("return hash.ToHashCode();");
    }
}
