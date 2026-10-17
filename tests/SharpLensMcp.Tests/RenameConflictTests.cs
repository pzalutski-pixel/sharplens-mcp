using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;
using static SharpLensMcp.Tests.RefactoringTestKit;

namespace SharpLensMcp.Tests;

// rename_symbol reports the Renamer's unresolved conflicts: the preview lists each
// location where the new name would collide or rebind, and an apply that would leave a
// conflict is refused with nothing written. A conflict the Renamer resolves itself (by
// qualifying a reference) is not a conflict. A multi-targeted file is listed once, and
// maxFiles and verbosity are validated.
public class RenameConflictTests
{
    internal const string ConflictSource = """
        namespace M
        {
            public class Ren
            {
                public int Alpha() => 1;
                public int Beta() => 2;
                public int Sum() => Alpha() + Beta();
            }
        }
        """;

    [Fact]
    public async Task RenameIntoAnExistingSignature_PreviewListsTheConflicts()
    {
        var (service, _, document) = Load(ConflictSource);

        var json = Json(await service.RenameSymbolAsync(null, null, null, "Beta", preview: true,
            typeName: "M.Ren", memberName: "Alpha"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var path = Reported(document.FilePath!);
        ((JArray)json["data"]!["conflicts"]!).Select(c =>
                $"{c["filePath"]!.Value<string>()}:{c["line"]!.Value<int>()}:{c["column"]!.Value<int>()} {c["kind"]!.Value<string>()}")
            .Should().Equal(
                $"{path}:5:19 unresolvable",
                $"{path}:6:28 unresolved",
                $"{path}:6:38 unresolved");
    }

    [Fact]
    public async Task RenameIntoAnExistingSignature_ApplyIsRefused_AndNothingIsWritten()
    {
        var (service, workspace, document) = Load(ConflictSource);

        var json = Json(await service.RenameSymbolAsync(null, null, null, "Beta", preview: false,
            typeName: "M.Ren", memberName: "Alpha"));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be(
            "Renaming 'Alpha' to 'Beta' leaves 3 unresolved conflicts; nothing was written");
        ((JArray)json["error"]!["Context"]!["conflicts"]!).Count.Should().Be(3);
        (await TextOf(workspace, document.Id)).Should().Be(ConflictSource);
    }

    internal const string ResolvableSource = """
        namespace M
        {
            public class C
            {
                private int x;
                public int Read()
                {
                    int y = 1;
                    return y + x;
                }
            }
        }
        """;

    [Fact]
    public async Task ConflictTheRenamerResolves_IsNotReported_AndApplies()
    {
        var (service, workspace, document) = Load(ResolvableSource);
        var line = ResolvableSource.Split('\n').ToList().FindIndex(l => l.Contains("int y = 1;"));

        var preview = Json(await service.RenameSymbolAsync(document.FilePath, line, 16, "x", preview: true));
        preview["success"]!.Value<bool>().Should().BeTrue(preview.ToString());
        ((JArray)preview["data"]!["conflicts"]!).Should().BeEmpty();

        var json = Json(await service.RenameSymbolAsync(document.FilePath, line, 16, "x", preview: false));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(ResolvableSource,
            ("int y = 1;", "int x = 1;"),
            ("return y + x;", "return x + this.x;")));
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task MultiTargetedFile_IsListedOnce()
    {
        using var fixture = new MultiTargetSolutionFixture();
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(fixture.Workspace);

        var json = Json(await service.RenameSymbolAsync(null, null, null, "Render", preview: true,
            typeName: "Multi.Greeter", memberName: "Format"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        ((JArray)json["data"]!["changes"]!).Select(c => $"{c["filePath"]!.Value<string>()}:{c["changeCount"]!.Value<int>()}")
            .Should().Equal(new[] { $"{Reported(fixture.MultiFilePath(MultiTargetSources.MultiFileName))}:3" },
                "the declaration, Greet's call and Modern's call - one file, whatever number of frameworks compile it");
        JObject.FromObject(await service.RenameSymbolAsync(null, null, null, "Render", preview: true,
                typeName: "Multi.Greeter", memberName: "Format"))["meta"]!["TotalCount"]!.Value<int>()
            .Should().Be(1);
    }

    [Theory]
    [InlineData(0, "maxFiles must be between 1 and 1000 (got 0)")]
    [InlineData(-3, "maxFiles must be between 1 and 1000 (got -3)")]
    public async Task MaxFilesBelowOne_IsRejected(int maxFiles, string message)
    {
        var (service, _, _) = Load(ConflictSource);

        var json = Json(await service.RenameSymbolAsync(null, null, null, "Gamma", preview: true, maxFiles: maxFiles,
            typeName: "M.Ren", memberName: "Alpha"));

        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be(message);
    }

    [Fact]
    public async Task UnknownVerbosity_IsRejected()
    {
        var (service, _, _) = Load(ConflictSource);

        var json = Json(await service.RenameSymbolAsync(null, null, null, "Gamma", preview: true, verbosity: "loud",
            typeName: "M.Ren", memberName: "Alpha"));

        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be(
            "verbosity must be one of: summary, compact, full (got 'loud')");
    }
}
