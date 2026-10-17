using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;
using static SharpLensMcp.Tests.RefactoringTestKit;

namespace SharpLensMcp.Tests;

// generate_test_stub writes its stub for the test project that references the system
// under test (never merely the first project with a test attribute), closes a generic
// SUT and a generic method over type arguments that satisfy their constraints
// (self-referential ones included), refuses a target the test project cannot call, and
// gives every ref argument a local of its own. The stub compiles in that test project.
public class TestStubTargetingTests
{
    private const string LibSource = """
        namespace Lib
        {
            public class Box<T>
            {
                public T Value { get; set; } = default!;
                public T Get() => Value;
            }

            public class Ranked<T> where T : System.IComparable<T>
            {
                public T Max(T a, T b) => a.CompareTo(b) >= 0 ? a : b;
                public U Pick<U>(U x) where U : System.IComparable<U> => x;
            }

            public class Holder
            {
                private int Secret() => 1;
                internal int Inner() => 2;
                public int Open() => Secret() + Inner();
            }

            public class RefHost
            {
                public RefHost(ref int x) { }
                public void Take(ref int x) { x++; }
            }
        }
        """;

    private const string FakeXunit = """
        namespace Xunit
        {
            public class FactAttribute : System.Attribute { }
        }
        """;

    private const string FakeNUnit = """
        namespace NUnit.Framework
        {
            public class TestAttribute : System.Attribute { }
            public class TestFixtureAttribute : System.Attribute { }
        }
        """;

    // Unrelated.Tests (xUnit, no reference to Lib) is the first project with a test
    // attribute; Lib.Tests (NUnit) is the one that references Lib.
    private static (RoslynService Service, AdhocWorkspace Workspace, string Root) Load() =>
        LoadProjects(OutputKind.DynamicallyLinkedLibrary, Array.Empty<string>(),
            ("Unrelated.Tests", Array.Empty<string>(), new[] { (FakeXunit, "Xunit.cs") }),
            ("Lib", Array.Empty<string>(), new[] { (LibSource, "Lib.cs") }),
            ("Lib.Tests", new[] { "Lib" }, new[] { (FakeNUnit, "NUnit.cs") }));

    private static async Task<List<string>> ErrorsInTestProject(AdhocWorkspace workspace, string testCode)
    {
        var project = workspace.CurrentSolution.Projects.Single(p => p.Name == "Lib.Tests");
        var compilation = await project.AddDocument("Stub.cs", testCode).Project.GetCompilationAsync();
        return compilation!.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id}: {d.GetMessage()}")
            .ToList();
    }

    [Fact]
    public async Task GenericSut_IsClosedOverObject_InTheTestProjectThatReferencesIt()
    {
        var (service, workspace, root) = Load();

        var json = Json(await service.GenerateTestStubAsync("Lib.Box", "Get"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        data["framework"]!.Value<string>().Should().Be("nunit");
        data["frameworkDetected"]!.Value<bool>().Should().BeTrue();
        data["suggestedFilePath"]!.Value<string>().Should().Be(Reported(Path.Combine(root, "Lib.Tests", "BoxTests.cs")));
        var code = data["testCode"]!.Value<string>()!.ReplaceLineEndings("\n");
        code.Should().Be("""
            using NUnit.Framework;

            namespace Lib.Tests;

            [TestFixture]
            public class BoxTests
            {
                [Test]
                public void Get_Behavior()
                {
                    // Arrange
                    var sut = new global::Lib.Box<object>();

                    // Act
                    var result = sut.Get();

                    // Assert
                    // TODO: assert the expected behavior
                }
            }

            """);
        (await ErrorsInTestProject(workspace, code)).Should().BeEmpty();
    }

    [Fact]
    public async Task SelfReferentialConstraints_GetATypeThatSatisfiesThem()
    {
        var (service, workspace, _) = Load();

        var json = Json(await service.GenerateTestStubAsync("Lib.Ranked", "Pick"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var code = json["data"]!["testCode"]!.Value<string>()!.ReplaceLineEndings("\n");
        code.Should().Contain("        var sut = new global::Lib.Ranked<int>();\n");
        code.Should().Contain("        var result = sut.Pick<int>(x: default);\n");
        (await ErrorsInTestProject(workspace, code)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Secret", "'Lib.Holder.Secret()' is not accessible from Lib.Tests: it is private")]
    [InlineData("Inner", "'Lib.Holder.Inner()' is not accessible from Lib.Tests: it is internal")]
    public async Task TargetTheTestProjectCannotCall_IsRefused(string method, string message)
    {
        var (service, _, _) = Load();

        var json = Json(await service.GenerateTestStubAsync("Lib.Holder", method));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be(message);
    }

    [Fact]
    public async Task SameNamedRefParameters_GetDistinctLocals()
    {
        var (service, workspace, _) = Load();

        var json = Json(await service.GenerateTestStubAsync("Lib.RefHost", "Take"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var code = json["data"]!["testCode"]!.Value<string>()!.ReplaceLineEndings("\n");
        code.Should().Contain("""
                    // Arrange
                    int xRef = default;
                    int xRef2 = default;
                    var sut = new global::Lib.RefHost(x: ref xRef);

                    // Act
                    sut.Take(x: ref xRef2);

            """);
        (await ErrorsInTestProject(workspace, code)).Should().BeEmpty();
    }
}
