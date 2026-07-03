using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// organize_usings must keep a file-header comment carried by the first using pinned to
// the top when that using sorts to a non-first position. Each directive otherwise keeps
// its own leading trivia through the sort, so the header would travel down the file with
// System.Text; the shared PinHeaderTrivia helper re-pins it to whichever directive ends
// up first — the Roslyn "Remove and Sort Usings" contract.
public class OrganizeUsingsHeaderTriviaTests
{
    private const string Code = @"// Copyright 2026 Example
using System.Text;
using System.Collections.Generic;

namespace Demo
{
    public class C
    {
        private StringBuilder _sb = new();
        private List<int> _list = new();
    }
}";

    [Fact]
    public async Task ReorderingFirstUsing_KeepsHeaderAtTop()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.OrganizeUsingsAsync(document.FilePath!))["data"]!;
        data["changed"]!.Value<bool>().Should().BeTrue("System.Collections.Generic sorts before System.Text");
        data["removedUsings"]!.Value<int>().Should().Be(0, "both usings are used and kept");

        var organized = data["organizedText"]!.Value<string>()!;

        organized.Should().StartWith("// Copyright 2026 Example",
            "the file header must stay at the very top after the first using sorts down");

        var headerIdx = organized.IndexOf("// Copyright 2026 Example", StringComparison.Ordinal);
        var genericIdx = organized.IndexOf("using System.Collections.Generic;", StringComparison.Ordinal);
        var textIdx = organized.IndexOf("using System.Text;", StringComparison.Ordinal);

        headerIdx.Should().Be(0);
        genericIdx.Should().BeGreaterThan(headerIdx, "the header precedes the sorted usings");
        textIdx.Should().BeGreaterThan(genericIdx, "System.Collections.Generic sorts before System.Text");
    }
}
