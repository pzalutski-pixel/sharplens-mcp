using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpLensMcp;

/// <summary>
/// Proxies tool calls to the worker process via stdin/stdout JSON-RPC.
/// Runs in the main process and communicates with WorkerHost in the worker process.
/// Requests are processed sequentially (one at a time).
/// </summary>
public class RoslynWorkerProxy : IDisposable
{
    private readonly Process _workerProcess;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _readerTask;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private TaskCompletionSource<JsonObject>? _pendingResponse;
    private bool _disposed;

    public RoslynWorkerProxy(Process workerProcess)
    {
        _workerProcess = workerProcess ?? throw new ArgumentNullException(nameof(workerProcess));
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        // Start reading responses from worker stdout
        _readerTask = Task.Run(ReadResponsesAsync);
    }

    /// <summary>
    /// Gets whether the worker process is still running.
    /// </summary>
    public bool IsWorkerAlive => !_workerProcess.HasExited;

    /// <summary>
    /// Gets the worker process ID.
    /// </summary>
    public int WorkerProcessId => _workerProcess.Id;

    /// <summary>
    /// Invokes a tool on the worker and returns the result.
    /// </summary>
    public async Task<object> InvokeToolAsync(string toolName, Dictionary<string, object?>? arguments, int timeoutMs = 30000)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RoslynWorkerProxy));

        if (!IsWorkerAlive)
            throw new InvalidOperationException("Worker process has exited");

        var request = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "invoke_tool",
            @params = new
            {
                tool = toolName,
                arguments = arguments ?? new Dictionary<string, object?>()
            }
        };

        var response = await SendRequestAsync(request, timeoutMs);

        // Check for JSON-RPC error
        if (response.TryGetPropertyValue("error", out var errorNode) && errorNode != null)
        {
            var errorCode = errorNode["code"]?.GetValue<int>() ?? -1;
            var errorMessage = errorNode["message"]?.GetValue<string>() ?? "Unknown error";
            throw new InvalidOperationException($"Worker error ({errorCode}): {errorMessage}");
        }

        // Return the result
        if (response.TryGetPropertyValue("result", out var resultNode) && resultNode != null)
        {
            return JsonSerializer.Deserialize<object>(resultNode.ToJsonString(), _jsonOptions)
                ?? new { success = true };
        }

        return new { success = true };
    }

    /// <summary>
    /// Sends a ping to verify worker is responsive.
    /// </summary>
    public async Task<bool> PingAsync(int timeoutMs = 5000)
    {
        if (_disposed || !IsWorkerAlive)
            return false;

        try
        {
            var request = new { jsonrpc = "2.0", id = 1, method = "ping" };
            await SendRequestAsync(request, timeoutMs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Shuts down the worker by closing stdin (triggering EOF).
    /// </summary>
    public async Task<bool> ShutdownAsync(int timeoutMs = 5000)
    {
        if (_disposed || !IsWorkerAlive)
            return true;

        try
        {
            // Close stdin to signal EOF - worker will exit gracefully
            _workerProcess.StandardInput.Close();

            // Wait for process to exit
            using var exitCts = new CancellationTokenSource(timeoutMs);
            await _workerProcess.WaitForExitAsync(exitCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<JsonObject> SendRequestAsync(object request, int timeoutMs)
    {
        await _requestLock.WaitAsync(_shutdownCts.Token);
        try
        {
            var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingResponse = tcs;

            var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
            LogDebug($"Sending to worker: {requestJson}");

            await _workerProcess.StandardInput.WriteLineAsync(requestJson);
            await _workerProcess.StandardInput.FlushAsync();

            return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), _shutdownCts.Token);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Request timed out after {timeoutMs}ms");
        }
        finally
        {
            _pendingResponse = null;
            _requestLock.Release();
        }
    }

    private async Task ReadResponsesAsync()
    {
        try
        {
            var reader = _workerProcess.StandardOutput;

            while (!_shutdownCts.Token.IsCancellationRequested && !_workerProcess.HasExited)
            {
                var line = await reader.ReadLineAsync();
                if (line == null)
                {
                    LogDebug("Worker stdout closed (EOF)");
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                LogDebug($"Received from worker: {line}");

                try
                {
                    var response = JsonSerializer.Deserialize<JsonObject>(line);
                    if (response == null)
                        continue;

                    _pendingResponse?.TrySetResult(response);
                }
                catch (JsonException ex)
                {
                    LogDebug($"Failed to parse worker response: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            LogDebug($"Reader task error: {ex}");
        }
        finally
        {
            _pendingResponse?.TrySetException(new InvalidOperationException("Worker connection closed"));
        }
    }

    private static void LogDebug(string message) => Logger.Log("Proxy", LogLevel.Debug, message);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _shutdownCts.Cancel();
        _pendingResponse?.TrySetCanceled();

        try
        {
            _readerTask.Wait(1000);
        }
        catch
        {
            // Ignore cleanup errors
        }

        _requestLock.Dispose();
        _shutdownCts.Dispose();
    }
}
