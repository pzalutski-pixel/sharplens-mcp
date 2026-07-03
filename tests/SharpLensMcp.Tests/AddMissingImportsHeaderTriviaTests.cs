using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// add_missing_imports inserts a using before the current first one when it sorts
// earlier; the first using's leading trivia (a file header) must move onto the new
// using so the header stays at the top of the file.
public class AddMissingImportsHeaderTriviaTests
{
    private const string Code = @"// Copyright 2026 Example
using System.Text;

namespace Demo
{
    public class C
    {
        private StringBuilder _sb = new();
        public List<int> Make() => new();
    }
}";

    [Fact]
    public async Task InsertingBeforeFirstUsing_KeepsHeaderAtTop()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.AddMissingImportsAsync(document.FilePath!);
        var content = JObject.FromObject(result)["data"]!["updatedContent"]!.Value<string>()!;

        content.Should().StartWith("// Copyright 2026 Example",
            "the file header must stay at the very top after inserting a using before the first one");

        var headerIdx = content.IndexOf("// Copyright 2026 Example", StringComparison.Ordinal);
        var newUsingIdx = content.IndexOf("using System.Collections.Generic;", StringComparison.Ordinal);
        var textUsingIdx = content.IndexOf("using System.Text;", StringComparison.Ordinal);

        newUsingIdx.Should().BeGreaterThan(headerIdx, "the header precedes the inserted using");
        newUsingIdx.Should().BeLessThan(textUsingIdx,
            "System.Collections.Generic sorts before System.Text and is inserted first");
    }
}
