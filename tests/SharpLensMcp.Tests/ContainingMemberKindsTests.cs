using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;
using static SharpLensMcp.Tests.MultiTargetNavigationSources;

namespace SharpLensMcp.Tests;

// get_containing_member resolves a position to the member declaration around it for
// every member declaration kind — operators, conversions, indexers, events (field-like
// and with accessors), fields (their initializers, one declarator of several), enum
// members, finalizers, delegates — and to the type itself for a position in a type
// declaration outside its members (records and record structs included). memberKind
// uses the kind vocabulary of search_symbols. A position in a declaration of several
// fields that names none of them is ambiguous; one outside every type is not found.
public class ContainingMemberKindsTests
{
    internal const string Code = """
        namespace Lib
        {
            public delegate void Handler(int code);

            public record Person(string Name)
            {
                public int Age { get; init; }
                public static Person operator +(Person a, Person b) => a with { Age = a.Age + b.Age };
                public static implicit operator string(Person p) => p.Name;
                public string this[int i] { get { return Name + i; } }
                private int _f = Compute();
                static int Compute() => 1;
                public event System.EventHandler? Changed;
                public event System.EventHandler Explicit { add { } remove { } }
            }

            public record struct Point(int X, int Y)
            {
                public int Sum() => X + Y;
            }

            public enum Color { Red, Green }

            public class Holder
            {
                ~Holder() { }
                public int A = 1, B = 2;
            }
        }
        """;

    [Theory]
    [InlineData("public record Person", "record", "Person", "Record")]
    [InlineData("operator +(Person a", "with", "op_Addition", "Operator")]
    [InlineData("implicit operator string", "p.Name", "op_Implicit", "Conversion")]
    [InlineData("public string this[int i]", "Name + i", "this[]", "Indexer")]
    [InlineData("private int _f = Compute();", "Compute", "_f", "Field")]
    [InlineData("public event System.EventHandler? Changed", "Changed", "Changed", "Event")]
    [InlineData("public event System.EventHandler Explicit", "add", "Explicit", "Event")]
    [InlineData("public enum Color", "Red", "Red", "Field")]
    [InlineData("~Holder()", "{ }", "Finalize", "Finalizer")]
    [InlineData("public delegate void Handler", "Handler", "Handler", "Delegate")]
    [InlineData("public int Sum()", "X + Y", "Sum", "Method")]
    [InlineData("public record struct Point", "struct", "Point", "RecordStruct")]
    [InlineData("public int A = 1, B = 2;", "B", "B", "Field")]
    public async Task ResolvesTheMemberAroundThePosition(string lineMarker, string token, string memberName, string memberKind)
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Members.cs");
        var service = ToolResult.Load(workspace);
        var (line, column) = Pos(Code, lineMarker, token);

        var data = ToolResult.Data(await service.GetContainingMemberAsync(document.FilePath!, line, column));

        data["memberName"]!.Value<string>().Should().Be(memberName);
        data["memberKind"]!.Value<string>().Should().Be(memberKind);
    }

    [Fact]
    public async Task OperatorSpan_CoversTheWholeDeclaration()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Members.cs");
        var service = ToolResult.Load(workspace);
        var (line, column) = Pos(Code, "operator +(Person a", "with");

        var data = ToolResult.Data(await service.GetContainingMemberAsync(document.FilePath!, line, column));

        var (startLine, startColumn) = Pos(Code, "operator +(Person a", "public");
        data["span"]!["startLine"]!.Value<int>().Should().Be(startLine);
        data["span"]!["startColumn"]!.Value<int>().Should().Be(startColumn);
        data["span"]!["endLine"]!.Value<int>().Should().Be(startLine);
        data["containingType"]!.Value<string>().Should().Be("Lib.Person");
    }

    [Fact]
    public async Task SharedFieldDeclaration_TypePosition_IsAmbiguous_AndOutsideEveryType_IsNotFound()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Members.cs");
        var service = ToolResult.Load(workspace);
        var (fieldLine, fieldColumn) = Pos(Code, "public int A = 1, B = 2;", "int");
        var (namespaceLine, namespaceColumn) = Pos(Code, "namespace Lib", "namespace");

        ToolResult.Error(await service.GetContainingMemberAsync(document.FilePath!, fieldLine, fieldColumn), ErrorCodes.AmbiguousMatch);
        ToolResult.Error(await service.GetContainingMemberAsync(document.FilePath!, namespaceLine, namespaceColumn), ErrorCodes.SymbolNotFound);
    }

    [Theory]
    [InlineData("implicit operator string", "p.Name", "op_Implicit", "Conversion")]
    [InlineData("public record struct Point", "struct", "Point", "RecordStruct")]
    public async Task ResolvesTheMemberAroundThePosition_ViaMcp(string lineMarker, string token, string memberName, string memberKind)
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Members.cs");
        var host = new InMemoryToolHost(workspace);
        var (line, column) = Pos(Code, lineMarker, token);

        var data = await host.DataAsync("get_containing_member", new { filePath = document.FilePath, line, column });

        data["memberName"]!.Value<string>().Should().Be(memberName);
        data["memberKind"]!.Value<string>().Should().Be(memberKind);
    }
}
