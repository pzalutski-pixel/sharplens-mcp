using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// semantic_query's filters are constraints every result satisfies: a method-only filter
// (isAsync, returnType, parameterIncludes/Excludes) keeps only methods, the type filter
// only fields, properties and events, and no filter brings in namespaces (the query
// covers types and members). attributes accept the short name, the class name and the
// fully-qualified name, like the attribute tools. Results are in canonical order.
public class SemanticQueryFilterTests
{
    internal const string Code = """
        using System.Threading.Tasks;

        namespace Lib
        {
            public class Service
            {
                [System.Obsolete] public Task RunAsync() => Task.CompletedTask;
                public async Task<int> CountAsync() { await Task.Yield(); return 1; }
                public int Sync() => 1;
                public Task Pending { get; } = Task.CompletedTask;
                public Task Field = Task.CompletedTask;
            }

            [System.Obsolete]
            public class OldThing { }
        }
        """;

    private static async Task<List<string>> QueryAsync(
        bool? isAsync = null, string? accessibility = null, string? type = null, string? returnType = null,
        List<string>? attributes = null, List<string>? parameterExcludes = null)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Service.cs");
        var service = ToolResult.Load(workspace);
        var data = ToolResult.Data(await service.SemanticQueryAsync(
            null, isAsync, null, accessibility, null, type, returnType, attributes, null, parameterExcludes, null));
        return ((JArray)data["results"]!).Select(r => $"{r["fullyQualifiedName"]}|{r["kind"]}").ToList();
    }

    [Fact]
    public async Task ReturnType_KeepsOnlyMethods()
    {
        (await QueryAsync(returnType: "Task")).Should().Equal(
            "Lib.Service.CountAsync()|Method", "Lib.Service.RunAsync()|Method");
    }

    [Theory]
    [InlineData(true, new[] { "Lib.Service.CountAsync()|Method" })]
    [InlineData(false, new[] { "Lib.Service.RunAsync()|Method", "Lib.Service.Sync()|Method" })]
    public async Task IsAsync_KeepsOnlyMethods(bool isAsync, string[] expected)
    {
        (await QueryAsync(isAsync: isAsync)).Should().Equal(expected);
    }

    [Fact]
    public async Task ParameterExcludes_KeepsOnlyMethods()
    {
        (await QueryAsync(parameterExcludes: new List<string> { "CancellationToken" })).Should().Equal(
            "Lib.Service.CountAsync()|Method", "Lib.Service.RunAsync()|Method", "Lib.Service.Sync()|Method");
    }

    [Fact]
    public async Task TypeFilter_KeepsOnlyFieldsPropertiesAndEvents()
    {
        (await QueryAsync(type: "Task")).Should().Equal("Lib.Service.Field|Field", "Lib.Service.Pending|Property");
    }

    [Theory]
    [InlineData("Obsolete")]
    [InlineData("ObsoleteAttribute")]
    [InlineData("System.ObsoleteAttribute")]
    [InlineData("System.Obsolete")]
    public async Task Attributes_AcceptShortClassAndQualifiedNames(string attribute)
    {
        (await QueryAsync(attributes: new List<string> { attribute })).Should().Equal(
            "Lib.OldThing|Class", "Lib.Service.RunAsync()|Method");
    }

    [Fact]
    public async Task NoNamespacesAmongResults()
    {
        (await QueryAsync(accessibility: "Public")).Should().Equal(
            "Lib.OldThing|Class",
            "Lib.Service|Class",
            "Lib.Service.CountAsync()|Method",
            "Lib.Service.Field|Field",
            "Lib.Service.Pending|Property",
            "Lib.Service.RunAsync()|Method",
            "Lib.Service.Sync()|Method");
    }

    [Fact]
    public async Task Filters_ViaMcp()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Service.cs");
        var host = new InMemoryToolHost(workspace);

        var returnType = await host.DataAsync("semantic_query", new { returnType = "Task" });
        var attributes = await host.DataAsync("semantic_query", new { attributes = new[] { "Obsolete" } });

        ((JArray)returnType["results"]!).Select(r => r["fullyQualifiedName"]!.Value<string>())
            .Should().Equal("Lib.Service.CountAsync()", "Lib.Service.RunAsync()");
        ((JArray)attributes["results"]!).Select(r => r["fullyQualifiedName"]!.Value<string>())
            .Should().Equal("Lib.OldThing", "Lib.Service.RunAsync()");
    }
}
