using Xunit;

namespace SharpLensMcp.Tests;

// Regression for issue #14: analyze_data_flow / analyze_control_flow must return a
// structured error for an out-of-range or inverted line range, not throw an
// unhandled ArgumentOutOfRangeException that the dispatcher surfaces as -32603.
// The region span is built by indexing text.Lines[startLine] (upper bound
// unguarded — only Math.Max(0, startLine) clamps the lower end) and
// TextSpan.FromBounds(start, end) (throws when start > end), both BEFORE the
// method's own try/catch. extract_method already validates its range up front the
// same way; these two region tools skipped it.
public class RegionAnalysisRangeValidationTests : RoslynServiceTestBase
{
    private (string file, int lineCount) LocateFixtureFile()
    {
        var solution = Service.GetSolutionForTesting()!;
        var doc = solution.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.FilePath != null && d.FilePath.EndsWith("RefactoringFixture.cs"));
        var lineCount = File.ReadAllLines(doc.FilePath!).Length;
        return (doc.FilePath!, lineCount);
    }

    [Fact]
    public async Task AnalyzeDataFlow_StartLineBeyondEndOfFile_ReturnsInvalidParameter()
    {
        var (file, lineCount) = LocateFixtureFile();

        var result = await Service.AnalyzeDataFlowAsync(file, startLine: lineCount + 1000, endLine: lineCount + 1000);

        AssertError(result, ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task AnalyzeControlFlow_StartLineBeyondEndOfFile_ReturnsInvalidParameter()
    {
        var (file, lineCount) = LocateFixtureFile();

        var result = await Service.AnalyzeControlFlowAsync(file, startLine: lineCount + 1000, endLine: lineCount + 1000);

        AssertError(result, ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task AnalyzeDataFlow_InvertedRange_ReturnsInvalidParameter()
    {
        var (file, lineCount) = LocateFixtureFile();

        // Both indices are in range, but startLine > endLine — TextSpan.FromBounds
        // throws today because start > end.
        var result = await Service.AnalyzeDataFlowAsync(file, startLine: lineCount - 1, endLine: 0);

        AssertError(result, ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task AnalyzeControlFlow_InvertedRange_ReturnsInvalidParameter()
    {
        var (file, lineCount) = LocateFixtureFile();

        var result = await Service.AnalyzeControlFlowAsync(file, startLine: lineCount - 1, endLine: 0);

        AssertError(result, ErrorCodes.InvalidParameter);
    }
}
