using System.Diagnostics;

namespace SharpLensMcp;

/// <summary>
/// Manages the worker process lifecycle: spawning, monitoring, and termination.
/// The worker process runs the same executable with --worker flag.
/// </summary>
public class ProcessManager : IDisposable
{
    private Process? _workerProcess;
    private RoslynWorkerProxy? _proxy;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _spawnLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// The last solution path that was loaded, used for auto-reload after respawn.
    /// </summary>
    public string? LastLoadedSolutionPath { get; set; }

    /// <summary>
    /// Gets when the worker was last started.
    /// </summary>
    public DateTime? WorkerStartTime { get; private set; }

    /// <summary>
    /// Gets whether a worker process is currently running.
    /// </summary>
    public bool IsWorkerRunning
    {
        get
        {
            lock (_lock)
            {
                return _workerProcess != null && !_workerProcess.HasExited;
            }
        }
    }

    /// <summary>
    /// Gets the worker process ID, or null if not running.
    /// </summary>
    public int? WorkerProcessId
    {
        get
        {
            lock (_lock)
            {
                return _workerProcess != null && !_workerProcess.HasExited ? _workerProcess.Id : null;
            }
        }
    }

    /// <summary>
    /// Gets worker uptime, or null if not running.
    /// </summary>
    public TimeSpan? WorkerUptime
    {
        get
        {
            if (WorkerStartTime == null || !IsWorkerRunning)
                return null;
            return DateTime.UtcNow - WorkerStartTime.Value;
        }
    }

    /// <summary>
    /// Ensures a worker is running and returns the proxy. Spawns if needed.
    /// </summary>
    public async Task<RoslynWorkerProxy> EnsureWorkerAsync()
    {
        // Fast path: check if worker is already running
        lock (_lock)
        {
            if (_proxy != null && IsWorkerRunning)
            {
                return _proxy;
            }
        }

        // Slow path: acquire async lock to spawn worker (prevents race condition)
        await _spawnLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            lock (_lock)
            {
                if (_proxy != null && IsWorkerRunning)
                {
                    return _proxy;
                }
            }

            // Need to spawn (or respawn) worker
            return await SpawnWorkerAsync();
        }
        finally
        {
            _spawnLock.Release();
        }
    }

    /// <summary>
    /// Spawns a new worker process, killing any existing one first.
    /// </summary>
    public async Task<RoslynWorkerProxy> SpawnWorkerAsync()
    {
        lock (_lock)
        {
            if (_workerProcess != null)
            {
                CleanupWorker();
            }
        }

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine executable path for worker process");

        Log(LogLevel.Information, $"Spawning worker process: {executablePath} --worker");

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "--worker",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Pass through environment variables
            Environment =
            {
                ["DOTNET_SOLUTION_PATH"] = "", // Don't auto-load in worker, we'll send load_solution
                ["ROSLYN_LOG_LEVEL"] = Environment.GetEnvironmentVariable("ROSLYN_LOG_LEVEL") ?? "Information",
                ["ROSLYN_TIMEOUT_SECONDS"] = Environment.GetEnvironmentVariable("ROSLYN_TIMEOUT_SECONDS") ?? "30",
                ["ROSLYN_MAX_DIAGNOSTICS"] = Environment.GetEnvironmentVariable("ROSLYN_MAX_DIAGNOSTICS") ?? "100",
                ["SHARPLENS_ABSOLUTE_PATHS"] = Environment.GetEnvironmentVariable("SHARPLENS_ABSOLUTE_PATHS") ?? "false"
            }
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.Exited += OnWorkerExited;

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start worker process");
        }

        var proxy = new RoslynWorkerProxy(process);

        // Start forwarding stderr (worker logs) with exception handling (fire-and-forget)
        _ = Task.Run(async () =>
        {
            try
            {
                await ForwardStderrAsync(process);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Debug, $"Stderr forwarding ended: {ex.Message}");
            }
        });

        lock (_lock)
        {
            _workerProcess = process;
            _proxy = proxy;
            WorkerStartTime = DateTime.UtcNow;
        }

        Log(LogLevel.Information, $"Worker process started (PID: {process.Id})");

        // Verify worker is responsive
        var pingOk = await proxy.PingAsync(5000);
        if (!pingOk)
        {
            Log(LogLevel.Warning, "Worker did not respond to initial ping");
        }

        return proxy;
    }

    /// <summary>
    /// Gracefully shuts down the worker, releasing file locks.
    /// </summary>
    public async Task<bool> ShutdownWorkerAsync(bool force = true, int gracefulTimeoutMs = 5000)
    {
        Process? process;
        RoslynWorkerProxy? proxy;

        lock (_lock)
        {
            process = _workerProcess;
            proxy = _proxy;

            if (process == null || process.HasExited)
            {
                CleanupWorker();
                return true;
            }
        }

        Log(LogLevel.Information, "Shutting down worker process...");

        // Try graceful shutdown first
        if (proxy != null)
        {
            var graceful = await proxy.ShutdownAsync(gracefulTimeoutMs);
            if (graceful)
            {
                Log(LogLevel.Information, "Worker shut down gracefully");
                CleanupWorker();
                return true;
            }
        }

        // Graceful failed, force kill if requested
        if (force)
        {
            Log(LogLevel.Warning, "Graceful shutdown failed, force killing worker");
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(new CancellationTokenSource(2000).Token);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Error killing worker: {ex.Message}");
            }
        }

        CleanupWorker();
        return true;
    }

    private void OnWorkerExited(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            var exitCode = _workerProcess?.ExitCode ?? -1;
            Log(LogLevel.Warning, $"Worker process exited (exit code: {exitCode})");
        }
    }

    private static async Task ForwardStderrAsync(Process process)
    {
        try
        {
            var reader = process.StandardError;
            while (!process.HasExited)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;

                // Forward worker logs to MCP stderr
                Console.Error.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, $"Stderr forwarding ended: {ex.Message}");
        }
    }

    private void CleanupWorker()
    {
        lock (_lock)
        {
            _proxy?.Dispose();
            _proxy = null;

            if (_workerProcess != null)
            {
                _workerProcess.Exited -= OnWorkerExited;
                try
                {
                    if (!_workerProcess.HasExited)
                    {
                        _workerProcess.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }
                _workerProcess.Dispose();
                _workerProcess = null;
            }

            WorkerStartTime = null;
        }
    }

    private static void Log(LogLevel level, string message) => Logger.Log("ProcessManager", level, message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CleanupWorker();
        _spawnLock.Dispose();
    }
}
