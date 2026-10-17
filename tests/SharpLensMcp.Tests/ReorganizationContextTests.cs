using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;
using static SharpLensMcp.Tests.RefactoringTestKit;

namespace SharpLensMcp.Tests;

// The reorganization tools carry over exactly the context the moved code needs and
// leave every file they compute compilable:
//   - move_type_to_file's new file holds the type in its namespace with only the usings
//     it needs: never the origin's top-level statements, assembly attributes or other
//     types;
//   - removing a declaration (move_type_to_file, split_type, remove_unused_code) keeps
//     the preprocessor directives around it, so no #region/#if is left unbalanced, and
//     keeps the file header and #nullable context a removed first type carried;
//   - split_type's new half carries neither the primary-constructor parameter list nor
//     the base list, and keeps the directives (not the doc comment or attributes) that
//     precede the type;
//   - remove_unused_code refuses an unknown projectName.
public class ReorganizationContextTests
{
    private static string ContentOf(JObject json, string field) => json["data"]![field]!.Value<string>()!;

    // The project's sources with one file replaced and one added, compiled.
    private static async Task<List<string>> ErrorsAfter(AdhocWorkspace workspace, string replacedFileName,
        string replacedContent, string? addedFileName, string? addedContent)
    {
        var document = DocumentNamed(workspace, replacedFileName);
        var solution = workspace.CurrentSolution.WithDocumentText(document.Id,
            Microsoft.CodeAnalysis.Text.SourceText.From(replacedContent));
        if (addedFileName != null)
        {
            solution = solution.GetProject(document.Project.Id)!
                .AddDocument(addedFileName, addedContent!).Project.Solution;
        }
        var errors = new List<string>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            errors.AddRange(compilation!.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{Path.GetFileName(d.Location.SourceTree?.FilePath)} {d.Id}: {d.GetMessage()}"));
        }
        return errors;
    }

    private const string ProgramSource = """
        using System;

        Console.WriteLine(new Helper().Name);

        public class Helper { public string Name => "h"; }

        public record Rec(int X);

        """;

