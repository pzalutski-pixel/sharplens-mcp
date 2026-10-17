using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// find_similar_code (name mode) and check_type_compatibility disclose, in data.notice, a
// type name that resolved to a solution type shadowing a same-named framework type —
// App.Timer over System.Threading.Timer — and say nothing (notice null) for a
// fully-qualified name.
public class ShadowNoticeTests
{
    private const string Source = """
        namespace App
        {
            public class Timer
            {
                public int Tick(int[] values)
                {
                    var total = 0;
                    foreach (var v in values) { total += v; }
                    return total;
                }

                public int Tock(int[] items)
                {
                    var acc = 0;
                    foreach (var x in items) { acc += x; }
                    return acc;
                }
            }
        }
        """;

    private const string TimerNotice =
        "'Timer' resolved to App.Timer; also matches System.Threading.Timer — pass the fully-qualified name to target it.";

    private static QualitySolutionBuilder Build(out RoslynService service, out InMemoryDispatcher dispatcher)
    {
        var builder = new QualitySolutionBuilder();
        builder.AddProject("App", new[] { ("Timer.cs", Source) });
        var workspace = builder.Build();
        service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        dispatcher = new InMemoryDispatcher(workspace);
        return builder;
    }

    [Fact]
    public async Task SimilarCode_ByAShadowingTypeName_CarriesTheNotice()
    {
        using var builder = Build(out var service, out _);

        var shadowed = JObject.FromObject(await service.FindSimilarCodeAsync("Timer", "Tick", threshold: 0.99))["data"]!;
        shadowed["notice"]!.Value<string>().Should().Be(TimerNotice);
        ((JArray)shadowed["results"]!).Select(r => r["method"]!.Value<string>()).Should().Equal("Timer.Tock");

        var qualified = JObject.FromObject(await service.FindSimilarCodeAsync("App.Timer", "Tick", threshold: 0.99))["data"]!;
        qualified["notice"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task TypeCompatibility_ByAShadowingTypeName_CarriesTheNotice()
    {
        using var builder = Build(out var service, out _);

        var shadowed = JObject.FromObject(await service.CheckTypeCompatibilityAsync("Timer", "object"))["data"]!;
        shadowed["notice"]!.Value<string>().Should().Be(TimerNotice);
        shadowed["sourceType"]!.Value<string>().Should().Be("App.Timer");

        var qualified = JObject.FromObject(await service.CheckTypeCompatibilityAsync("App.Timer", "object"))["data"]!;
        qualified["notice"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task ViaDispatcher_BothToolsCarryTheNotice()
    {
        using var builder = Build(out _, out var dispatcher);

        var similar = await dispatcher.DataAsync("find_similar_code", new { typeName = "Timer", methodName = "Tick", threshold = 0.99 });
        similar["notice"]!.Value<string>().Should().Be(TimerNotice);

        var compatibility = await dispatcher.DataAsync("check_type_compatibility", new { sourceType = "Timer", targetType = "object" });
        compatibility["notice"]!.Value<string>().Should().Be(TimerNotice);
    }
}
