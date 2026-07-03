using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// EnvironmentConfig's SHARPLENS_*-then-ROSLYN_* fallback and McpServer's
// timeout-seconds parse guard. Uses dedicated, test-unique env var names so the
// process-global environment isn't shared with other parallel tests.
public class EnvironmentConfigAndTimeoutTests
{
    [Fact]
    public void EnvironmentConfig_PrefersPrimary_FallsBackToLegacy_ElseNull()
    {
        const string primary = "SHARPLENS_TEST_ENVCFG_PRIMARY_XZ";
        const string legacy = "ROSLYN_TEST_ENVCFG_LEGACY_XZ";
        try
        {
            // Neither set → null.
            EnvironmentConfig.Get(primary, legacy).Should().BeNull();

            // Only legacy set → legacy value (the 1.5.x migration fallback).
            Environment.SetEnvironmentVariable(legacy, "from-legacy");
            EnvironmentConfig.Get(primary, legacy).Should().Be("from-legacy");

            // Primary set → primary wins over legacy.
            Environment.SetEnvironmentVariable(primary, "from-primary");
            EnvironmentConfig.Get(primary, legacy).Should().Be("from-primary");
        }
        finally
        {
            Environment.SetEnvironmentVariable(primary, null);
            Environment.SetEnvironmentVariable(legacy, null);
        }
    }

    [Theory]
    [InlineData("45", 45)]
    [InlineData("1", 1)]
    [InlineData(null, 30)]   // missing → default
    [InlineData("", 30)]     // empty → default
    [InlineData("abc", 30)]  // non-numeric → default
    [InlineData("0", 30)]    // zero is not positive → default
    [InlineData("-5", 30)]   // negative → default
    public void ParseToolCallTimeoutSeconds_GuardsToPositiveOrDefaults30(string? configured, int expected)
    {
        McpServer.ParseToolCallTimeoutSeconds(configured).Should().Be(expected);
    }
}
