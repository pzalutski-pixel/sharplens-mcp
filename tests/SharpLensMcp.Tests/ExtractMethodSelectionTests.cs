using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks extract_method's selection semantics: nested-block statements are not
// duplicated, awaited selections extract into async methods, a method-level
// return inside the selection is refused, and returns inside lambdas/local
// functions do NOT trigger that refusal.
public class ExtractMethodSelectionTests
{
    private static (RoslynService service, string filePath, string[] lines) CreateService(string code)
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "Extractable.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, document.FilePath!, code.Split('\n'));
    }

    private static int LineOf(string[] lines, string marker)
    {
        var idx = Array.FindIndex(lines, l => l.Contains(marker, StringComparison.Ordinal));
        idx.Should().BeGreaterThan(-1, $"marker '{marker}' must exist in the fixture");
        return idx;
    }

    [Fact]
    public async Task OutOfRangeLineSelection_ReturnsInvalidParameter_NotInternalError()
    {
        const string code = @"
namespace Production;

public class Tiny
{
    public int N() => 1;
}";
        var (service, filePath, _) = CreateService(code);

        var beyondEnd = await service.ExtractMethodAsync(filePath, startLine: 0, endLine: 9999, methodName: "X");
        JObject.FromObject(beyondEnd)["error"]!["Code"]!.Value<string>().Should().Be(
            ErrorCodes.InvalidParameter, "an end line past the file must be rejected, not throw");

        var reversed = await service.ExtractMethodAsync(filePath, startLine: 4, endLine: 2, methodName: "X");
        JObject.FromObject(reversed)["error"]!["Code"]!.Value<string>().Should().Be(
            ErrorCodes.InvalidParameter, "a reversed range (start > end) must be rejected");
    }

    [Fact]
    public async Task Selection_ContainingNestedBlock_EmitsInnerStatementsExactlyOnce()
    {
        const string code = @"
namespace Production;

public class Worker
{
    public int Process(int a)
    {
        if (a > 0)
        {
            a = a + 100;
        }
        var doubled = a * 2;
        return doubled;
    }
}";
        var (service, filePath, lines) = CreateService(code);

        var result = await service.ExtractMethodAsync(
            filePath,
            startLine: LineOf(lines, "if (a > 0)"),
            endLine: LineOf(lines, "var doubled"),
            methodName: "AdjustAndDouble");

        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var extracted = json["data"]!["extractedCode"]!.Value<string>()!;
        CountOccurrences(extracted, "a = a + 100;").Should().Be(1,
            "the if-body statement is part of the if and must not ALSO be emitted as a sibling");
        CountOccurrences(extracted, "var doubled = a * 2;").Should().Be(1);
        json["data"]!["statementsExtracted"]!.Value<int>().Should().Be(2,
            "the selection has exactly two TOP-LEVEL statements: the if and the var declaration");
    }

    [Fact]
    public async Task Selection_PreservesNestedIndentation()
    {
        const string code = @"
namespace Production;

public class Worker
{
    public int Process(int a)
    {
        if (a > 0)
        {
            a = a + 100;
        }
        var doubled = a * 2;
        return doubled;
    }
}";
        var (service, filePath, lines) = CreateService(code);

        var result = await service.ExtractMethodAsync(
            filePath,
            startLine: LineOf(lines, "if (a > 0)"),
            endLine: LineOf(lines, "var doubled"),
            methodName: "AdjustAndDouble");

        var extracted = JObject.FromObject(result)["data"]!["extractedCode"]!.Value<string>()!
            .Replace("\r\n", "\n");
        var extractedLines = extracted.Split('\n');

        int IndentOf(string marker)
        {
            var line = extractedLines.First(l => l.Contains(marker));
            return line.Length - line.TrimStart().Length;
        }

        var ifIndent = IndentOf("if (a > 0)");
        var innerIndent = IndentOf("a = a + 100;");
        innerIndent.Should().BeGreaterThan(ifIndent,
            "the if-body statement must stay nested deeper than the if, not be flattened to the same column");
    }

    [Fact]
    public async Task Selection_ContainingAwait_ExtractsAsyncMethodAndAwaitedCall()
    {
        const string code = @"
using System.Threading.Tasks;

namespace Production;

public class Worker
{
    public async Task<int> RunAsync()
    {
        var x = await Task.FromResult(21);
        var y = x * 2;
        return y;
    }
}";
        var (service, filePath, lines) = CreateService(code);

        var result = await service.ExtractMethodAsync(
            filePath,
            startLine: LineOf(lines, "var x = await"),
            endLine: LineOf(lines, "var y = x * 2;"),
            methodName: "ComputeAsync");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var signature = json["data"]!["signature"]!.Value<string>()!;
        signature.Should().Contain("async Task<int>",
            "an awaited selection whose result flows out must extract as async Task<int>");

        var replacement = json["data"]!["replacementCode"]!.Value<string>()!;
        replacement.Should().StartWith("var y = await ComputeAsync(",
            "the call site must await the now-async extracted method");
    }

    [Fact]
    public async Task Selection_ContainingMethodReturn_IsRefusedWithHint()
    {
        const string code = @"
namespace Production;

public class Worker
{
    public int Process(int a)
    {
        var b = a + 1;
        return b;
    }
}";
        var (service, filePath, lines) = CreateService(code);

        var result = await service.ExtractMethodAsync(
            filePath,
            startLine: LineOf(lines, "var b = a + 1;"),
            endLine: LineOf(lines, "return b;"),
            methodName: "Broken");

        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeFalse(
            "extracting a selection containing the method's return would generate broken control flow");
        json["error"]!["Code"]!.Value<string>().Should().Be("INVALID_PARAMETER");
        json["error"]!["Hint"]!.Value<string>().Should().Contain("smaller range");
    }

    [Fact]
    public async Task Selection_WithReturnInsideLambda_IsExtractedNormally()
    {
        const string code = @"
using System;

namespace Production;

public class Worker
{
    public int Process(int a)
    {
        Func<int, int> bump = v => { return v + 1; };
        var c = bump(a);
        return c;
    }
}";
        var (service, filePath, lines) = CreateService(code);

        var result = await service.ExtractMethodAsync(
            filePath,
            startLine: LineOf(lines, "Func<int, int> bump"),
            endLine: LineOf(lines, "var c = bump(a);"),
            methodName: "BumpOnce");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(
            "a return INSIDE a lambda never leaves the extracted method and must not trigger the refusal: " + json);
        json["data"]!["extractedCode"]!.Value<string>()!.Should().Contain("return v + 1;",
            "the lambda body travels with the extraction");
    }

    [Fact]
    public async Task Selection_UsingMethodTypeParameter_DeclaresItOnTheExtractedMethod()
    {
        const string code = @"
using System.Collections.Generic;

namespace Production;

public class Box
{
    public T Pick<T>(T[] items) where T : class
    {
        var list = new List<T>();
        list.Add(items[0]);
        var chosen = list[0];
        return chosen;
    }
}";
        var (service, filePath, lines) = CreateService(code);

        var result = await service.ExtractMethodAsync(
            filePath,
            startLine: LineOf(lines, "var list = new List<T>();"),
            endLine: LineOf(lines, "var chosen = list[0];"),
            methodName: "Choose");

        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var signature = json["data"]!["signature"]!.Value<string>()!;
        signature.Should().Contain("Choose<T>(",
            "the extracted method must declare the method type parameter it uses, or it references an undeclared T");
        signature.Should().Contain("T[] items", "the flowed-in parameter keeps its generic type");
        signature.Should().Contain("where T : class",
            "the type parameter's constraint must travel with it onto the extracted method");

        var replacement = json["data"]!["replacementCode"]!.Value<string>()!;
        replacement.Should().Contain("Choose<T>(items)",
            "the call site passes the type argument explicitly so a parameter used in the return still binds");
    }

    [Fact]
    public async Task Selection_AssigningPreExistingLocal_AssignsNotRedeclares()
    {
        const string code = @"
namespace Production;

public class Worker
{
    public int Process(int seed)
    {
        int total = 0;
        total = seed * 2;
        total = total + 1;
        return total;
    }
}";
        var (service, filePath, lines) = CreateService(code);

        var result = await service.ExtractMethodAsync(
            filePath,
            startLine: LineOf(lines, "total = seed * 2;"),
            endLine: LineOf(lines, "total = total + 1;"),
            methodName: "Compute");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var replacement = json["data"]!["replacementCode"]!.Value<string>()!;
        replacement.Should().StartWith("total = ",
            "total is declared before the selection, so the call assigns it, not re-declares it");
        replacement.Should().NotContain("var total",
            "re-declaring an existing local would not compile (CS0128)");
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
