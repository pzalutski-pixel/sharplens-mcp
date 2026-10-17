using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// Unit level of the strict array-of-object argument contract: NestedArguments' readers
// and the four ToolRegistry parsers built on them. Every wrong shape must throw
// JsonRpcInvalidParamsException with the exact path-qualified message; every correct
// shape must parse to the exact values. The dispatcher level is
// Mcp/NestedArgumentShapeViaMcpTests.
public class NestedArgumentsTests
{
    private static JsonRpcParameters Args(string json, string tool = "probe_tool") =>
        new((JsonObject)JsonNode.Parse(json)!, tool);

    private static string ThrownMessage(Action read)
    {
        var thrown = read.Should().Throw<JsonRpcInvalidParamsException>().Which;
        return thrown.Message;
    }

    // ---- NestedArguments: the array itself ----

    [Fact]
    public void RequiredObjectArray_Absent_IsMissingRequiredArray()
    {
        ThrownMessage(() => NestedArguments.RequiredObjectArray(Args("{}"), "items"))
            .Should().Be("Missing required array parameter 'items' for tool 'probe_tool'");
    }

    [Fact]
    public void RequiredObjectArray_JsonNull_IsMissingRequiredArray()
    {
        ThrownMessage(() => NestedArguments.RequiredObjectArray(Args("""{"items":null}"""), "items"))
            .Should().Be("Missing required array parameter 'items' for tool 'probe_tool'");
    }

    [Fact]
    public void RequiredObjectArray_AbsentUnderTheSchemaProbe_ReadsEmptyAndRecordsTheName()
    {
        var recorded = new HashSet<string>();
        JsonRpcParameters.ProbeLog.Value = recorded;
        try
        {
            NestedArguments.RequiredObjectArray(new JsonRpcParameters(null, "probe_tool"), "items")
                .Should().BeEmpty();
        }
        finally
        {
            JsonRpcParameters.ProbeLog.Value = null;
        }
        recorded.Should().Equal("items");
    }

    [Fact]
    public void OptionalObjectArray_Absent_IsNull()
    {
        NestedArguments.OptionalObjectArray(Args("{}"), "items").Should().BeNull();
        NestedArguments.OptionalObjectArray(Args("""{"items":null}"""), "items").Should().BeNull();
    }

    [Theory]
    [InlineData("""{"items":{"a":1}}""")]
    [InlineData("""{"items":"text"}""")]
    [InlineData("""{"items":7}""")]
    [InlineData("""{"items":true}""")]
    public void ObjectArray_NonArrayValue_IsInvalid(string json)
    {
        ThrownMessage(() => NestedArguments.OptionalObjectArray(Args(json), "items"))
            .Should().Be("Parameter 'items' for tool 'probe_tool' must be an array of objects");
        ThrownMessage(() => NestedArguments.RequiredObjectArray(Args(json), "items"))
            .Should().Be("Parameter 'items' for tool 'probe_tool' must be an array of objects");
    }

    [Theory]
    [InlineData("""{"items":[{"a":1},null]}""", 1)]
    [InlineData("""{"items":["x"]}""", 0)]
    [InlineData("""{"items":[{"a":1},{"b":2},[1]]}""", 2)]
    [InlineData("""{"items":[3]}""", 0)]
    public void ObjectArray_NonObjectElement_IsInvalidAtItsIndex(string json, int index)
    {
        ThrownMessage(() => NestedArguments.RequiredObjectArray(Args(json), "items"))
            .Should().Be($"Parameter 'items[{index}]' for tool 'probe_tool' must be an object");
    }

    [Fact]
    public void ObjectArray_EmptyArray_IsEmptyNotMissing()
    {
        NestedArguments.RequiredObjectArray(Args("""{"items":[]}"""), "items").Should().BeEmpty();
        NestedArguments.OptionalObjectArray(Args("""{"items":[]}"""), "items").Should().BeEmpty();
    }

    // ---- NestedArguments: element fields ----

