namespace SharpLensMcp;

/// <summary>
/// Log severity levels in increasing order of importance.
/// </summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4
}

/// <summary>
/// Shared logging utility for consistent log formatting across all components.
/// Logs to stderr with timestamp, source, and level.
/// </summary>
public static class Logger
{
    private static LogLevel? _cachedConfiguredLevel;

    /// <summary>
    /// Logs a message to stderr if the configured log level allows it.
    /// </summary>
    /// <param name="source">The source component (e.g., "Worker", "Proxy", "ProcessManager")</param>
    /// <param name="level">Log level</param>
    /// <param name="message">The message to log</param>
    public static void Log(string source, LogLevel level, string message)
    {
        var configuredLevel = GetConfiguredLevel();

        if (level >= configuredLevel)
        {
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{source}] [{level}] {message}");
        }
    }

    private static LogLevel GetConfiguredLevel()
    {
        if (_cachedConfiguredLevel.HasValue)
            return _cachedConfiguredLevel.Value;

        var envValue = Environment.GetEnvironmentVariable("ROSLYN_LOG_LEVEL");
        _cachedConfiguredLevel = Enum.TryParse<LogLevel>(envValue, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

        return _cachedConfiguredLevel.Value;
    }
}
