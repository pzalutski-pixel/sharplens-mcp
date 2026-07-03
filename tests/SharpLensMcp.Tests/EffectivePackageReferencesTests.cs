using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// CollectEffectivePackageReferences must resolve Central Package Management
// versions (Directory.Packages.props) and surface packages declared in
// Directory.Build.props, not just the leaf csproj.
public class EffectivePackageReferencesTests
{
    [Fact]
    public void ResolvesCpmVersions_AndDirectoryBuildPropsPackages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cpm-{Guid.NewGuid():N}");
        var projDir = Path.Combine(root, "proj");
        Directory.CreateDirectory(projDir);
        try
        {
            File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), """
                <Project>
                  <ItemGroup>
                    <PackageVersion Include="Foo" Version="1.2.3" />
                    <PackageVersion Include="Baz" Version="4.5.6" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"), """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="Baz" />
                  </ItemGroup>
                </Project>
                """);
            var csproj = Path.Combine(projDir, "App.csproj");
            File.WriteAllText(csproj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Foo" />
                  </ItemGroup>
                </Project>
                """);

            var refs = RoslynService.CollectEffectivePackageReferences(csproj);

            var foo = refs.SingleOrDefault(r => r.Name == "Foo");
            foo.Should().NotBeNull("the leaf PackageReference must be present");
            foo!.Version.Should().Be("1.2.3", "the version comes from Directory.Packages.props (CPM)");

            var baz = refs.SingleOrDefault(r => r.Name == "Baz");
            baz.Should().NotBeNull("a Directory.Build.props package must be surfaced");
            baz!.Version.Should().Be("4.5.6", "its CPM version resolves too");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