    private static JsonObject Element(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void RequiredString_PresentAbsentNullAndWrongType()
    {
        NestedArguments.RequiredString(Element("""{"name":"Run"}"""), "name", "items[3]", "probe_tool")
            .Should().Be("Run");
        ThrownMessage(() => NestedArguments.RequiredString(Element("{}"), "name", "items[3]", "probe_tool"))
            .Should().Be("Missing required parameter 'items[3].name' for tool 'probe_tool'");
        ThrownMessage(() => NestedArguments.RequiredString(Element("""{"name":null}"""), "name", "items[3]", "probe_tool"))
            .Should().Be("Missing required parameter 'items[3].name' for tool 'probe_tool'");
        ThrownMessage(() => NestedArguments.RequiredString(Element("""{"name":5}"""), "name", "items[3]", "probe_tool"))
            .Should().Be("Parameter 'items[3].name' for tool 'probe_tool' must be a string");
    }

    [Fact]
    public void OptionalString_AbsentAndNullReadNull_EmptyStringIsKept()
    {
        NestedArguments.OptionalString(Element("{}"), "name", "items[0]", "probe_tool").Should().BeNull();
        NestedArguments.OptionalString(Element("""{"name":null}"""), "name", "items[0]", "probe_tool").Should().BeNull();
        NestedArguments.OptionalString(Element("""{"name":""}"""), "name", "items[0]", "probe_tool").Should().Be("");
        ThrownMessage(() => NestedArguments.OptionalString(Element("""{"name":{"x":1}}"""), "name", "items[0]", "probe_tool"))
            .Should().Be("Parameter 'items[0].name' for tool 'probe_tool' must be a string");
    }

    [Theory]
    [InlineData("""{"n":"3"}""")]
    [InlineData("""{"n":1.5}""")]
    [InlineData("""{"n":3000000000}""")]
    [InlineData("""{"n":true}""")]
    [InlineData("""{"n":[1]}""")]
    public void OptionalInt_WrongType_IsInvalid(string json)
    {
        ThrownMessage(() => NestedArguments.OptionalInt(Element(json), "n", "items[1]", "probe_tool"))
            .Should().Be("Parameter 'items[1].n' for tool 'probe_tool' must be an integer");
    }

    [Fact]
    public void OptionalInt_ReadsIntegersIncludingNegative()
    {
        NestedArguments.OptionalInt(Element("""{"n":-1}"""), "n", "items[1]", "probe_tool").Should().Be(-1);
        NestedArguments.OptionalInt(Element("""{"n":0}"""), "n", "items[1]", "probe_tool").Should().Be(0);
        NestedArguments.OptionalInt(Element("{}"), "n", "items[1]", "probe_tool").Should().BeNull();
    }

    [Fact]
    public void OptionalStringArray_ShapesAndElements()
    {
        NestedArguments.OptionalStringArray(Element("""{"order":["b","a"]}"""), "order", "changes[0]", "probe_tool")
            .Should().Equal("b", "a");
        NestedArguments.OptionalStringArray(Element("{}"), "order", "changes[0]", "probe_tool").Should().BeNull();
        ThrownMessage(() => NestedArguments.OptionalStringArray(Element("""{"order":"b,a"}"""), "order", "changes[0]", "probe_tool"))
            .Should().Be("Parameter 'changes[0].order' for tool 'probe_tool' must be an array of strings");
        ThrownMessage(() => NestedArguments.OptionalStringArray(Element("""{"order":["b",null]}"""), "order", "changes[0]", "probe_tool"))
            .Should().Be("Parameter 'changes[0].order[1]' for tool 'probe_tool' must be a string");
        ThrownMessage(() => NestedArguments.OptionalStringArray(Element("""{"order":[1]}"""), "order", "changes[0]", "probe_tool"))
            .Should().Be("Parameter 'changes[0].order[0]' for tool 'probe_tool' must be a string");
    }

    // ---- The ToolRegistry parsers ----

    [Fact]
    public void ParseMemberRefs_ReadsEveryEndpoint()
    {
        var refs = ToolRegistry.ParseMemberRefs(
            Args("""{"sources":[{"typeName":"A","memberName":"Run"},{"typeName":"N.B","memberName":"Go"}]}""", "find_path_between"),
            "sources", required: true);

        refs.Should().Equal(new MemberRef("A", "Run"), new MemberRef("N.B", "Go"));
    }

    [Fact]
    public void ParseMemberRefs_OptionalAbsent_IsEmpty_RequiredAbsent_Throws()
    {
        var p = Args("{}", "find_path_between");
        ToolRegistry.ParseMemberRefs(p, "avoidThrough", required: false).Should().BeEmpty();
        ThrownMessage(() => ToolRegistry.ParseMemberRefs(p, "sources", required: true))
            .Should().Be("Missing required array parameter 'sources' for tool 'find_path_between'");
    }

    [Fact]
    public void ParseMemberRefs_ObjectInsteadOfArray_Throws_EvenWhenOptional()
    {
        ThrownMessage(() => ToolRegistry.ParseMemberRefs(
                Args("""{"avoidThrough":{"typeName":"A","memberName":"B"}}""", "find_path_between"),
                "avoidThrough", required: false))
            .Should().Be("Parameter 'avoidThrough' for tool 'find_path_between' must be an array of objects");
    }

    [Fact]
    public void ParseMemberRefs_MissingTypeName_Throws()
    {
        ThrownMessage(() => ToolRegistry.ParseMemberRefs(
                Args("""{"targets":[{"memberName":"Run"}]}""", "find_path_between"), "targets", required: true))
            .Should().Be("Missing required parameter 'targets[0].typeName' for tool 'find_path_between'");
    }

    [Fact]
    public void ParseSignatureChanges_ReadsEveryField()
    {
        var changes = ToolRegistry.ParseSignatureChanges(Args("""
            {"changes":[
              {"action":"add","name":"z","type":"int","defaultValue":"0","position":-1},
              {"action":"rename","name":"a","newName":"b"},
              {"action":"reorder","order":["y","x"]}
            ]}
            """, "change_signature"), "changes");

        changes.Should().HaveCount(3);
        changes[0].Should().BeEquivalentTo(new SignatureChange
        {
            Action = "add", Name = "z", Type = "int", DefaultValue = "0", Position = -1
        });
        changes[1].Should().BeEquivalentTo(new SignatureChange { Action = "rename", Name = "a", NewName = "b" });
        changes[2].Should().BeEquivalentTo(new SignatureChange { Action = "reorder", Order = new List<string> { "y", "x" } });
    }

    [Fact]
    public void ParseSignatureChanges_WrongShapes_Throw()
    {
        ThrownMessage(() => ToolRegistry.ParseSignatureChanges(Args("{}", "change_signature"), "changes"))
            .Should().Be("Missing required array parameter 'changes' for tool 'change_signature'");
        ThrownMessage(() => ToolRegistry.ParseSignatureChanges(Args("""{"changes":{"action":"add"}}""", "change_signature"), "changes"))
            .Should().Be("Parameter 'changes' for tool 'change_signature' must be an array of objects");
        ThrownMessage(() => ToolRegistry.ParseSignatureChanges(Args("""{"changes":[{"action":"add","position":"end"}]}""", "change_signature"), "changes"))
            .Should().Be("Parameter 'changes[0].position' for tool 'change_signature' must be an integer");
        ThrownMessage(() => ToolRegistry.ParseSignatureChanges(Args("""{"changes":[{"name":"a"}]}""", "change_signature"), "changes"))
            .Should().Be("Missing required parameter 'changes[0].action' for tool 'change_signature'");
    }

    [Fact]
    public void ParseArchitectureRules_ReadsRules_AbsentGlobsReadEmpty()
    {
        var rules = ToolRegistry.ParseArchitectureRules(
            Args("""{"rules":[{"from":"App.Domain*","deny":"App.Infra*"},{"from":"X"}]}""", "check_architecture"), "rules");

        rules.Select(r => (r.From, r.Deny)).Should().Equal(("App.Domain*", "App.Infra*"), ("X", ""));
    }

    [Fact]
    public void ParseArchitectureRules_WrongShapes_Throw()
    {
        ThrownMessage(() => ToolRegistry.ParseArchitectureRules(Args("{}", "check_architecture"), "rules"))
            .Should().Be("Missing required array parameter 'rules' for tool 'check_architecture'");
        ThrownMessage(() => ToolRegistry.ParseArchitectureRules(Args("""{"rules":[{"from":"A","deny":"B"},"C"]}""", "check_architecture"), "rules"))
            .Should().Be("Parameter 'rules[1]' for tool 'check_architecture' must be an object");
    }

    [Fact]
    public void ParseMethodBatchRequests_ReadsRequests_OverloadIndexOnlyWhenGiven()
    {
        var requests = ToolRegistry.ParseMethodBatchRequests(Args("""
            {"methods":[{"typeName":"A","methodName":"Run"},{"typeName":"B","methodName":"Go","overloadIndex":2}]}
            """, "get_method_source_batch"), "methods");

        requests.Should().HaveCount(2);
        requests[0].Should().BeEquivalentTo(new Dictionary<string, object> { ["typeName"] = "A", ["methodName"] = "Run" });
        requests[1].Should().BeEquivalentTo(new Dictionary<string, object>
        {
            ["typeName"] = "B", ["methodName"] = "Go", ["overloadIndex"] = 2
        });
    }

    [Fact]
    public void ParseMethodBatchRequests_WrongShapes_Throw()
    {
        ThrownMessage(() => ToolRegistry.ParseMethodBatchRequests(Args("{}", "get_method_source_batch"), "methods"))
            .Should().Be("Missing required array parameter 'methods' for tool 'get_method_source_batch'");
        ThrownMessage(() => ToolRegistry.ParseMethodBatchRequests(Args("""{"methods":[{"methodName":"Run"}]}""", "get_method_source_batch"), "methods"))
            .Should().Be("Missing required parameter 'methods[0].typeName' for tool 'get_method_source_batch'");
    }
}
