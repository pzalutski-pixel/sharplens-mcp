using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// DOTNET_SOLUTION_PATH resolution: a solution file, or a directory holding exactly one.
public class SolutionPathResolverTests : IDisposable
{
    private readonly TempSolutionDirectory _dir = new("solutionpath");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void SolutionFile_ResolvesToItself()
    {
        var sln = _dir.Write("App.sln", "");

        var resolution = SolutionPathResolver.Resolve(sln);

        resolution.Should().Be(new SolutionPathResolution(sln, null));
    }

    [Fact]
    public void DirectoryWithOneSolution_ResolvesToIt()
    {
        var slnx = _dir.Write("App.slnx", "<Solution />");
        _dir.Write("App.csproj", "");

        SolutionPathResolver.Resolve(_dir.Root).Should().Be(new SolutionPathResolution(slnx, null));
    }

    [Fact]
    public void DirectoryWithSeveralSolutions_IsAnErrorNamingThem_NotAnArbitraryPick()
    {
        _dir.Write("B.sln", "");
        _dir.Write("A.sln", "");
        _dir.Write("A.slnx", "<Solution />");

        var resolution = SolutionPathResolver.Resolve(_dir.Root);

        resolution.Path.Should().BeNull();
        resolution.Error.Should().Be(
            $"DOTNET_SOLUTION_PATH is a directory with 3 solution files (A.sln, A.slnx, B.sln); set it to one of them: {_dir.Root}");
    }

    [Fact]
    public void DirectoryWithoutASolution_IsAnError()
    {
        _dir.Write("App.csproj", "");

        SolutionPathResolver.Resolve(_dir.Root).Should().Be(new SolutionPathResolution(null,
            $"DOTNET_SOLUTION_PATH is a directory with no .sln or .slnx file: {_dir.Root}"));
    }

    [Fact]
    public void MissingPath_IsAnError()
    {
        var missing = _dir.PathOf("nope.sln");

        SolutionPathResolver.Resolve(missing).Should().Be(new SolutionPathResolution(null,
            $"DOTNET_SOLUTION_PATH does not exist: {missing}"));
    }
}
