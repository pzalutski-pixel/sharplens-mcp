using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// extract_method validates the new method's name and accessibility up front, so preview
// and apply agree: an invalid identifier or accessibility must be rejected at preview
// rather than reported as a successful preview that the apply path then rejects.
public class ExtractMethodValidationTests
{
    private const string Code = @"namespace Demo
{
    public class C
    {
        public int M()
        {
            int a = 1;
            int b = 2;
            return a + b;
        }
    }
}";

    private static (RoslynService service, string filePath, int start, int end) Setup()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var lines = Code.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains("int a = 1;"));
        var end = Array.FindIndex(lines, l => l.Contains("int b = 2;"));
        return (service, document.FilePath!, start, end);
    }

    [Fact]
    public async Task Preview_InvalidMethodName_ReturnsInvalidParameter()
    {
        var (service, filePath, start, end) = Setup();

        var json = JObject.FromObject(await service.ExtractMethodAsync(
            filePath, start, end, methodName: "123bad", preview: true));

        json["success"]!.Value<bool>().Should().BeFalse(json.ToString());
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter,
            "an invalid C# identifier must be rejected at preview, not reported as success");
    }

    [Fact]
    public async Task Preview_InvalidAccessibility_ReturnsInvalidParameter()
    {
        var (service, filePath, start, end) = Setup();

        var json = JObject.FromObject(await service.ExtractMethodAsync(
            filePath, start, end, methodName: "Extracted", accessibility: "bogus", preview: true));

        json["success"]!.Value<bool>().Should().BeFalse(json.ToString());
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task Preview_ValidInput_Succeeds()
    {
        var (service, filePath, start, end) = Setup();

        var json = JObject.FromObject(await service.ExtractMethodAsync(
            filePath, start, end, methodName: "Extracted", preview: true));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
    }
}
