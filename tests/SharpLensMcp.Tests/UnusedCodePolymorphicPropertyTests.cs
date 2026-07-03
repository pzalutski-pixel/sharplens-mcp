using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_unused_code must not flag abstract/virtual/override PROPERTIES as unused —
// they are reached through the type contract (dispatch), exactly like the
// methods the detector already excludes. A plain unused property is still reported.
public class UnusedCodePolymorphicPropertyTests
{
    private const string Code = @"
namespace Demo;

public abstract class Base
{
    public abstract int AbstractProp { get; }
    public virtual int VirtualProp => 0;
}

public class Derived : Base
{
    public override int AbstractProp => 1;
    public override int VirtualProp => 2;
    public int PlainUnused => 3;
}
";

    [Fact]
    public async Task PolymorphicProperties_AreNotFlaggedUnused_PlainPropertyIs()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Props.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindUnusedCodeAsync(
            "TestProject", includePrivate: false, includeInternal: false, symbolKindFilter: null);
        var names = (JObject.FromObject(result)["data"]!["unusedSymbols"] as JArray)!
            .Select(s => s["fullyQualifiedName"]!.Value<string>()!).ToList();

        names.Should().Contain("Demo.Derived.PlainUnused",
            "a plain, unreferenced property is genuinely unused");
        names.Should().NotContain("Demo.Base.AbstractProp");
        names.Should().NotContain("Demo.Base.VirtualProp");
        names.Should().NotContain("Demo.Derived.AbstractProp");
        names.Should().NotContain("Demo.Derived.VirtualProp");
    }
}
