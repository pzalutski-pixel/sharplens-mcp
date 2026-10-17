using System.Globalization;
using System.Numerics;

namespace SharpLensMcp;

/// <summary>
/// The one place environment variables are read and parsed. Every variable has a branded
/// SHARPLENS_* name and a legacy ROSLYN_* fallback (the 1.5.x spelling). Booleans are
/// true/false case-insensitively; integers have a minimum and a maximum — a value below
/// the minimum (or unparseable) falls back to the default, a value above the maximum is
/// clamped to it. Every fallback or clamp is reported on stderr (once per process), never
/// silently applied.
/// </summary>
internal static class EnvironmentConfig
{
    /// <summary>Default per-call budget, in seconds, for every tools/call and the startup auto-load.</summary>
    public const int DefaultToolCallTimeoutSeconds = 30;

    /// <summary>
    /// The largest per-call budget, in seconds: int.MaxValue milliseconds, the largest
    /// delay a CancellationTokenSource timer accepts on every supported runtime.
    /// </summary>
    public const int MaxToolCallTimeoutSeconds = int.MaxValue / 1000;

    public static string? Get(string name, string legacyName) =>
        Environment.GetEnvironmentVariable(name)
        ?? Environment.GetEnvironmentVariable(legacyName);

    public static bool GetBool(string name, string legacyName, bool defaultValue)
    {
        var (value, warning) = ParseBool(name, Get(name, legacyName), defaultValue);
        Report(warning);
        return value;
    }

    public static int GetInt(string name, string legacyName, int defaultValue, int minimum, int maximum)
    {
        var (value, warning) = ParseInt(name, Get(name, legacyName), defaultValue, minimum, maximum);
        Report(warning);
        return value;
    }

    /// <summary>SHARPLENS_TIMEOUT_SECONDS (legacy ROSLYN_TIMEOUT_SECONDS): a positive budget, clamped to the timer maximum.</summary>
    public static int GetToolCallTimeoutSeconds() =>
        GetInt("SHARPLENS_TIMEOUT_SECONDS", "ROSLYN_TIMEOUT_SECONDS",
            DefaultToolCallTimeoutSeconds, minimum: 1, maximum: MaxToolCallTimeoutSeconds);

    // Unset or empty means "not configured": the default applies without a warning.
    internal static (bool Value, string? Warning) ParseBool(string name, string? raw, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (defaultValue, null);
        if (bool.TryParse(raw.Trim(), out var parsed)) return (parsed, null);
        return (defaultValue,
            $"{name}='{raw}' is not true or false; using the default {(defaultValue ? "true" : "false")}");
    }

    internal static (int Value, string? Warning) ParseInt(
        string name, string? raw, int defaultValue, int minimum, int maximum)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (defaultValue, null);
        // BigInteger so an out-of-int-range value is still recognized as an integer and
        // clamped, rather than misreported as "not an integer".
        if (!BigInteger.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            return (defaultValue, $"{name}='{raw}' is not an integer; using the default {defaultValue}");
        }
        if (parsed < minimum)
        {
            return (defaultValue, $"{name}='{raw}' is below the minimum {minimum}; using the default {defaultValue}");
        }
        if (parsed > maximum)
        {
            return (maximum, $"{name}='{raw}' is above the maximum {maximum}; using the maximum {maximum}");
        }
        return ((int)parsed, null);
    }

    // Several components read the same variable (the timeout is read by the dispatcher and
    // reported by health_check), so each distinct warning is written once per process.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ReportedWarnings = new();

    private static void Report(string? warning)
    {
        if (warning != null && ReportedWarnings.TryAdd(warning, 0)) Console.Error.WriteLine($"[Warning] {warning}");
    }
}
