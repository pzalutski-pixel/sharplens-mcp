using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// End-to-end snapshot/restore test for organize_usings_batch preview=false.
public class OrganizeUsingsBatchApplyTests : IAsyncLifetime
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
        _fixturePath = Path.Combine(dir, "tests", "SharpLensMcp.Tests", "Fixtures", "UnsortedUsingsFixture.cs");
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
    public async Task Apply_OnTestProject_SortsUsingsOnDisk()
    {
        // Apply across the whole tests project. The fixture has unsorted usings
        // (Microsoft first, then mixed System.*). After apply, System.* must
        // appear BEFORE Microsoft.* per Analysis.cs:981-984.
        var result = await _service.OrganizeUsingsBatchAsync(
            projectName: "SharpLensMcp.Tests",
            filePattern: "UnsortedUsingsFixture.cs",
            preview: false);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        data["preview"]?.Value<bool>().Should().BeFalse();
        data["filesWithChanges"]?.Value<int>().Should().BeGreaterOrEqualTo(1,
            "the fixture file's unsorted usings must show up as changes");

        // Verify on disk: System.* now appears before Microsoft.*.
        var diskContent = File.ReadAllText(_fixturePath);
        var systemIdx = diskContent.IndexOf("using System;");
        var microsoftIdx = diskContent.IndexOf("using Microsoft.CodeAnalysis;");
        systemIdx.Should().BeGreaterThan(-1);
        microsoftIdx.Should().BeGreaterThan(-1);
        systemIdx.Should().BeLessThan(microsoftIdx,
            "System-prefixed usings sort to bucket 0; Microsoft to bucket 1");
    }
}
