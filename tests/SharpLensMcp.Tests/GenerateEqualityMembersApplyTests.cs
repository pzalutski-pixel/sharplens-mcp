using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// End-to-end snapshot/restore test for generate_equality_members preview=false.
public class GenerateEqualityMembersApplyTests : IAsyncLifetime
{
    private RoslynService _service = null!;
    private string _solutionPath = null!;
    private string _fixturePath = null!;
    private string _originalFixtureContent = null!;

    public async Task InitializeAsync()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !File.Exists(Path.Combine(dir, "SharpLensMcp.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        if (dir == null) throw new InvalidOperationException("SharpLensMcp.sln not found");

        _solutionPath = Path.Combine(dir, "SharpLensMcp.sln");
        _fixturePath = Path.Combine(dir, "tests", "SharpLensMcp.Tests", "Fixtures", "EqualityMembersFixture.cs");
        _originalFixtureContent = File.ReadAllText(_fixturePath);

        _service = new RoslynService();
        var result = await _service.LoadSolutionAsync(_solutionPath);
        JObject.FromObject(result)["success"]?.Value<bool>().Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        if (_originalFixtureContent != null && _fixturePath != null && File.Exists(_fixturePath))
        {
            File.WriteAllText(_fixturePath, _originalFixtureContent);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Apply_OnTypeWithOneField_WritesEqualsAndGetHashCodeToDisk()
    {
        var lines = File.ReadAllLines(_fixturePath);
        var classLine = Array.FindIndex(lines, l => l.Contains("class EqualityMembersTarget"));
        classLine.Should().BeGreaterThan(-1);

        var result = await _service.GenerateEqualityMembersAsync(
            _fixturePath,
            line: classLine,
            column: 14,
            includeOperators: false,
            preview: false);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["applied"]?.Value<bool>().Should().BeTrue(
            "preview=false sets applied=true (CodeGeneration.cs:651)");

        var diskContent = File.ReadAllText(_fixturePath);
        diskContent.Should().Contain("public override bool Equals(object? obj)");
        diskContent.Should().Contain("public bool Equals(EqualityMembersTarget? other)");
        diskContent.Should().Contain("public override int GetHashCode()");
        diskContent.Should().Contain("HashCode.Combine(Value)");
        diskContent.Should().NotContain("operator ==",
            "includeOperators=false must NOT emit operator overloads");
    }
}
