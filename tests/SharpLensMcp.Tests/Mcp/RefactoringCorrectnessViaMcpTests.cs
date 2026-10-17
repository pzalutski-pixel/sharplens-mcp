using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;
using static SharpLensMcp.Tests.RefactoringTestKit;

namespace SharpLensMcp.Tests.Mcp;

// The refactoring-correctness behaviors through the MCP dispatcher: each tool is called
// as a client calls it (JSON-RPC tools/call, camelCase arguments, content[0].text
// unwrapped) against the same in-memory fixtures the unit tests pin, and the resulting
// source text, data fields and error codes are asserted exactly.
public class RefactoringCorrectnessViaMcpTests
{
    private static async Task<JToken> Data(McpHarnessOps ops, string tool, object arguments)
    {
        var inner = await ops.CallToolAsync(tool, arguments);
        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        return inner["data"]!;
    }

    private static async Task<JToken> Error(McpHarnessOps ops, string tool, object arguments, string code)
    {
        var inner = await ops.CallToolAsync(tool, arguments);
        inner["success"]!.Value<bool>().Should().BeFalse(inner.ToString());
        inner["error"]!["code"]!.Value<string>().Should().Be(code);
        return inner["error"]!;
    }

    private static int LineOf(string source, string marker) =>
        source.Split('\n').ToList().FindIndex(l => l.Contains(marker, StringComparison.Ordinal));

    // ---- change_signature ----

    [Fact]
    public async Task ChangeSignature_RenameFollowsEveryReference()
    {
        var (_, workspace, document) = Load(ChangeSignatureRebuildTests.RenameSource);

        var data = await Data(Wire(workspace), "change_signature", new
        {
            typeName = "Calc", memberName = "Twice", preview = false,
            changes = new[] { new { action = "rename", name = "x", newName = "value" } }
        });

        (await TextOf(workspace, document.Id)).Should().Be(ChangeSignatureRebuildTests.RenamedSource);
        data["callSitesUpdated"]!.Value<int>().Should().Be(1);
    }

    [Fact]
    public async Task ChangeSignature_ReorderKeepsUntouchedDefaultsAsWritten()
    {
        var (_, workspace, document) = Load(ChangeSignatureRebuildTests.PreserveSource);

        await Data(Wire(workspace), "change_signature", new
        {
            typeName = "Sig", memberName = "D", preview = false,
            changes = new[] { new { action = "reorder", order = new[] { "mode", "d", "m" } } }
        });

        (await TextOf(workspace, document.Id)).Should().Be(Edited(ChangeSignatureRebuildTests.PreserveSource,
            ("D(decimal m = 2.5m, double d = double.NaN, Other.Mode mode = Other.Mode.B)",
                "D(Other.Mode mode = Other.Mode.B, double d = double.NaN, decimal m = 2.5m)"),
            ("D(1m, 2.0, Other.Mode.A);", "D(Other.Mode.A, 2.0, 1m);")));
    }

    [Fact]
    public async Task ChangeSignature_MethodGroupIsReportedNotRewritten()
    {
        var (_, workspace, document) = Load(ChangeSignatureRebuildTests.GroupSource);

        var data = await Data(Wire(workspace), "change_signature", new
        {
            typeName = "Groups", memberName = "Work", preview = true,
            changes = new[] { new { action = "add", name = "n", type = "int", defaultValue = "0" } }
        });

        data["callSitesCount"]!.Value<int>().Should().Be(0);
        ((JArray)data["unrewrittenReferences"]!).Select(r => $"{r["line"]}:{r["column"]}").Should().Equal("13:54");
        (await TextOf(workspace, document.Id)).Should().Be(ChangeSignatureRebuildTests.GroupSource);
    }

    [Fact]
    public async Task ChangeSignature_AddAfterASkippedOptionalIsNamed()
    {
        var (_, workspace, document) = Load(ChangeSignatureRebuildTests.OptionalSource);

        await Data(Wire(workspace), "change_signature", new
        {
            typeName = "Opts", memberName = "Opt", preview = false,
            changes = new[] { new { action = "add", name = "c", type = "int", defaultValue = "7" } }
        });

        (await TextOf(workspace, document.Id)).Should().Be(ChangeSignatureRebuildTests.OptionalWithC);
    }

