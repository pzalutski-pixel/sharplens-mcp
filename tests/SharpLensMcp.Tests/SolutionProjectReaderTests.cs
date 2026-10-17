using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// The declared-project set, read the way MSBuildWorkspace reads solutions.
public class SolutionProjectReaderTests : IDisposable
{
    private readonly TempSolutionDirectory _dir = new("slnreader");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Sln_SplitsCompilingProjectsFromOtherTypes_AndSkipsSolutionFolders()
    {
        var sln = _dir.WriteSolution("App.sln", new[]
        {
            ("A", "A\\A.csproj", "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC"),
            ("Folder", "Folder", "2150E333-8FDC-42A3-9474-1A3956D46DE8"),
            ("B", "B\\B.vbproj", "F184B08F-C81C-45F6-A57F-5ABD9991F28F"),
            ("Shared", "Shared\\Shared.shproj", "D954291E-2A0B-460D-934E-DC6B0785DB48"),
            ("F", "F\\F.fsproj", "6EC3EE1D-3C4E-46DD-8F32-0CC8E7565705"),
        });

        var declared = await SolutionProjectReader.ReadAsync(sln, CancellationToken.None);

        declared.Should().NotBeNull();
        declared!.CompilingProjectPaths.Should().Equal(_dir.PathOf("A/A.csproj"), _dir.PathOf("B/B.vbproj"), _dir.PathOf("F/F.fsproj"));
        declared.OtherProjectPaths.Should().Equal(_dir.PathOf("Shared/Shared.shproj"));
    }

    [Fact]
    public async Task Slnx_ReadsProjectElements_WithFoldersAndNonCompilingTypes()
    {
        var slnx = _dir.Write("App.slnx",
            "<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"src/A/A.csproj\" />\n  </Folder>\n" +
            "  <Project Path=\"B/B.csproj\" />\n  <Project Path=\"Shared/Shared.shproj\" />\n</Solution>\n");

        var declared = await SolutionProjectReader.ReadAsync(slnx, CancellationToken.None);

        declared!.CompilingProjectPaths.Should().BeEquivalentTo(_dir.PathOf("src/A/A.csproj"), _dir.PathOf("B/B.csproj"));
        declared.OtherProjectPaths.Should().Equal(_dir.PathOf("Shared/Shared.shproj"));
    }

    [Fact]
    public async Task Slnf_NarrowsItsSolutionToTheListedProjects()
    {
        _dir.WriteSolution("App.sln", ("A", "A\\A.csproj"), ("B", "B\\B.csproj"));
        var slnf = _dir.Write("filters/OnlyB.slnf",
            "{ \"solution\": { \"path\": \"..\\\\App.sln\", \"projects\": [ \"B\\\\B.csproj\" ] } }");

        var declared = await SolutionProjectReader.ReadAsync(slnf, CancellationToken.None);

        declared!.CompilingProjectPaths.Should().Equal(_dir.PathOf("B/B.csproj"));
        declared.OtherProjectPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task UnreadableSolution_IsNull()
    {
        var sln = _dir.Write("Broken.sln", "not a solution\n");

        (await SolutionProjectReader.ReadAsync(sln, CancellationToken.None)).Should().BeNull();
    }
}
