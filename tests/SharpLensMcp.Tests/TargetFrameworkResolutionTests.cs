using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// Unit tests for the pure csproj-XML target-framework resolution behind
// get_project_structure's targetFramework field.
public class TargetFrameworkResolutionTests
{
    [Fact]
    public void SingleTargetFramework_IsReturned()
    {
        var xml = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
        RoslynService.ResolveTargetFrameworkFromXml(xml, "MyProj").Should().Be("net8.0");
    }

    [Fact]
    public void MultiTarget_WithMonikerInProjectName_ReturnsThatInstanceMoniker()
    {
        // Roslyn loads a multi-targeted project once per TFM, naming the
        // instances "MyProj(net8.0)" / "MyProj(net9.0)".
        var xml = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>";
        RoslynService.ResolveTargetFrameworkFromXml(xml, "MyProj(net9.0)").Should().Be("net9.0");
    }

    [Fact]
    public void MultiTarget_WithPlainProjectName_ReturnsFullList()
    {
        var xml = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>";
        RoslynService.ResolveTargetFrameworkFromXml(xml, "MyProj").Should().Be("net8.0;net9.0");
    }

    [Fact]
    public void MultiTarget_ParenthesizedSuffixNotInList_ReturnsFullList()
    {
        // A project genuinely named with parentheses must not have the suffix
        // mistaken for a moniker.
        var xml = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>";
        RoslynService.ResolveTargetFrameworkFromXml(xml, "MyProj(legacy)").Should().Be("net8.0;net9.0");
    }

    [Fact]
    public void LegacyProject_WithXmlnsAndTargetFrameworkVersion_ResolvesByLocalName()
    {
        var xml = "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">" +
                  "<PropertyGroup><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion></PropertyGroup></Project>";
        RoslynService.ResolveTargetFrameworkFromXml(xml, "OldProj").Should().Be("v4.7.2");
    }

    [Fact]
    public void NoFrameworkElement_ReturnsNull()
    {
        var xml = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>";
        RoslynService.ResolveTargetFrameworkFromXml(xml, "MyProj").Should().BeNull();
    }
}
