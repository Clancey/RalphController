using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RalphController.Models;

namespace RalphController;

/// <summary>
/// Manages spawning and controlling AI CLI processes (Claude, Codex, etc.)
/// </summary>
public class AIProcess : IDisposable
{
    private Process? _process;
    private readonly RalphConfig _config;
    private readonly AIProviderConfig _providerConfig;
    private readonly StringBuilder _outputBuffer = new();
    private readonly StringBuilder _errorBuffer = new();
    private readonly StringBuilder _streamingTextBuffer = new();
    private readonly StringBuilder _lineBuffer = new();
    private readonly object _lock = new();
    private string? _tempPromptFile;
    private bool _disposed;

    /// <summary>Fired when stdout data is received</summary>
    public event Action<string>? OnOutput;

    /// <summary>Fired when stderr data is received</summary>
    public event Action<string>? OnError;

    /// <summary>Fired when the process exits</summary>
    public event Action<int>? OnExit;

    /// <summary>Whether the process is currently running</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Exit code of the last run (null if still running or never ran)</summary>
    public int? ExitCode => _process?.HasExited == true ? _process.ExitCode : null;

    /// <summary>The AI provider being used</summary>
    public AIProvider Provider => _providerConfig.Provider;

    /// <summary>All stdout captured from current/last run</summary>
    public string Output
    {
        get { lock (_lock) return _outputBuffer.ToString(); }
    }

    /// <summary>All stderr captured from current/last run</summary>
    public string Error
    {
        get { lock (_lock) return _errorBuffer.ToString(); }
    }

    public AIProcess(RalphConfig config, AIProviderConfig providerConfig)
    {
        _config = config;
        _providerConfig = providerConfig;
    }

    public AIProcess(RalphConfig config) : this(config, config.ProviderConfig)
    {
    }

