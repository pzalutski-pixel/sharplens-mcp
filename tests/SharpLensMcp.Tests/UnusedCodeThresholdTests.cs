using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// In-memory tests for find_unused_code's reference-count threshold and the
// disposable-interface skip. SymbolFinder.FindReferencesAsync locations exclude
// the declaration itself, so a genuinely unused symbol has ZERO reference
// locations and a symbol used exactly once has ONE — only the zero case may be
// reported. Types implementing IDisposable/IAsyncDisposable get no special
// treatment: disposability says nothing about whether anything uses the type.
public class UnusedCodeThresholdTests
{
    private const string SingleReferenceCode = @"
namespace Production
{
    public class Helpers
    {
        // Called exactly once (Caller.Use) — one reference location, NOT unused.
        public int CalledOnce(int seed) => seed + 1;

        // Never called — zero reference locations, must be flagged.
        public int NeverCalled(int seed) => seed - 1;
    }

    public class Caller
    {
        public int Use(Helpers h) => h.CalledOnce(1);
    }
}";

    private const string SingleTypeReferenceCode = @"
namespace Production
{
    // Referenced exactly once, as TypeUser.Holder's property type — NOT unused.
    public class UsedOnceType { }

    public class TypeUser
    {
        public UsedOnceType? Holder { get; set; }
    }
}";

    private const string ExtensionMethodCode = @"
namespace Production
{
    // The static class itself is never named (extension syntax skips the
    // qualifier), but its member is invoked — the member-reference fallback
    // must keep the type off the unused list.
    public static class Extensions
    {
        public static int Doubled(this int value) => value * 2;
    }

    public class ExtensionUser
    {
        public int Go() => 3.Doubled();
    }
}";

    private const string DisposableCode = @"
namespace Production
{
    // Implements IDisposable and is referenced nowhere — must be flagged;
    // disposability is not a usage signal.
    public sealed class UnusedDisposable : System.IDisposable
    {
        public void Dispose() { }
    }

    // Same for the async pattern.
    public sealed class UnusedAsyncDisposable : System.IAsyncDisposable
    {
        public System.Threading.Tasks.ValueTask DisposeAsync() => default;
    }
}";

    private static async Task<List<string>> RunAndGetUnusedNames(string code, string kindFilter)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var result = await service.FindUnusedCodeAsync(
            projectName: "TestProject",
            includePrivate: false,
            includeInternal: false,
            symbolKindFilter: kindFilter,
            maxResults: 50);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return (json["data"]!["unusedSymbols"] as JArray)!
            .Select(u => u["fullyQualifiedName"]!.Value<string>()!)
            .ToList();
    }

    [Fact]
    public async Task FindUnusedCode_AbstractMethod_IsNotFlagged_ButConcreteUncalledIs()
    {
        const string code = @"
namespace Production
{
    public abstract class Shape
    {
        // Abstract — dispatched through the type contract, never a direct call.
        public abstract double Area();

        // Concrete and never called — a genuine unused method.
        public double Perimeter() => 0;
    }
}";
        var names = await RunAndGetUnusedNames(code, "method");

        names.Should().NotContain(n => n.Contains("Shape.Area"),
            "an abstract method is reached through the type contract, not a direct call");
        names.Should().Contain(n => n.Contains("Shape.Perimeter"),
            "a concrete uncalled method is genuinely unused");
    }

    [Fact]
    public async Task FindUnusedCode_MethodReferencedExactlyOnce_IsNotReported()
    {
        var names = await RunAndGetUnusedNames(SingleReferenceCode, "Method");
        names.Should().NotContain(n => n.Contains("CalledOnce"),
            "CalledOnce has exactly one reference location — a threshold that treats " +
            "one reference as unused misreports every single-use symbol");
        names.Should().Contain(n => n.Contains("NeverCalled"),
            "NeverCalled has zero reference locations and must be flagged");
    }

    [Fact]
    public async Task FindUnusedCode_TypeReferencedExactlyOnce_IsNotReported()
    {
        var names = await RunAndGetUnusedNames(SingleTypeReferenceCode, "Type");
        names.Should().NotContain(n => n.Contains("UsedOnceType"),
            "UsedOnceType is referenced once as a property type and is not unused");
        names.Should().Contain(n => n.Contains("TypeUser"),
            "TypeUser has zero reference locations and must be flagged");
    }

    [Fact]
    public async Task FindUnusedCode_StaticClassUsedOnlyViaExtensionInvocation_IsNotReported()
    {
        var names = await RunAndGetUnusedNames(ExtensionMethodCode, "Type");
        names.Should().NotContain(n => n.Contains("Extensions"),
            "Extensions is never named directly, but Doubled has a reference — the " +
            "member-reference fallback must count a single member use as usage");
        names.Should().Contain(n => n.Contains("ExtensionUser"),
            "ExtensionUser has zero reference locations and must be flagged");
    }

    [Fact]
    public async Task FindUnusedCode_UnusedDisposableTypes_AreReported()
    {
        var names = await RunAndGetUnusedNames(DisposableCode, "Type");
        names.Should().Contain(n => n.Contains("UnusedDisposable"),
            "an unreferenced type must be flagged even though it implements IDisposable");
        names.Should().Contain(n => n.Contains("UnusedAsyncDisposable"),
            "an unreferenced type must be flagged even though it implements IAsyncDisposable");
    }
}
