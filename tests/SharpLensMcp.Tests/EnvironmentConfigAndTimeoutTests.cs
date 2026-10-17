using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// EnvironmentConfig's SHARPLENS_*-then-ROSLYN_* fallback, its typed parsers, and
// McpServer's timeout-seconds guard. Uses dedicated, test-unique env var names so the
// process-global environment isn't shared with other tests; the real variables are
// covered out of process by StdioProtocolTests.
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

    [Fact]
    public void GetBool_ReadsTheLegacyNameWhenThePrimaryIsUnset()
    {
        const string primary = "SHARPLENS_TEST_ENVCFG_BOOL_XZ";
        const string legacy = "ROSLYN_TEST_ENVCFG_BOOL_XZ";
        try
        {
            EnvironmentConfig.GetBool(primary, legacy, defaultValue: false).Should().BeFalse();
            Environment.SetEnvironmentVariable(legacy, "TRUE");
            EnvironmentConfig.GetBool(primary, legacy, defaultValue: false).Should().BeTrue(
                "the legacy spelling is honored for every variable, case-insensitively");
        }
        finally
        {
            Environment.SetEnvironmentVariable(primary, null);
            Environment.SetEnvironmentVariable(legacy, null);
        }
    }

    [Theory]
    [InlineData("false", true, false, null)]
    [InlineData("False", true, false, null)]
    [InlineData("FALSE", true, false, null)]
    [InlineData(" false ", true, false, null)]
    [InlineData("True", false, true, null)]
    [InlineData(null, true, true, null)]
    [InlineData("", false, false, null)]
    [InlineData("no", true, true, "SHARPLENS_X='no' is not true or false; using the default true")]
    [InlineData("1", false, false, "SHARPLENS_X='1' is not true or false; using the default false")]
    public void ParseBool_IsCaseInsensitive_AndFallsBackWithAWarning(
        string? raw, bool defaultValue, bool expected, string? expectedWarning)
    {
        var (value, warning) = EnvironmentConfig.ParseBool("SHARPLENS_X", raw, defaultValue);

        value.Should().Be(expected);
        warning.Should().Be(expectedWarning);
    }

    [Theory]
    [InlineData("50", 50, null)]
    [InlineData(" 7 ", 7, null)]
    [InlineData(null, 100, null)]
    [InlineData("", 100, null)]
    [InlineData("-5", 100, "SHARPLENS_X='-5' is below the minimum 1; using the default 100")]
    [InlineData("0", 100, "SHARPLENS_X='0' is below the minimum 1; using the default 100")]
    [InlineData("abc", 100, "SHARPLENS_X='abc' is not an integer; using the default 100")]
    [InlineData("1.5", 100, "SHARPLENS_X='1.5' is not an integer; using the default 100")]
    [InlineData("1000", 500, "SHARPLENS_X='1000' is above the maximum 500; using the maximum 500")]
    [InlineData("99999999999999999999", 500, "SHARPLENS_X='99999999999999999999' is above the maximum 500; using the maximum 500")]
    public void ParseInt_RejectsBelowMinimum_ClampsAboveMaximum(string? raw, int expected, string? expectedWarning)
    {
        var (value, warning) = EnvironmentConfig.ParseInt("SHARPLENS_X", raw, defaultValue: 100, minimum: 1, maximum: 500);

        value.Should().Be(expected);
        warning.Should().Be(expectedWarning);
    }

    [Theory]
    [InlineData("45", 45)]
    [InlineData("1", 1)]
    [InlineData(null, 30)]   // missing → default
    [InlineData("", 30)]     // empty → default
    [InlineData("abc", 30)]  // non-numeric → default
    [InlineData("0", 30)]    // zero is not positive → default
    [InlineData("-5", 30)]   // negative → default
    [InlineData("2147483", 2147483)]       // the largest budget a CancellationTokenSource timer accepts on every runtime
    [InlineData("2147484", 2147483)]       // above it → clamped, never an out-of-range CancelAfter
    [InlineData("99999999", 2147483)]
    public void ParseToolCallTimeoutSeconds_GuardsToPositiveOrDefaults30_AndClampsToTheTimerMaximum(string? configured, int expected)
    {
        McpServer.ParseToolCallTimeoutSeconds(configured).Should().Be(expected);
    }

    [Theory]
    [InlineData("debug", "Debug")]
    [InlineData("WARNING", "Warning")]
    [InlineData("Trace", "Trace")]
    [InlineData("verbose", "Information")]
    [InlineData(null, "Information")]
    public void ParseLogLevel_MatchesTheKnownLevelsCaseInsensitively(string? raw, string expected)
    {
        McpServer.ParseLogLevel(raw).Should().Be(expected);
    }
}