    [Fact]
    public async Task MoveFromAProgramWithTopLevelStatements_LeavesTheStatementsBehind()
    {
        var (service, workspace, _) = LoadProjects(OutputKind.ConsoleApplication, Array.Empty<string>(),
            ("App", Array.Empty<string>(), new[] { (ProgramSource, "Program.cs") }));

        var json = Json(await service.MoveTypeToFileAsync("Helper"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        ContentOf(json, "newFileContent").Should().Be("public class Helper { public string Name => \"h\"; }\n");
        ContentOf(json, "updatedOriginContent").Should().Be(Edited(ProgramSource,
            ("public class Helper { public string Name => \"h\"; }\n\n", "")));
        (await ErrorsAfter(workspace, "Program.cs", ContentOf(json, "updatedOriginContent"),
            "Helper.cs", ContentOf(json, "newFileContent"))).Should().BeEmpty();
    }

    private const string AsmAttrSource = """
        using System;

        [assembly: CLSCompliant(true)]

        namespace R;

        public class First { }

        public class Second { }

        """;

    [Fact]
    public async Task MoveFromAFileWithAssemblyAttributes_LeavesTheAttributesBehind()
    {
        var (service, workspace, _) = LoadProjects(OutputKind.DynamicallyLinkedLibrary, Array.Empty<string>(),
            ("R", Array.Empty<string>(), new[] { (AsmAttrSource, "AsmAttr.cs") }));

        var json = Json(await service.MoveTypeToFileAsync("R.Second"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        ContentOf(json, "newFileContent").Should().Be("namespace R;\n\npublic class Second { }\n");
        ContentOf(json, "updatedOriginContent").Should().Be(Edited(AsmAttrSource, ("\npublic class Second { }\n", "")));
        (await ErrorsAfter(workspace, "AsmAttr.cs", ContentOf(json, "updatedOriginContent"),
            "Second.cs", ContentOf(json, "newFileContent"))).Should().BeEmpty();
    }

    private const string RegionsSource = """
        namespace R
        {
            #region Types
            public class RA { }
            #endregion

            public class RB { }

        #if FEATURE
            public class RC { }
        #endif
        }

        """;

    [Fact]
    public async Task MoveOutOfARegion_KeepsEveryDirectiveBalancedInBothFiles()
    {
        var (service, workspace, _) = LoadProjects(OutputKind.DynamicallyLinkedLibrary, new[] { "FEATURE" },
            ("R", Array.Empty<string>(), new[] { (RegionsSource, "Regions.cs") }));

        var json = Json(await service.MoveTypeToFileAsync("R.RA"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        ContentOf(json, "newFileContent").Should().Be(
            "namespace R\n{\n    #region Types\n    public class RA { }\n    #endregion\n\n#if FEATURE\n#endif\n}\n");
        ContentOf(json, "updatedOriginContent").Should().Be(
            "namespace R\n{\n    #region Types\n    #endregion\n\n    public class RB { }\n\n#if FEATURE\n    public class RC { }\n#endif\n}\n");
        (await ErrorsAfter(workspace, "Regions.cs", ContentOf(json, "updatedOriginContent"),
            "RA.cs", ContentOf(json, "newFileContent"))).Should().BeEmpty();
    }

    private const string HeaderSource = """
        // Copyright header.
        #nullable enable

        public class First { public string? Name; }

        public class Second { public string? Other; }

        """;

    [Fact]
    public async Task MovingTheFirstType_KeepsTheFileHeaderAndNullableContextInBothFiles()
    {
        var (service, _, _) = LoadProjects(OutputKind.DynamicallyLinkedLibrary, Array.Empty<string>(),
            ("R", Array.Empty<string>(), new[] { (HeaderSource, "Pair.cs") }));

        var json = Json(await service.MoveTypeToFileAsync("First"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        ContentOf(json, "newFileContent").Should().Be(
            "// Copyright header.\n#nullable enable\n\npublic class First { public string? Name; }\n");
        ContentOf(json, "updatedOriginContent").Should().Be(
            "// Copyright header.\n#nullable enable\n\npublic class Second { public string? Other; }\n");
    }

    private const string DeadSource = """
        namespace R;

        public class Dead
        {
            public int Live() => 1;

            #region Helpers
            private void Unused() { }
            #endregion
        }

        """;

    [Fact]
    public async Task RemovingAMemberInARegion_KeepsTheRegionBalanced()
    {
        var (service, workspace, _) = LoadProjects(OutputKind.DynamicallyLinkedLibrary, Array.Empty<string>(),
            ("R", Array.Empty<string>(), new[] { (DeadSource, "Dead.cs") }));

        var json = Json(await service.RemoveUnusedCodeAsync(symbolNames: new List<string> { "Unused" }));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var file = ((JArray)json["data"]!["files"]!).Single();
        file["updatedContent"]!.Value<string>().Should().Be(
            "namespace R;\n\npublic class Dead\n{\n    public int Live() => 1;\n\n    #region Helpers\n    #endregion\n}\n");
        (await ErrorsAfter(workspace, "Dead.cs", file["updatedContent"]!.Value<string>()!, null, null)).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveUnusedCode_WithAnUnknownProject_IsRefused()
    {
        var (service, _, _) = LoadProjects(OutputKind.DynamicallyLinkedLibrary, Array.Empty<string>(),
            ("R", Array.Empty<string>(), new[] { (DeadSource, "Dead.cs") }));

        var json = Json(await service.RemoveUnusedCodeAsync(projectName: "Nope"));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be("Project not found: Nope");
    }

    private const string PrimSource = """
        namespace R;

        public class Base(int x) { public int X => x; }

        public class Prim(int seed) : Base(seed), System.IDisposable
        {
            public int A() => seed;
            public int B() => seed + 1;
            public void Dispose() { }
        }

        """;

    [Fact]
    public async Task SplittingAPrimaryConstructorType_GivesTheNewHalfNoParameterOrBaseList()
    {
        var (service, workspace, _) = LoadProjects(OutputKind.DynamicallyLinkedLibrary, Array.Empty<string>(),
            ("R", Array.Empty<string>(), new[] { (PrimSource, "Prim.cs") }));

        var json = Json(await service.SplitTypeAsync("R.Prim", new List<string> { "B" }));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        ContentOf(json, "newFileContent").Should().Be(
            "namespace R;\n\npublic partial class Prim\n{\n    public int B() => seed + 1;\n}\n");
        ContentOf(json, "updatedOriginContent").Should().Be(Edited(PrimSource,
            ("public class Prim(int seed)", "public partial class Prim(int seed)"),
            ("    public int B() => seed + 1;\n", "")));
        (await ErrorsAfter(workspace, "Prim.cs", ContentOf(json, "updatedOriginContent"),
            "Prim.Partial.cs", ContentOf(json, "newFileContent"))).Should().BeEmpty();
    }

    private const string AttributedSource = """
        namespace R
        {
        #if FEATURE
            /// <summary>Doc.</summary>
            [System.Serializable]
            public class Attributed
            {
                public int A() => 1;
                public int B() => 2;
            }
        #endif
        }

        """;

    [Fact]
    public async Task SplittingAnAttributedTypeInsideAnIf_KeepsTheDirectiveButNotTheDocOrAttributesOnTheNewHalf()
    {
        var (service, workspace, _) = LoadProjects(OutputKind.DynamicallyLinkedLibrary, new[] { "FEATURE" },
            ("R", Array.Empty<string>(), new[] { (AttributedSource, "Attributed.cs") }));

        var json = Json(await service.SplitTypeAsync("R.Attributed", new List<string> { "B" }));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        ContentOf(json, "newFileContent").Should().Be(
            "namespace R\n{\n#if FEATURE\n    public partial class Attributed\n    {\n        public int B() => 2;\n    }\n#endif\n}\n");
        ContentOf(json, "updatedOriginContent").Should().Be(Edited(AttributedSource,
            ("    public class Attributed", "    public partial class Attributed"),
            ("        public int B() => 2;\n", "")));
        (await ErrorsAfter(workspace, "Attributed.cs", ContentOf(json, "updatedOriginContent"),
            "Attributed.Partial.cs", ContentOf(json, "newFileContent"))).Should().BeEmpty();
    }
}
