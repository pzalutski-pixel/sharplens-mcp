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
        var loadJson = JObject.FromObject(result);
        loadJson["success"].Should().NotBeNull();
        loadJson["success"]!.Value<bool>().Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        // Always restore the fixture to its committed content — recreating it even
        // if a test deleted it — and assert the restore physically landed, so a
        // swallowed write can't leave the tree dirty for the next serialized test.
        if (_originalFixtureContent != null && _fixturePath != null)
        {
            File.WriteAllText(_fixturePath, _originalFixtureContent);
            File.ReadAllText(_fixturePath).Should().Be(_originalFixtureContent,
                "the fixture must be restored to its committed content after the test");
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Apply_OnTestProject_RemovesRedundantAndSortsUsingsOnDisk()
    {
        // Apply across the tests project, which has ImplicitUsings enabled — so
        // the compiler marks the fixture's `using System;` and
        // `using System.Collections.Generic;` CS8019-redundant and the tool must
        // REMOVE them, while the genuinely needed System.Text (not implicit) and
        // Microsoft.CodeAnalysis survive, sorted System-first.
        var result = await _service.OrganizeUsingsBatchAsync(
            projectName: "SharpLensMcp.Tests",
            filePattern: "UnsortedUsingsFixture.cs",
            preview: false);

        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        data["preview"].Should().NotBeNull();
        data["preview"]!.Value<bool>().Should().BeFalse();
        data["filesWithChanges"].Should().NotBeNull();
        data["filesWithChanges"]!.Value<int>().Should().BeGreaterOrEqualTo(1,
            "the fixture file's unsorted usings must show up as changes");

        var diskContent = File.ReadAllText(_fixturePath);
        diskContent.Should().NotContain("using System;",
            "ImplicitUsings already provides System — the compiler flags the explicit directive CS8019 and the tool removes it");
        diskContent.Should().NotContain("using System.Collections.Generic;",
            "ImplicitUsings already provides System.Collections.Generic");

        var systemTextIdx = diskContent.IndexOf("using System.Text;");
        var microsoftIdx = diskContent.IndexOf("using Microsoft.CodeAnalysis;");
        systemTextIdx.Should().BeGreaterThan(-1, "System.Text is not implicit and is used by the fixture");
        microsoftIdx.Should().BeGreaterThan(-1, "Microsoft.CodeAnalysis is used by the fixture");
        systemTextIdx.Should().BeLessThan(microsoftIdx,
            "System root namespaces sort before other plain usings");
    }
}
