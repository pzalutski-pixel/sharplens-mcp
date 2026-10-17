using FluentAssertions;
using Microsoft.CodeAnalysis;
using SharpLensMcp.Tests.Consistency;
using Xunit;

namespace SharpLensMcp.Tests;

// The in-memory fixtures the call-flow, exception-flow, stack-trace, usage-search,
// dependency-edge and interceptor tests load compile without errors, so every expected
// answer is about code that binds — an unbound call or type would silently drop out of
// the tools' results and of the tests' expectations alike.
public class CallFlowFixtureCompileTests
{
    public static IEnumerable<object[]> Fixtures() => new[]
    {
        new object[] { nameof(CallFlowSources), CallFlowSources.Flow },
        new object[] { nameof(TypeDependencyEdgeTests), TypeDependencyEdgeTests.Source },
        new object[] { nameof(FineGrainedSearchFormTests), FineGrainedSearchFormTests.Source },
        new object[] { nameof(StackTraceFrameFormatTests), StackTraceFrameFormatTests.Source },
        new object[] { nameof(CallSetConsistencyTests), CallSetConsistencyTests.Source },
        new object[] { nameof(InterceptorAttributeFormTests), InterceptorAttributeFormTests.Source }
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Fixture_CompilesWithoutErrors(string fixture, string source)
    {
        var (_, document) = TestHelpers.CreateWorkspaceWithCode(source, "Fixture.cs");

        var compilation = await document.Project.GetCompilationAsync();

        compilation!.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .Should().BeEmpty($"the {fixture} fixture must bind completely");
    }
}