    /// <summary>
    /// Starts a new AI process with the given prompt piped to stdin
    /// </summary>
    /// <param name="prompt">The prompt to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<bool> StartAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Process is already running");
        }

        // Clear buffers for new run
        lock (_lock)
        {
            _outputBuffer.Clear();
            _errorBuffer.Clear();
        }

        // Build arguments - some providers take prompt as argument, others via stdin
        var arguments = _providerConfig.Arguments;
        var useShell = false;

        string? scriptFile = null;
        if (_providerConfig.UsesPromptArgument)
        {
            // Write prompt to temp file and create a shell script to execute
            var tempFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(tempFile, prompt, cancellationToken);
            _tempPromptFile = tempFile;

    // Create a temp script file to avoid quoting issues
    scriptFile = Path.GetTempFileName() + ".sh";
    var scriptContent = $"#!/bin/bash\n{_providerConfig.ExecutablePath} {arguments} \"$(cat '{tempFile}')\"";
    await File.WriteAllTextAsync(scriptFile, scriptContent, cancellationToken);

            arguments = scriptFile;
            useShell = true;
        }
        else if (!_providerConfig.UsesStdin)
        {
            // Write prompt to temp file and use shell input redirection
            var tempFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(tempFile, prompt, cancellationToken);
            _tempPromptFile = tempFile;

            // Use shell with input redirection from temp file
            var fullCmd = $"{_providerConfig.ExecutablePath} {arguments} < '{tempFile}'";
            arguments = $"-c \"{fullCmd}\"";
            useShell = true;
        }

        var psi = new ProcessStartInfo
        {
            FileName = useShell ? "/bin/bash" : _providerConfig.ExecutablePath,
            Arguments = arguments,
            WorkingDirectory = _config.TargetDirectory,
            RedirectStandardInput = _providerConfig.UsesStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (_providerConfig.Provider == AIProvider.OpenCode)
        {
            psi.Environment["OPENCODE_DISABLE_AUTOUPDATE"] = "true";
            psi.Environment["OPENCODE_PERMISSION"] = "{\"permission\":\"allow\"}";
        }

        try
        {
            _process = new Process { StartInfo = psi };

            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    if (_providerConfig.UsesStreamJson)
                    {
                        // Parse stream-json format to extract text content
                        var text = ParseStreamJsonLine(e.Data);
                        if (text != null)
                    {
                        lock (_lock)
                        {
                            _streamingTextBuffer.Append(text);
                            _outputBuffer.Append(text);
                            _lineBuffer.Append(text);

                            // Emit complete lines as they become available
                            var content = _lineBuffer.ToString();
                            var lastNewline = content.LastIndexOf('\n');
                            if (lastNewline >= 0)
                            {
                                // Extract and emit complete lines
                                var completeLines = content.Substring(0, lastNewline + 1);
                                _lineBuffer.Clear();
                                _lineBuffer.Append(content.Substring(lastNewline + 1));

                                // Emit each complete line
                                foreach (var line in completeLines.Split('\n', StringSplitOptions.None))
                                {
                                    if (!string.IsNullOrEmpty(line))
                                        OnOutput?.Invoke(line);
                                }
                            }
                        }
                    }
                }
                else if (_providerConfig.Provider == AIProvider.OpenCode)
                {
                    // Parse OpenCode JSON format
                    var text = ParseOpenCodeJsonLine(e.Data);
                    if (text != null)
                    {
                        lock (_lock)
                        {
                            _outputBuffer.Append(text);
                            OnOutput?.Invoke(text);
                        }
                    }
                }
                else
                {
                    lock (_lock) _outputBuffer.AppendLine(e.Data);
                    OnOutput?.Invoke(e.Data);
                }
                }
            };

            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (_lock) _errorBuffer.AppendLine(e.Data);
                    OnError?.Invoke(e.Data);
                }
            };

            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                // Flush any remaining buffered text
                if (_providerConfig.UsesStreamJson)
                {
                    lock (_lock)
                    {
                        if (_lineBuffer.Length > 0)
                        {
                            OnOutput?.Invoke(_lineBuffer.ToString());
                            _lineBuffer.Clear();
                        }
                    }
                }
                OnExit?.Invoke(_process.ExitCode);
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Write prompt to stdin and close it (if provider uses stdin)
            if (_providerConfig.UsesStdin)
            {
                await _process.StandardInput.WriteAsync(prompt);
                _process.StandardInput.Close();
            }

            return true;
        }
        catch (Exception ex)
        {
            var providerName = _providerConfig.Provider.ToString();
            lock (_lock) _errorBuffer.AppendLine($"Failed to start {providerName}: {ex.Message}");
            OnError?.Invoke($"Failed to start {providerName}: {ex.Message}");
            return false;
        }
    }

    private static string EscapeArgument(string arg)
    {
        return arg.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// Parse a line of stream-json output to extract text content
    /// </summary>
    private static string? ParseStreamJsonLine(string line)
    {
        try
        {
            // Skip empty lines or non-JSON
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("{"))
                return null;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Check for content_block_delta with text_delta
            if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "stream_event")
            {
                if (root.TryGetProperty("event", out var eventEl))
                {
                    if (eventEl.TryGetProperty("type", out var eventTypeEl) &&
                        eventTypeEl.GetString() == "content_block_delta")
                    {
                        if (eventEl.TryGetProperty("delta", out var deltaEl) &&
                            deltaEl.TryGetProperty("text", out var textEl))
                        {
                            return textEl.GetString();
                        }
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parse a line of OpenCode JSON output to extract text content
    /// </summary>
    private static string? ParseOpenCodeJsonLine(string line)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("{"))
                return null;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeEl))
            {
                var type = typeEl.GetString();
                if (type == "text" || type == "text_delta" || type == "content_block_delta")
                {
                    // Check if text is directly on root
                    if (root.TryGetProperty("text", out var textEl))
                    {
                        return textEl.GetString();
                    }
                    // Check in part.text
                    if (root.TryGetProperty("part", out var partEl) && partEl.TryGetProperty("text", out textEl))
                    {
                        return textEl.GetString();
                    }
                    // Other formats
                    if (root.TryGetProperty("content", out textEl))
                    {
                        return textEl.GetString();
                    }
                    if (root.TryGetProperty("delta", out var deltaEl))
                    {
                        if (deltaEl.TryGetProperty("text", out textEl))
                        {
                            return textEl.GetString();
                        }
                    }
                }
                else if (type == "error")
                {
                    string? errorMsg = null;
                    if (root.TryGetProperty("error", out var errorEl))
                    {
                        if (errorEl.TryGetProperty("message", out var msgEl))
                        {
                            errorMsg = msgEl.GetString();
                        }
                        else if (errorEl.TryGetProperty("data", out var dataEl) &&
                                 dataEl.TryGetProperty("message", out var dataMsgEl))
                        {
                            errorMsg = dataMsgEl.GetString();
                        }
                    }
                    else if (root.TryGetProperty("part", out var partEl) && partEl.TryGetProperty("error", out errorEl))
                    {
                        if (errorEl.TryGetProperty("message", out var msgEl))
                        {
                            errorMsg = msgEl.GetString();
                        }
                    }
                    if (errorMsg != null)
                    {
                        return $"Error: {errorMsg}";
                    }
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Waits for the process to exit
    /// </summary>
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null)
        {
            throw new InvalidOperationException("Process has not been started");
        }

        await _process.WaitForExitAsync(cancellationToken);
        return _process.ExitCode;
    }

    /// <summary>
    /// Runs a complete iteration - start, wait for exit, return result
    /// </summary>
    public async Task<AIResult> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var started = await StartAsync(prompt, cancellationToken);
        if (!started)
        {
            return new AIResult
            {
                Success = false,
                ExitCode = -1,
                Output = Output,
                Error = Error
            };
        }

        var exitCode = await WaitForExitAsync(cancellationToken);

        return new AIResult
        {
            Success = exitCode == 0,
            ExitCode = exitCode,
            Output = Output,
            Error = Error
        };
    }

    /// <summary>
    /// Attempts to gracefully stop the process, then forcefully if needed
    /// </summary>
    public async Task StopAsync(TimeSpan? gracePeriod = null)
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        gracePeriod ??= TimeSpan.FromSeconds(5);

        try
        {
            // Try graceful termination first (SIGINT on Unix, Ctrl+C on Windows)
            if (OperatingSystem.IsWindows())
            {
                // On Windows, we can't easily send Ctrl+C, so we just kill
                _process.Kill(entireProcessTree: true);
            }
            else
            {
                // On Unix, send SIGINT first
                Process.Start("kill", $"-INT {_process.Id}");

                // Wait for grace period
                var exited = await WaitForExitWithTimeoutAsync(gracePeriod.Value);

                if (!exited)
                {
                    // Force kill
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
    }

    private async Task<bool> WaitForExitWithTimeoutAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process!.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_process is not null)
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch { /* ignore */ }
            }
            _process.Dispose();
        }

        // Clean up temp prompt file if used
        if (_tempPromptFile is not null && File.Exists(_tempPromptFile))
        {
            try { File.Delete(_tempPromptFile); }
            catch { /* ignore */ }
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Result of an AI process execution
/// </summary>
public record AIResult
{
    public required bool Success { get; init; }
    public required int ExitCode { get; init; }
    public required string Output { get; init; }
    public required string Error { get; init; }
}
