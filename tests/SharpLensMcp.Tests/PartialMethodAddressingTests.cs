using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Name-based addressing of a partial method must land on the implementing part
// (which carries the body), not the bodyless defining declaration that appears
// first in the source.
public class PartialMethodAddressingTests
{
    private const string Code = """
        namespace P
        {
            public partial class Calc
            {
                public partial int Risky(int x);
            }

            public partial class Calc
            {
                public partial int Risky(int x)
                {
                    if (x < 0) throw new System.ArgumentException();
                    return x;
                }
            }
        }
        """;

    [Fact]
    public async Task NameAddressedPartialMethod_ResolvesToImplementingPart()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Calc.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.GetExceptionFlowAsync(
            filePath: null, line: null, column: null, typeName: "Calc", memberName: "Risky");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var escaping = (json["data"]!["escaping"] as JArray)!
            .Select(e => e["exceptionType"]!.Value<string>()!)
            .ToList();
        escaping.Should().Contain("System.ArgumentException",
            "addressing must reach the implementing part's body, where the throw lives; " +
            "the bodyless defining declaration would surface nothing");
    }
}
