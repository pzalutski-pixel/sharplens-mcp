using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_naming_violations over in-memory workspaces: the defaults path pins
// every deliberate violation in the probe source (and nothing else), and the
// editorconfig path proves a real analyzer-config document drives the rule
// set and flips rulesSource.
public class NamingViolationsTests
{
    private const string ProbeSource = """
        namespace NamingProbe;

        public class ProperHost
        {
            private int BadField;
            private int _goodField;

            public int GoodProperty { get; set; }

            public void lowercase_method()
            {
                var Bad_Local = BadField + _goodField;
                _goodField = Bad_Local;
            }

            public void GoodMethod(int BadParam)
            {
                var goodLocal = BadParam;
                _goodField = goodLocal;
            }
        }
        """;

    private static int LineOf(string marker)
    {
        var lines = ProbeSource.Replace("\r\n", "\n").Split('\n');
        var index = Array.FindIndex(lines, l => l.Contains(marker));
        index.Should().BeGreaterThan(-1, $"the probe source must contain '{marker}'");
        return index;
    }

    [Fact]
    public async Task DefaultsMode_FlagsExactlyTheDeliberateViolations()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(ProbeSource, "ProperHost.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindNamingViolationsAsync(projectName: document.Project.Name);

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        data["rulesSource"]!.Value<string>().Should().Be("defaults");
        (data["unsupportedRules"] as JArray)!.Should().BeEmpty();

        var violations = (data["violations"] as JArray)!;
        var byKind = violations
            .GroupBy(v => v["kind"]!.Value<string>())
            .ToDictionary(g => g.Key!, g => g.Select(v => v["symbol"]!.Value<string>()).ToList());

        byKind.Keys.Should().BeEquivalentTo(new[] { "field", "method", "parameter", "local" },
            "exactly four kinds carry deliberate violations");
        byKind["field"].Should().ContainSingle().Which.Should().Contain("BadField");
        byKind["method"].Should().ContainSingle().Which.Should().Contain("lowercase_method");
        byKind["parameter"].Should().ContainSingle().Which.Should().Contain("BadParam");
        byKind["local"].Should().ContainSingle().Which.Should().Contain("Bad_Local");

        var fieldViolation = violations.First(v => v["kind"]!.Value<string>() == "field");
        fieldViolation["rule"]!.Value<string>().Should().Be("default_private_fields");
        fieldViolation["expected"]!.Value<string>().Should().Be("'_' + camelCase");
        fieldViolation["location"]!["line"]!.Value<int>().Should().Be(LineOf("private int BadField;"),
            "the violation points at the BadField declaration (0-based, Roslyn's convention)");
    }

