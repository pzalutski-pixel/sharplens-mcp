using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Position resolution (behind every filePath/line/column tool) resolves the symbol
// the cursor's own token denotes, as an editor's go-to-definition does: a modifier
// or the leading indentation of a declaration line → that declaration (a field
// included); a cref → its target; a caret just past an identifier → that identifier.
// Doc-comment text, punctuation, blank lines and end-of-file denote no symbol and
// fail with SYMBOL_NOT_FOUND — never the enclosing or the next declaration — with a
// hint naming the nearest resolvable token on the line by its line-relative column.
// A path that cannot name a file (NUL, empty) is INVALID_PARAMETER.
public class PositionResolutionTests
{
    internal const string Code = @"// fixture header
using System.Text;

namespace Lib
{
    public class Widget
    {
        public void Ping() { }
    }

    /// <summary>Holder; see <see cref=""Widget""/>.</summary>
    public class CrefHost
    {
        private readonly int _field = 1;
        private int _a, _b;
        public int Read() => _field;
        public StringBuilder Make() => new StringBuilder();
    }
}

";

    internal static readonly string[] Lines = Code.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    // The zero-based (line, column) of `token` on the first line containing `lineMarker`.
    internal static (int line, int column) At(string lineMarker, string token, int offsetInToken = 0)
    {
        var line = Array.FindIndex(Lines, l => l.Contains(lineMarker, StringComparison.Ordinal));
        line.Should().BeGreaterOrEqualTo(0, $"the fixture must contain '{lineMarker}'");
        var column = Lines[line].IndexOf(token, StringComparison.Ordinal);
        column.Should().BeGreaterOrEqualTo(0, $"line {line} must contain '{token}'");
        return (line, column + offsetInToken);
    }

    private static (RoslynService service, string filePath) CreateService()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Refs.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, document.FilePath!);
    }

    private static async Task<JObject> SymbolInfoAsync((int line, int column) position)
    {
        var (service, filePath) = CreateService();
        return JObject.FromObject(await service.GetSymbolInfoAsync(filePath, position.line, position.column));
    }

    private static void ShouldResolveTo(JObject json, string name, string kind)
    {
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["name"]!.Value<string>().Should().Be(name);
        json["data"]!["kind"]!.Value<string>().Should().Be(kind);
    }

    private static void ShouldFindNoSymbol(JObject json)
    {
        json["success"]!.Value<bool>().Should().BeFalse(json.ToString());
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("readonly")]
    public async Task FieldModifier_ResolvesTheField(string modifier)
    {
        ShouldResolveTo(await SymbolInfoAsync(At("private readonly int _field", modifier)), "_field", "Field");
    }

    [Fact]
    public async Task DeclarationLineIndentation_ResolvesTheDeclaration()
    {
        ShouldResolveTo(await SymbolInfoAsync((At("private readonly int _field", "private").line, 0)), "_field", "Field");
    }

    [Fact]
    public async Task CrefName_ResolvesTheCrefTarget()
    {
        var json = await SymbolInfoAsync(At("<see cref=", "Widget\"", offsetInToken: 2));
        ShouldResolveTo(json, "Widget", "NamedType");
        json["data"]!["location"]!["line"]!.Value<int>().Should().Be(At("public class Widget", "Widget").line);
    }

    [Fact]
    public async Task CaretJustPastAnIdentifier_ResolvesThatIdentifier()
    {
        ShouldResolveTo(await SymbolInfoAsync(At("public void Ping()", "()")), "Ping", "Method");
    }

    [Fact]
    public async Task FieldReference_ResolvesTheField()
    {
        ShouldResolveTo(await SymbolInfoAsync(At("public int Read()", "_field")), "_field", "Field");
    }

    [Fact]
    public async Task DocumentationText_FindsNoSymbol()
    {
        ShouldFindNoSymbol(await SymbolInfoAsync(At("<see cref=", "Holder")));
    }

    [Fact]
    public async Task OpenBraceLine_FindsNoSymbol()
    {
        var braceLine = At("public class CrefHost", "CrefHost").line + 1;
        Lines[braceLine].Trim().Should().Be("{");
        ShouldFindNoSymbol(await SymbolInfoAsync((braceLine, Lines[braceLine].IndexOf('{'))));
    }

    [Fact]
    public async Task ExpressionBodyArrow_FindsNoSymbol()
    {
        ShouldFindNoSymbol(await SymbolInfoAsync(At("public int Read()", "=>")));
    }

    [Fact]
    public async Task BlankLineAfterTheLastBrace_FindsNoSymbol()
    {
        ShouldFindNoSymbol(await SymbolInfoAsync((Lines.Length - 2, 0)));
    }

    [Fact]
    public async Task MultiVariableFieldModifier_FindsNoSymbolAndNamesTheVariables()
    {
        var json = await SymbolInfoAsync(At("private int _a, _b;", "private"));
        ShouldFindNoSymbol(json);
        json["error"]!["Hint"]!.Value<string>().Should().Be(
            "This declaration declares 2 variables (_a, _b); place the cursor on the variable's name.");
    }

    [Fact]
    public async Task UnresolvableToken_HintNamesTheNearestResolvableTokenByLineColumn()
    {
        var json = await SymbolInfoAsync(At("using System.Text;", "using"));
        ShouldFindNoSymbol(json);
        var (line, column) = At("using System.Text;", "System");
        json["error"]!["Hint"]!.Value<string>().Should().Be(
            $"Try positioning the cursor on 'System' (line {line}, column {column}).");
    }

    [Theory]
    [InlineData("Re\0fs.cs")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnnameablePath_ReturnsInvalidParameter(string filePath)
    {
        var (service, _) = CreateService();
        var json = JObject.FromObject(await service.GetSymbolInfoAsync(filePath, 0, 0));
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public void TryFindDocument_UnnameablePath_ReturnsNull()
    {
        var (service, _) = CreateService();
        service.TryFindDocument("Re\0fs.cs").Should().BeNull();
        service.TryFindDocument("").Should().BeNull();
    }
}
