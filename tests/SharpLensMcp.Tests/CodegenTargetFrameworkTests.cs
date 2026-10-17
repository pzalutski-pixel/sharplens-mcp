using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Generated code compiles for every target framework of the project it lands in: on a
// project that also targets netstandard2.0 (no ArgumentNullException.ThrowIfNull, no
// System.HashCode), add_null_checks writes an if-null-throw guard and
// generate_equality_members a hash it can compute there; on a net8.0-only project the
// modern APIs are used.
public class CodegenTargetFrameworkTests
{
    private const string Guard =
        "namespace Multi;\n" +
        "\n" +
        "public class Guard\n" +
        "{\n" +
        "    public int Count;\n" +
        "    public string Name = \"\";\n" +
        "\n" +
        "    public void Take(object value)\n" +
        "    {\n" +
        "    }\n" +
        "}\n";

    private static async Task<(DiskSolution solution, RoslynService service)> LoadAsync(string targetFrameworks)
    {
        var solution = new DiskSolution("Multi/Multi.csproj");
        solution.WriteText("Multi/Multi.csproj", DiskSolution.SdkProject(targetFrameworks));
        solution.WriteText("Multi/Guard.cs", Guard);
        solution.Restore();
        var service = new RoslynService();
        var load = JObject.FromObject(await service.LoadSolutionAsync(solution.SolutionPath));
        load["success"]!.Value<bool>().Should().BeTrue(load.ToString());
        return (solution, service);
    }

    [Fact]
    public async Task NullChecks_OnAProjectThatAlsoTargetsNetStandard_UseAGuardEveryFrameworkHas()
    {
        var (solution, service) = await LoadAsync("net8.0;netstandard2.0");
        using var _ = solution;

        var result = JObject.FromObject(await service.AddNullChecksAsync(
            filePath: null, line: null, column: null, preview: false, typeName: "Guard", memberName: "Take"));

        result["success"]!.Value<bool>().Should().BeTrue(result.ToString());
        result["data"]!["postApplyErrors"]!.Type.Should().Be(JTokenType.Null);
        solution.ReadText("Multi/Guard.cs").Should().Be(Guard.Replace(
            "    public void Take(object value)\n    {\n    }\n",
            "    public void Take(object value)\n    {\n        if (value is null) throw new global::System.ArgumentNullException(nameof(value));\n    }\n"));
    }

    [Fact]
    public async Task NullChecks_OnANet8OnlyProject_UseThrowIfNull()
    {
        var (solution, service) = await LoadAsync("net8.0");
        using var _ = solution;

        var result = JObject.FromObject(await service.AddNullChecksAsync(
            filePath: null, line: null, column: null, preview: false, typeName: "Guard", memberName: "Take"));

        result["success"]!.Value<bool>().Should().BeTrue(result.ToString());
        solution.ReadText("Multi/Guard.cs").Should().Be(Guard.Replace(
            "    public void Take(object value)\n    {\n    }\n",
            "    public void Take(object value)\n    {\n        global::System.ArgumentNullException.ThrowIfNull(value);\n    }\n"));
    }

    [Fact]
    public async Task EqualityMembers_OnAProjectThatAlsoTargetsNetStandard_HashWithoutSystemHashCode()
    {
        var (solution, service) = await LoadAsync("net8.0;netstandard2.0");
        using var _ = solution;

        var result = JObject.FromObject(await service.GenerateEqualityMembersAsync(
            filePath: null, line: null, column: null, includeOperators: false, preview: false, typeName: "Guard"));

        result["success"]!.Value<bool>().Should().BeTrue(result.ToString());
        result["data"]!["postApplyErrors"]!.Type.Should().Be(JTokenType.Null);
        solution.ReadText("Multi/Guard.cs").Should().Be(Guard.Replace(
            "    public void Take(object value)\n    {\n    }\n}\n",
            "    public void Take(object value)\n    {\n    }\n" +
            "\n" +
            "    public override bool Equals(object obj)\n" +
            "    {\n" +
            "        return obj is Guard other && Equals(other);\n" +
            "    }\n" +
            "\n" +
            "    public bool Equals(Guard other)\n" +
            "    {\n" +
            "        if (other is null)\n" +
            "        {\n" +
            "            return false;\n" +
            "        }\n" +
            "\n" +
            "        if (ReferenceEquals(this, other))\n" +
            "        {\n" +
            "            return true;\n" +
            "        }\n" +
            "\n" +
            "        return Count == other.Count && Name == other.Name;\n" +
            "    }\n" +
            "\n" +
            "    public override int GetHashCode()\n" +
            "    {\n" +
            "        unchecked\n" +
            "        {\n" +
            "            int hash = 17;\n" +
            "            hash = hash * -1521134295 + global::System.Collections.Generic.EqualityComparer<int>.Default.GetHashCode(Count);\n" +
            "            hash = hash * -1521134295 + global::System.Collections.Generic.EqualityComparer<string>.Default.GetHashCode(Name);\n" +
            "            return hash;\n" +
            "        }\n" +
            "    }\n" +
            "}\n"));
    }
}
