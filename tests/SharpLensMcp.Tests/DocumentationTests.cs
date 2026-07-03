using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_documentation over an in-memory workspace (TestHelpers' parse options
// keep DocumentationMode.Parse, so /// comments are retained). The probe
// covers the four verify cases: interface member with full docs, an
// implementing member carrying only <inheritdoc/>, a cref inheritdoc to a
// sibling, and a self-referential inheritdoc that must terminate.
public class DocumentationTests
{
    private const string ProbeSource = """
        namespace DocProbe;

        /// <summary>A widget contract.</summary>
        public interface IWidget
        {
            /// <summary>Computes the widget total.</summary>
            /// <param name="factor">The multiplier to apply.</param>
            /// <returns>The computed total.</returns>
            /// <exception cref="System.ArgumentException">When factor is negative.</exception>
            int Compute(int factor);
        }

        /// <summary>The canonical widget.</summary>
        public class Widget : IWidget
        {
            /// <inheritdoc/>
            public int Compute(int factor) => factor * 2;

            /// <summary>Local summary that wins.</summary>
            /// <inheritdoc cref="IWidget.Compute(int)"/>
            public int ComputeWithLocalSummary(int factor) => factor;

            /// <inheritdoc cref="DocProbe.Widget.SelfReferential"/>
            public int SelfReferential => 0;
        }
        """;

    private static RoslynService CreateService()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(ProbeSource, "Widget.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    private static JToken Doc(JToken data) => data["documentation"]!;

    [Fact]
    public async Task BareInheritDoc_ExpandsToTheInterfaceMemberDocs()
    {
        var data = GetSuccessData(await CreateService().GetDocumentationAsync("Widget", "Compute"));

        data["inheritedFrom"]!.Value<string>().Should().Be("DocProbe.IWidget.Compute(int)");
        data["unresolved"]!.Type.Should().Be(JTokenType.Null);

        var doc = Doc(data);
        doc["summary"]!.Value<string>().Should().Be("Computes the widget total.");
        doc["returns"]!.Value<string>().Should().Be("The computed total.");

        var @params = (doc["params"] as JArray)!;
        @params.Should().ContainSingle();
        @params[0]["name"]!.Value<string>().Should().Be("factor");
        @params[0]["text"]!.Value<string>().Should().Be("The multiplier to apply.");

        var exceptions = (doc["exceptions"] as JArray)!;
        exceptions.Should().ContainSingle();
        exceptions[0]["type"]!.Value<string>().Should().Be("System.ArgumentException");
        exceptions[0]["text"]!.Value<string>().Should().Be("When factor is negative.");
    }

    [Fact]
    public async Task MultiHopInheritDoc_ReportsTheUltimateOrigin()
    {
        const string source = """
            namespace DocChain;

            public interface IRoot
            {
                /// <summary>Root docs.</summary>
                int Do(int x);
            }

            public class Mid : IRoot
            {
                /// <inheritdoc/>
                public virtual int Do(int x) => x;
            }

            public class Leaf : Mid
            {
                /// <inheritdoc/>
                public override int Do(int x) => x * 2;
            }
            """;
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(source, "Chain.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = GetSuccessData(await service.GetDocumentationAsync("Leaf", "Do"));

        data["inheritedFrom"]!.Value<string>().Should().Be("DocChain.IRoot.Do(int)",
            "Leaf inheritdoc → Mid inheritdoc → IRoot, so the docs ultimately come from IRoot, not the immediate Mid");
        Doc(data)["summary"]!.Value<string>().Should().Be("Root docs.");
    }

    [Fact]
    public async Task LocalSummary_WinsOverCrefInheritedSummary_ButInheritedParamsFill()
    {
        var data = GetSuccessData(await CreateService().GetDocumentationAsync("Widget", "ComputeWithLocalSummary"));

        data["inheritedFrom"]!.Value<string>().Should().Be("DocProbe.IWidget.Compute(int)");
        var doc = Doc(data);
        doc["summary"]!.Value<string>().Should().Be("Local summary that wins.",
            "the local <summary> must override the inherited one");
        doc["returns"]!.Value<string>().Should().Be("The computed total.",
            "returns has no local value, so the cref-inherited one fills in");
        (doc["params"] as JArray)!.Should().ContainSingle()
            .Which["name"]!.Value<string>().Should().Be("factor");
    }

    [Fact]
    public async Task SelfReferentialInheritDoc_TerminatesWithCycleMarker()
    {
        var data = GetSuccessData(await CreateService().GetDocumentationAsync("Widget", "SelfReferential"));

        data["unresolved"]!.Value<string>().Should().Be("cycle",
            "an inheritdoc cref pointing at its own member must terminate, not recurse forever");
    }

    [Fact]
    public async Task InterfaceMember_ReturnsItsOwnDocsWithNoInheritance()
    {
        var data = GetSuccessData(await CreateService().GetDocumentationAsync("IWidget", "Compute"));

        data["inheritedFrom"]!.Type.Should().Be(JTokenType.Null);
        data["unresolved"]!.Type.Should().Be(JTokenType.Null);
        Doc(data)["summary"]!.Value<string>().Should().Be("Computes the widget total.");
    }

    [Fact]
    public async Task TypeLevelDocumentation_ReturnsTheTypeSummary()
    {
        var data = GetSuccessData(await CreateService().GetDocumentationAsync("Widget"));

        data["symbol"]!.Value<string>().Should().Be("DocProbe.Widget");
        Doc(data)["summary"]!.Value<string>().Should().Be("The canonical widget.");
    }

    [Fact]
    public async Task UnknownType_PropagatesTypeNotFound()
    {
        var result = await CreateService().GetDocumentationAsync("NoSuchType");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.TypeNotFound);
    }

    private static JToken GetSuccessData(object result)
    {
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return json["data"]!;
    }
}
