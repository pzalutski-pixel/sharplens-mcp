using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks add_null_checks parameter selection: nullable-annotated parameters
// (whose contract permits null) are never guarded, parameters the body already
// guards are not guarded twice, and applying then re-running is a no-op.
public class AddNullChecksFilterTests
{
    private static (RoslynService service, string filePath, int methodLine, int methodColumn) CreateService(string code, string methodName)
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "Guarded.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var lines = code.Split('\n');
        var line = Array.FindIndex(lines, l => l.Contains(methodName, StringComparison.Ordinal));
        line.Should().BeGreaterThan(-1);
        var column = lines[line].IndexOf(methodName, StringComparison.Ordinal);
        return (service, document.FilePath!, line, column);
    }

    [Fact]
    public async Task NullableAnnotatedAndValueTypeParameters_AreNotGuarded()
    {
        const string code = @"
namespace Production;

public class Service
{
    public void Handle(string name, string? optionalNote, int count, int? maybeCount)
    {
        _ = name.Length + count;
    }
}";
        var (service, filePath, line, column) = CreateService(code, "Handle");

        var result = await service.AddNullChecksAsync(filePath, line, column);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var guardedParams = (json["data"]!["parametersWithNullChecks"] as JArray)!
            .Select(p => p.Value<string>()).ToList();
        guardedParams.Should().BeEquivalentTo(new[] { "name" },
            "string? declares null as valid, int is not nullable, int? declares null as valid — only the plain string gets a guard");

        var skippedNullable = (json["data"]!["skippedNullable"] as JArray)!
            .Select(p => p.Value<string>()).ToList();
        skippedNullable.Should().BeEquivalentTo(new[] { "optionalNote", "maybeCount" });

        json["data"]!["generatedCode"]!.Value<string>()!
            .Should().NotContain("optionalNote", "no guard may be generated for a nullable-annotated parameter");
    }

    [Fact]
    public async Task AlreadyGuardedParameters_AreSkipped_ForBothGuardStyles()
    {
        const string code = @"
using System;

namespace Production;

public class Service
{
    public void Handle(string first, string second, string third)
    {
        ArgumentNullException.ThrowIfNull(first);
        if (second == null) throw new ArgumentNullException(nameof(second));
        _ = first.Length + second.Length + third.Length;
    }
}";
        var (service, filePath, line, column) = CreateService(code, "Handle");

        var result = await service.AddNullChecksAsync(filePath, line, column);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var guardedParams = (json["data"]!["parametersWithNullChecks"] as JArray)!
            .Select(p => p.Value<string>()).ToList();
        guardedParams.Should().BeEquivalentTo(new[] { "third" },
            "first is guarded by ThrowIfNull and second by an if-null-throw — only third needs a new guard");

        var skippedGuarded = (json["data"]!["skippedAlreadyGuarded"] as JArray)!
            .Select(p => p.Value<string>()).ToList();
        skippedGuarded.Should().BeEquivalentTo(new[] { "first", "second" });
    }

    [Fact]
    public async Task AlreadyGuardedParameters_AreSkipped_ForBroadenedGuardStyles()
    {
        const string code = @"
using System;

namespace Production;

public class Service
{
    public void Handle(string viaThrowHelper, string viaIsNullOrEmpty, string viaNotPattern, string fresh)
    {
        ArgumentException.ThrowIfNullOrEmpty(viaThrowHelper);
        if (string.IsNullOrEmpty(viaIsNullOrEmpty)) throw new ArgumentException(nameof(viaIsNullOrEmpty));
        if (viaNotPattern is not { }) throw new ArgumentNullException(nameof(viaNotPattern));
        _ = fresh.Length;
    }
}";
        var (service, filePath, line, column) = CreateService(code, "Handle");

        var json = JObject.FromObject(await service.AddNullChecksAsync(filePath, line, column));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        (json["data"]!["parametersWithNullChecks"] as JArray)!.Select(p => p.Value<string>())
            .Should().BeEquivalentTo(new[] { "fresh" },
                "ThrowIfNullOrEmpty, IsNullOrEmpty-if-throw, and `is not { }` all already guard their parameter");
        (json["data"]!["skippedAlreadyGuarded"] as JArray)!.Select(p => p.Value<string>())
            .Should().BeEquivalentTo(new[] { "viaThrowHelper", "viaIsNullOrEmpty", "viaNotPattern" });
    }

    [Fact]
    public async Task GeneratedGuards_IndentToTheMethodsBodyLevel_NotAHardcodedEightSpaces()
    {
        const string code = @"
namespace Production;

public class Outer
{
    public class Inner
    {
        public void Deep(string a, string b)
        {
            _ = a.Length + b.Length;
        }
    }
}";
        var (service, filePath, line, column) = CreateService(code, "Deep");

        var json = JObject.FromObject(await service.AddNullChecksAsync(filePath, line, column));
        var generated = json["data"]!["generatedCode"]!.Value<string>()!;

        // Deep sits at column 8 (nested class), so its body — and the inserted
        // guards — indent to 12 spaces, not the old hardcoded 8.
        generated.Should().Contain("\n            global::System.ArgumentNullException.ThrowIfNull(b);",
            "the second guard line must carry the method-body indent (12 spaces), derived from the method column");
    }

    [Fact]
    public async Task ApplyThenRerun_IsIdempotent()
    {
        const string code = @"
namespace Production;

public class Service
{
    public void Handle(string name)
    {
        _ = name.Length;
    }
}";
        var (service, filePath, line, column) = CreateService(code, "Handle");

        var applyResult = await service.AddNullChecksAsync(filePath, line, column, preview: false);
        var applyJson = JObject.FromObject(applyResult);
        applyJson["success"]!.Value<bool>().Should().BeTrue(applyJson.ToString());
        applyJson["data"]!["applied"]!.Value<bool>().Should().BeTrue();

        // The unified apply path updated the in-memory solution; the second run
        // must see the freshly inserted guard and decline to add another.
        var rerunResult = await service.AddNullChecksAsync(filePath, line, column);
        var rerunJson = JObject.FromObject(rerunResult);
        rerunJson["success"]!.Value<bool>().Should().BeTrue(rerunJson.ToString());
        rerunJson["data"]!["message"]!.Value<string>().Should().Be("No reference-type parameter needs a null check",
            "a second run after apply must find nothing left to guard");
        (rerunJson["data"]!["skippedAlreadyGuarded"] as JArray)!
            .Select(p => p.Value<string>()).Should().BeEquivalentTo(new[] { "name" });
    }

    [Fact]
    public async Task ConstructorParameters_AreGuarded()
    {
        const string code = @"
namespace Production;

public class Service
{
    public Service(string dependency, string label)
    {
        _ = dependency.Length + label.Length;
    }
}";
        var (service, filePath, line, column) = CreateService(code, "public Service(");

        var json = JObject.FromObject(await service.AddNullChecksAsync(filePath, line, column));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        (json["data"]!["parametersWithNullChecks"] as JArray)!
            .Select(p => p.Value<string>())
            .Should().BeEquivalentTo(new[] { "dependency", "label" },
                "a constructor's reference-type parameters must be guarded — constructors were previously rejected as NotAMethod");
    }

    [Fact]
    public async Task LocalFunctionParameters_AreGuarded()
    {
        const string code = @"
namespace Production;

public class Service
{
    public void Outer()
    {
        Local(""x"");

        void Local(string note)
        {
            _ = note.Length;
        }
    }
}";
        var (service, filePath, line, column) = CreateService(code, "void Local(");

        var json = JObject.FromObject(await service.AddNullChecksAsync(filePath, line, column));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        (json["data"]!["parametersWithNullChecks"] as JArray)!
            .Select(p => p.Value<string>())
            .Should().BeEquivalentTo(new[] { "note" },
                "a local function's reference-type parameters must be guarded — local functions were previously rejected as NotAMethod");
    }
}