    [Fact]
    public async Task DefaultsMode_FlagsLocalsBeyondPlainDeclarations()
    {
        // foreach variables, out-vars, pattern captures, and deconstruction
        // designations are all locals and must be subject to the camelCase rule;
        // only the badly-named ones are flagged, the well-named one is not.
        const string source = """
            namespace LocalsProbe;

            public class Host
            {
                public void Scan(string text, object value)
                {
                    foreach (var BadForeach in text.Split(','))
                    {
                        System.Console.WriteLine(BadForeach);
                    }
                    if (int.TryParse(text, out var BadOutVar))
                    {
                        System.Console.WriteLine(BadOutVar);
                    }
                    if (value is string BadPattern)
                    {
                        System.Console.WriteLine(BadPattern);
                    }
                    var (BadDecon, goodDecon) = (1, 2);
                    System.Console.WriteLine(BadDecon + goodDecon);
                }
            }
            """;
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(source, "Host.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindNamingViolationsAsync(projectName: document.Project.Name);

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var locals = (json["data"]!["violations"] as JArray)!
            .Where(v => v["kind"]!.Value<string>() == "local")
            .Select(v => v["symbol"]!.Value<string>()!)
            .ToList();

        locals.Should().HaveCount(4, "exactly four locals are badly named: " + string.Join(", ", locals));
        locals.Should().Contain(s => s.Contains("BadForeach"), "foreach loop variables are scanned");
        locals.Should().Contain(s => s.Contains("BadOutVar"), "out-var declarations are scanned");
        locals.Should().Contain(s => s.Contains("BadPattern"), "pattern captures are scanned");
        locals.Should().Contain(s => s.Contains("BadDecon"), "deconstruction designations are scanned");
        locals.Should().NotContain(s => s.Contains("goodDecon"),
            "a correctly camelCased deconstruction element is not a violation");
    }

    [Fact]
    public async Task EditorConfigMode_SeverityNoneDisablesARule()
    {
        // Two configured rules: methods must be camelCase (disabled via
        // severity = none) and interfaces must start with I (active). The
        // PascalCase method must NOT be flagged — the disabled rule is inert —
        // while the interface still is.
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            "namespace ConfigProbe; public interface Widget { } public class C { public void DoWork() { } }",
            "Widget.cs");

        var configText = """
            is_global = true
            dotnet_naming_rule.methods_camel.symbols = method_group
            dotnet_naming_rule.methods_camel.style = camel
            dotnet_naming_rule.methods_camel.severity = none
            dotnet_naming_symbols.method_group.applicable_kinds = method
            dotnet_naming_style.camel.capitalization = camel_case
            dotnet_naming_rule.interfaces_start_with_i.symbols = interface_group
            dotnet_naming_rule.interfaces_start_with_i.style = i_prefix_pascal
            dotnet_naming_rule.interfaces_start_with_i.severity = warning
            dotnet_naming_symbols.interface_group.applicable_kinds = interface
            dotnet_naming_style.i_prefix_pascal.capitalization = pascal_case
            dotnet_naming_style.i_prefix_pascal.required_prefix = I
            """;
        var configPath = Path.Combine(Path.GetDirectoryName(document.FilePath!)!, ".globalconfig");
        var solution = workspace.CurrentSolution.AddAnalyzerConfigDocument(
            DocumentId.CreateNewId(document.Project.Id),
            ".globalconfig",
            SourceText.From(configText),
            filePath: configPath);
        workspace.TryApplyChanges(solution).Should().BeTrue();

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindNamingViolationsAsync();

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["rulesSource"]!.Value<string>().Should().Be("editorconfig");

        var violations = (json["data"]!["violations"] as JArray)!;
        violations.Should().ContainSingle("only the interface violates an active rule")
            .Which["kind"]!.Value<string>().Should().Be("interface");
        violations.Should().NotContain(v => v["kind"]!.Value<string>() == "method",
            "the camelCase method rule was set to severity = none and must not fire");
    }

    [Fact]
    public async Task EditorConfigMode_DrivesRulesFromTheConfigDocument()
    {
        // An analyzer-config document whose naming section requires interfaces
        // to be 'I' + PascalCase; the probe interface violates it.
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            "namespace ConfigProbe; public interface Widget { }", "Widget.cs");

        var configText = """
            is_global = true
            dotnet_naming_rule.interfaces_start_with_i.symbols = interface_group
            dotnet_naming_rule.interfaces_start_with_i.style = i_prefix_pascal
            dotnet_naming_rule.interfaces_start_with_i.severity = warning
            dotnet_naming_symbols.interface_group.applicable_kinds = interface
            dotnet_naming_style.i_prefix_pascal.capitalization = pascal_case
            dotnet_naming_style.i_prefix_pascal.required_prefix = I
            """;
        var configPath = Path.Combine(Path.GetDirectoryName(document.FilePath!)!, ".globalconfig");
        var solution = workspace.CurrentSolution.AddAnalyzerConfigDocument(
            DocumentId.CreateNewId(document.Project.Id),
            ".globalconfig",
            SourceText.From(configText),
            filePath: configPath);
        workspace.TryApplyChanges(solution).Should().BeTrue();

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindNamingViolationsAsync();

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        data["rulesSource"]!.Value<string>().Should().Be("editorconfig");

        var violations = (data["violations"] as JArray)!;
        violations.Should().ContainSingle("only the interface violates the configured rule");
        violations[0]["kind"]!.Value<string>().Should().Be("interface");
        violations[0]["symbol"]!.Value<string>().Should().Contain("Widget");
        violations[0]["rule"]!.Value<string>().Should().Be("interfaces_start_with_i");
        violations[0]["expected"]!.Value<string>().Should().Be("'I' + PascalCase");
    }

    [Fact]
    public async Task MaxResultsOutOfRange_ReturnsInvalidParameter()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode("public class A { }", "A.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindNamingViolationsAsync(maxResults: 0);

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }
}