    [Fact]
    public async Task ChangeSignature_EmptyParamsListStaysEmpty()
    {
        var (_, workspace, document) = Load(ChangeSignatureRebuildTests.ParamsSource);

        await Data(Wire(workspace), "change_signature", new
        {
            typeName = "Packs", memberName = "P", preview = false,
            changes = new[] { new { action = "add", name = "k", type = "int", defaultValue = "0", position = 1 } }
        });

        (await TextOf(workspace, document.Id)).Should().Be(Edited(ChangeSignatureRebuildTests.ParamsSource,
            ("P(int a, params int[] rest)", "P(int a, int k = 0, params int[] rest)"),
            ("P(1);", "P(1, 0);"),
            ("P(1, 2, 3);", "P(1, 0, 2, 3);")));
    }

    [Fact]
    public async Task ChangeSignature_RemoveCascadesThroughTheInterface()
    {
        var (_, workspace, document) = Load(ChangeSignatureRebuildTests.HierarchySource);

        await Data(Wire(workspace), "change_signature", new
        {
            typeName = "Worker", memberName = "Do", preview = false,
            changes = new[] { new { action = "remove", name = "b" } }
        });

        (await TextOf(workspace, document.Id)).Should().Be(ChangeSignatureRebuildTests.HierarchyWithoutB);
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task ChangeSignature_RecursiveMethodApplies()
    {
        var (_, workspace, document) = Load(ChangeSignatureRebuildTests.RecursiveSource);

        await Data(Wire(workspace), "change_signature", new
        {
            typeName = "Rec", memberName = "Fact", preview = false,
            changes = new[] { new { action = "add", name = "depth", type = "int", defaultValue = "0" } }
        });

        (await TextOf(workspace, document.Id)).Should().Be(Edited(ChangeSignatureRebuildTests.RecursiveSource,
            ("Fact(int n) { return n <= 1 ? 1 : n * Fact(n - 1); }", "Fact(int n, int depth = 0) { return n <= 1 ? 1 : n * Fact(n - 1, 0); }"),
            ("Fact(5);", "Fact(5, 0);")));
    }

    [Fact]
    public async Task ChangeSignature_PositionMinusOneAppends_AndAnUnknownActionIsRejected()
    {
        var (_, workspace, document) = Load(ChangeSignatureRebuildTests.AddEndSource);
        var ops = Wire(workspace);

        var error = await Error(ops, "change_signature", new
        {
            typeName = "Ends", memberName = "AddEnd", preview = false,
            changes = new[] { new { action = "frobnicate", name = "a" } }
        }, ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be(
            "changes[0].action must be one of: add, remove, rename, reorder (got 'frobnicate')");

        await Data(ops, "change_signature", new
        {
            typeName = "Ends", memberName = "AddEnd", preview = false,
            changes = new[] { new { action = "add", name = "z", type = "int", defaultValue = "0", position = -1 } }
        });
        (await TextOf(workspace, document.Id)).Should().Be(Edited(ChangeSignatureRebuildTests.AddEndSource,
            ("AddEnd(int a)", "AddEnd(int a, int z = 0)"),
            ("AddEnd(1);", "AddEnd(1, 0);")));
    }

    [Fact]
    public async Task ChangeSignature_AChangeWithoutAnAction_IsInvalidParams()
    {
        var (_, workspace, _) = Load(ChangeSignatureRebuildTests.AddEndSource);

        var act = () => Wire(workspace).CallToolAsync("change_signature", new
        {
            typeName = "Ends", memberName = "AddEnd",
            changes = new[] { new { name = "a" } }
        });

        (await act.Should().ThrowAsync<McpJsonRpcException>()).Which.Code.Should().Be(-32602);
    }

    // ---- rename_symbol ----

    [Fact]
    public async Task RenameSymbol_ConflictIsPreviewedAndTheApplyRefused()
    {
        var (_, workspace, document) = Load(RenameConflictTests.ConflictSource);
        var ops = Wire(workspace);

        var preview = await Data(ops, "rename_symbol", new { typeName = "M.Ren", memberName = "Alpha", newName = "Beta", preview = true });
        ((JArray)preview["conflicts"]!).Select(c => $"{c["line"]}:{c["column"]} {c["kind"]}")
            .Should().Equal("5:19 unresolvable", "6:28 unresolved", "6:38 unresolved");

        var error = await Error(ops, "rename_symbol",
            new { typeName = "M.Ren", memberName = "Alpha", newName = "Beta", preview = false }, ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be(
            "Renaming 'Alpha' to 'Beta' leaves 3 unresolved conflicts; nothing was written");
        (await TextOf(workspace, document.Id)).Should().Be(RenameConflictTests.ConflictSource);
    }

    // ---- extract_method ----

    [Fact]
    public async Task ExtractMethod_InAStaticMethodIsStatic()
    {
        var source = ExtractMethodFlowTests.StaticHostSource;
        var (_, workspace, document) = Load(source);

        var data = await Data(Wire(workspace), "extract_method", new
        {
            filePath = document.FilePath, startLine = LineOf(source, "int b = a + 1;"),
            endLine = LineOf(source, "int c = b * 2;"), methodName = "M1", preview = false
        });

        data["signature"]!.Value<string>().Should().Be("private static int M1(int a)");
        (await TextOf(workspace, document.Id)).Should().Contain("    private static int M1(int a)\n");
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractMethod_BreakLeavingTheSelectionIsRefused()
    {
        var source = ExtractMethodFlowTests.JumpSource;
        var (_, workspace, document) = Load(source);
        var line = LineOf(source, "if (x < 0) break;");

        var error = await Error(Wire(workspace), "extract_method", new
        {
            filePath = document.FilePath, startLine = line, endLine = line, methodName = "M2", preview = false
        }, ErrorCodes.InvalidParameter);

        error["message"]!.Value<string>().Should().Be(
            "The selection contains 'break' (line 11), which transfers control out of the selection");
        (await TextOf(workspace, document.Id)).Should().Be(source);
    }

    [Fact]
    public async Task ExtractMethod_AwaitForeachIsAsync()
    {
        var source = ExtractMethodFlowTests.AsyncSource;
        var (_, workspace, document) = Load(source);
        var line = LineOf(source, "await foreach");

        var data = await Data(Wire(workspace), "extract_method", new
        {
            filePath = document.FilePath, startLine = line, endLine = line + 3, methodName = "M4", preview = true
        });

        data["signature"]!.Value<string>().Should().Be("private async Task M4(IAsyncEnumerable<int> xs)");
        data["replacementCode"]!.Value<string>().Should().Be("await M4(xs);");
    }

    [Fact]
    public async Task ExtractMethod_ParameterWriteFlowsBack_AndAHoistedLocalStaysDeclared()
    {
        var source = ExtractMethodFlowTests.WriteBackSource;
        var (_, workspace, document) = Load(source);
        var ops = Wire(workspace);

        var write = await Data(ops, "extract_method", new
        {
            filePath = document.FilePath, startLine = LineOf(source, "p = p * 2;"),
            endLine = LineOf(source, "p = p * 2;"), methodName = "M3", preview = true
        });
        write["replacementCode"]!.Value<string>().Should().Be("p = M3(p);");

        var hoist = await Data(ops, "extract_method", new
        {
            filePath = document.FilePath, startLine = LineOf(source, "int y = 0;"),
            endLine = LineOf(source, "Console.WriteLine(y);"), methodName = "M6", preview = true
        });
        hoist["replacementCode"]!.Value<string>().Should().Be("int y;\nM6();");
    }

    [Fact]
    public async Task ExtractMethod_NonSiblingSelectionIsRefused()
    {
        var source = ExtractMethodFlowTests.NestSource;
        var (_, workspace, document) = Load(source);

        var error = await Error(Wire(workspace), "extract_method", new
        {
            filePath = document.FilePath, startLine = LineOf(source, "Console.WriteLine(\"b\");"),
            endLine = LineOf(source, "Console.WriteLine(\"c\");"), methodName = "M7", preview = false
        }, ErrorCodes.InvalidParameter);

        error["message"]!.Value<string>().Should().Be(
            "The selection must cover complete statements of one block, but lines 11 and 13 belong to different blocks");
    }

    // ---- code generation ----

    [Fact]
    public async Task AddNullChecks_SkipsOutParameters()
    {
        var (_, workspace, document) = Load(CodegenCorrectnessTests.OutsSource);

        var data = await Data(Wire(workspace), "add_null_checks", new { typeName = "Gen.Outs", memberName = "TryGet", preview = false });

        ((JArray)data["skippedOut"]!).Select(p => p.Value<string>()).Should().Equal("value");
        (await TextOf(workspace, document.Id)).Should().Be(Edited(CodegenCorrectnessTests.OutsSource,
            ("        {\n            value = key;",
             "        {\n            global::System.ArgumentNullException.ThrowIfNull(key);\n\n            value = key;")));
    }

    [Fact]
    public async Task AddNullChecks_NullDefaultIsSkipped()
    {
        var (_, workspace, _) = Load(CodegenCorrectnessTests.OutsSource);

        var data = await Data(Wire(workspace), "add_null_checks", new { typeName = "Gen.Outs", memberName = "Opt", preview = true });

        data["message"]!.Value<string>().Should().Be("No reference-type parameter needs a null check");
        ((JArray)data["skippedNullable"]!).Select(p => p.Value<string>()).Should().Equal("s");
    }

    [Fact]
    public async Task GenerateEqualityMembers_ComparesWithTheDefaultComparerWhereNeeded()
    {
        var (_, workspace, _) = Load(CodegenCorrectnessTests.IndexerSource);

        var data = await Data(Wire(workspace), "generate_equality_members",
            new { typeName = "Gen.WithIndexer", includeOperators = false, preview = false });

        ((JArray)data["membersCompared"]!).Select(m => m.Value<string>()).Should().Equal("_a", "Count");
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateConstructor_QualifiesSameNamedFields()
    {
        var (_, workspace, _) = Load(CodegenCorrectnessTests.Ctor1Source);

        var data = await Data(Wire(workspace), "generate_constructor", new { typeName = "Gen.Ctor1", includeProperties = true });

        data["constructorCode"]!.Value<string>().Should().Be(
            "public Ctor1(string name, int count, string title, int size, int hidden)\n{\n" +
            "    this.name = name;\n    this.count = count;\n    Title = title;\n    Size = size;\n    Hidden = hidden;\n}");
    }

    [Fact]
    public async Task ExtractInterface_InvalidNameIsRejected()
    {
        var (_, workspace, _) = Load(CodegenCorrectnessTests.Svc2Source);

        var error = await Error(Wire(workspace), "extract_interface",
            new { typeName = "Gen.Svc2", interfaceName = "I Bad-Name" }, ErrorCodes.InvalidParameter);

        error["message"]!.Value<string>().Should().Be("'I Bad-Name' is not a valid C# identifier");
    }

    [Fact]
    public async Task ExtractInterface_CopiesOnlyPublicAccessors()
    {
        var (_, workspace, _) = Load(CodegenCorrectnessTests.Svc2Source);

        var data = await Data(Wire(workspace), "extract_interface", new { typeName = "Gen.Svc2", interfaceName = "ISvc2" });

        data["interfaceCode"]!.Value<string>().Should().Be(
            "namespace Gen\n{\n    public interface ISvc2\n    {\n" +
            "        global::System.Int32 P { get; }\n\n" +
            "        global::System.String Q { set; }\n\n" +
            "        global::System.Threading.Tasks.Task DoAsync();\n" +
            "        void V();\n" +
            "    }\n}");
    }

    // ---- generate_test_stub ----

    private const string StubLib = """
        namespace Lib
        {
            public class Box<T>
            {
                public T Get() => default!;
            }

            public class Holder
            {
                private int Secret() => 1;
                public int Open() => Secret();
            }
        }
        """;

    [Fact]
    public async Task GenerateTestStub_ClosesAGenericSut_AndRefusesAPrivateTarget()
    {
        var (_, workspace, _) = LoadProjects(OutputKind.DynamicallyLinkedLibrary, Array.Empty<string>(),
            ("Lib", Array.Empty<string>(), new[] { (StubLib, "Lib.cs") }),
            ("Lib.Tests", new[] { "Lib" }, new[] { ("namespace Xunit { public class FactAttribute : System.Attribute { } }", "Xunit.cs") }));
        var ops = Wire(workspace);

        var data = await Data(ops, "generate_test_stub", new { typeName = "Lib.Box", methodName = "Get" });
        data["framework"]!.Value<string>().Should().Be("xunit");
        data["testCode"]!.Value<string>()!.ReplaceLineEndings("\n").Should().Contain(
            "        var sut = new global::Lib.Box<object>();\n");

        var error = await Error(ops, "generate_test_stub", new { typeName = "Lib.Holder", methodName = "Secret" },
            ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be("'Lib.Holder.Secret()' is not accessible from Lib.Tests: it is private");
    }

    // ---- reorganization ----

    private const string RegionsSource = """
        namespace R
        {
            #region Types
            public class RA { }
            #endregion

            public class RB { }
        }

        """;

    [Fact]
    public async Task MoveTypeToFile_KeepsTheRegionBalanced()
    {
        var (_, workspace, _) = LoadProjects(OutputKind.DynamicallyLinkedLibrary, Array.Empty<string>(),
            ("R", Array.Empty<string>(), new[] { (RegionsSource, "Regions.cs") }));

        var data = await Data(Wire(workspace), "move_type_to_file", new { typeName = "R.RA" });

        data["newFileContent"]!.Value<string>().Should().Be(
            "namespace R\n{\n    #region Types\n    public class RA { }\n    #endregion\n}\n");
        data["updatedOriginContent"]!.Value<string>().Should().Be(
            "namespace R\n{\n    #region Types\n    #endregion\n\n    public class RB { }\n}\n");
    }

    private const string PrimSource = """
        namespace R;

        public class Prim(int seed)
        {
            public int A() => seed;
            public int B() => seed + 1;
        }

        """;

    [Fact]
    public async Task SplitType_NewHalfHasNoPrimaryConstructorParameters()
    {
        var (_, workspace, _) = LoadProjects(OutputKind.DynamicallyLinkedLibrary, Array.Empty<string>(),
            ("R", Array.Empty<string>(), new[] { (PrimSource, "Prim.cs") }));

        var data = await Data(Wire(workspace), "split_type", new { typeName = "R.Prim", memberNames = new[] { "B" } });

        data["newFileContent"]!.Value<string>().Should().Be(
            "namespace R;\n\npublic partial class Prim\n{\n    public int B() => seed + 1;\n}\n");
    }

    [Fact]
    public async Task RemoveUnusedCode_UnknownProjectIsRejected()
    {
        var (_, workspace, _) = Load("namespace R { public class C { private void Unused() { } } }");

        var error = await Error(Wire(workspace), "remove_unused_code", new { projectName = "Nope" }, ErrorCodes.InvalidParameter);

        error["message"]!.Value<string>().Should().Be("Project not found: Nope");
    }

    [Fact]
    public async Task AddMissingImports_ResolvesAnAttributeName()
    {
        var source = "namespace R;\n\npublic class Imp\n{\n    [Obsolete(\"x\")]\n    public int P { get; set; }\n}\n";
        var (_, workspace, document) = Load(source, "Imp.cs");

        var data = await Data(Wire(workspace), "add_missing_imports", new { filePath = document.FilePath });

        ((JArray)data["added"]!).Select(a => $"{a["name"]} -> {a["namespace"]}").Should().Equal("Obsolete -> System");
        data["updatedContent"]!.Value<string>().Should().Be("using System;\n" + source);
    }

    [Fact]
    public async Task AddMissingImports_ReportsEveryNameASharedUsingFixes()
    {
        var source = "namespace R;\n\npublic class Texts\n{\n    public StringBuilder Make() => new StringBuilder();\n    public Encoding Utf8() => Encoding.UTF8;\n}\n";
        var (_, workspace, document) = Load(source, "Texts.cs");

        var data = await Data(Wire(workspace), "add_missing_imports", new { filePath = document.FilePath });

        ((JArray)data["added"]!).Select(a => $"{a["name"]} -> {a["namespace"]}")
            .Should().Equal("Encoding -> System.Text", "StringBuilder -> System.Text");
        data["updatedContent"]!.Value<string>().Should().Be("using System.Text;\n" + source);
    }

    // ---- code actions ----

    [Fact]
    public async Task ExtractVariable_NeverFallsBackToIntroducingAField()
    {
        var source = "namespace CA;\n\npublic class Fld\n{\n    public int F = Compute(1) + 2;\n    public static int Compute(int x) => x;\n}\n";
        var (_, workspace, document) = Load(source, "Fld.cs");

        var error = await Error(Wire(workspace), "extract_variable", new
        {
            filePath = document.FilePath, line = 4, column = 19, endLine = 4, endColumn = 29, preview = false
        }, ErrorCodes.SymbolNotFound);

        error["message"]!.Value<string>().Should().Be("No 'extract variable' action found at this position");
        (await TextOf(workspace, document.Id)).Should().Be(source);
    }

    [Fact]
    public async Task ImplementMissingMembers_WorksAtTheClassDeclaration()
    {
        var source = "using System;\n\nnamespace CA;\n\npublic interface IShape { double Area(); }\n\npublic class Circle : IShape\n{\n}\n";
        var (_, workspace, document) = Load(source, "Shapes.cs");

        await Data(Wire(workspace), "implement_missing_members",
            new { filePath = document.FilePath, line = 6, column = 13, preview = false });

        (await TextOf(workspace, document.Id)).Should().Be(
            "using System;\n\nnamespace CA;\n\npublic interface IShape { double Area(); }\n\npublic class Circle : IShape\n{\n" +
            "    public double Area()\n    {\n        throw new NotImplementedException();\n    }\n}\n");
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task FixAll_CountsAMultiTargetedDiagnosticOnce()
    {
        const string multi = "namespace Multi\n{\n    public class Holder\n    {\n        public int One()\n        {\n            int unused = 5;\n            return 1;\n        }\n    }\n}\n";
        using var fixture = new MultiTargetSolutionFixture(multi, "namespace Consumer { public class App { } }");

        var data = await Data(Wire(fixture.Workspace), "fix_all", new { diagnosticId = "CS0219" });

        data["diagnosticsMatched"]!.Value<int>().Should().Be(1);
        ((JArray)data["files"]!).Select(f => f["updatedContent"]!.Value<string>()).Should().Equal(
            multi.Replace("            int unused = 5;\n", ""));
    }

    // ---- compound ----

    [Fact]
    public async Task GetTypeOverview_BaseTypeCountMatchesTheListedChain()
    {
        var (_, workspace, _) = Load("namespace M { public class L1 { } public class L2 : L1 { } public class L3 : L2 { } public struct Val { } }");
        var ops = Wire(workspace);

        var chain = await Data(ops, "get_type_overview", new { typeName = "M.L3" });
        ((JArray)chain["baseTypes"]!).Select(b => b.Value<string>()).Should().Equal("M.L2", "M.L1");
        chain["baseTypeCount"]!.Value<int>().Should().Be(2);

        var value = await Data(ops, "get_type_overview", new { typeName = "M.Val" });
        ((JArray)value["baseTypes"]!).Should().BeEmpty();
        value["baseTypeCount"]!.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeMethod_ResolvesThroughTheSharedOverloadSet()
    {
        var (_, workspace, _) = Load("namespace M { public class Over { public void Do(string s) { } public void Do(int i) { } public int Prop { get; set; } } }");
        var ops = Wire(workspace);

        var ambiguous = await Error(ops, "analyze_method", new { typeName = "M.Over", methodName = "Do" }, ErrorCodes.AmbiguousMatch);
        ambiguous["message"]!.Value<string>().Should().Be("Member 'Do' has 2 overloads in 'M.Over'");

        var property = await Error(ops, "analyze_method", new { typeName = "M.Over", methodName = "Prop" }, ErrorCodes.NotAMethod);
        property["message"]!.Value<string>().Should().Be("'M.Over.Prop' is not a method");

        var limit = await Error(ops, "analyze_method", new { typeName = "M.Over", methodName = "Do", overloadIndex = 0, maxCallers = 0 },
            ErrorCodes.InvalidParameter);
        limit["message"]!.Value<string>().Should().Be("maxCallers must be between 1 and 1000 (got 0)");
    }

    // ---- shadow notice ----

    public static IEnumerable<object[]> NoticeCalls => new[]
    {
        new object[] { "rename_symbol", new { typeName = "Timer", memberName = "Read", newName = "ReadCount", preview = true } },
        new object[] { "change_signature", new { typeName = "Timer", memberName = "Tick", preview = true,
            changes = new[] { new { action = "rename", name = "label", newName = "name" } } } },
        new object[] { "extract_interface", new { typeName = "Timer", interfaceName = "ITimer" } },
        new object[] { "generate_constructor", new { typeName = "Timer" } },
        new object[] { "add_null_checks", new { typeName = "Timer", memberName = "Tick", preview = true } },
        new object[] { "generate_equality_members", new { typeName = "Timer", preview = true } },
        new object[] { "generate_test_stub", new { typeName = "Timer", methodName = "Tick" } },
        new object[] { "move_type_to_file", new { typeName = "Timer" } },
        new object[] { "split_type", new { typeName = "Timer", memberNames = new[] { "Read" } } },
        new object[] { "get_type_overview", new { typeName = "Timer" } },
        new object[] { "analyze_method", new { typeName = "Timer", methodName = "Tick" } },
    };

    [Theory]
    [MemberData(nameof(NoticeCalls))]
    public async Task ShadowingTypeName_SurfacesTheNotice(string tool, object arguments)
    {
        var (_, workspace, _) = Load(ShadowNoticeWiringTests.Source, "App.cs");

        var data = await Data(Wire(workspace), tool, arguments);

        data["notice"]!.Value<string>().Should().Be(ShadowNoticeWiringTests.Notice);
    }
}
