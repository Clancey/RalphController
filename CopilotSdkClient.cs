using System.Text;
using GitHub.Copilot.SDK;

namespace RalphController;

/// <summary>
/// GitHub Copilot SDK (.NET) client with streaming and built-in tool execution.
/// The SDK handles tool execution internally — no manual tool implementations needed.
/// </summary>
public class CopilotSdkClient : IAsyncDisposable, IDisposable
{
    private readonly string _workingDirectory;
    private readonly string _model;
    private readonly string? _githubToken;
    private CopilotClient? _client;
    private bool _disposed;
    private bool _stopRequested;

    /// <summary>Fired when text output is received (streaming)</summary>
    public event Action<string>? OnOutput;

    /// <summary>Fired when a tool is being called</summary>
    public event Action<string, string>? OnToolCall;

    /// <summary>Fired when a tool returns a result</summary>
    public event Action<string, string>? OnToolResult;

    /// <summary>Fired when an error occurs</summary>
    public event Action<string>? OnError;

    /// <summary>Fired when an iteration completes</summary>
    public event Action<int>? OnIterationComplete;

    public CopilotSdkClient(string workingDirectory, string model, string? githubToken = null)
    {
        _workingDirectory = workingDirectory;
        _model = model;
        // Auth resolution: constructor param → COPILOT_GITHUB_TOKEN → GH_TOKEN → SDK default (logged-in user)
        _githubToken = githubToken
            ?? Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN")
            ?? Environment.GetEnvironmentVariable("GH_TOKEN");
    }

    public void Stop() => _stopRequested = true;

    /// <summary>
    /// Run a complete session with the given prompt, streaming output
    /// </summary>
    public async Task<CopilotSdkResult> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        _stopRequested = false;
        var iterations = 0;

        try
        {
            var options = new CopilotClientOptions();
            if (!string.IsNullOrEmpty(_githubToken))
            {
                options.GithubToken = _githubToken;
            }

            _client = new CopilotClient(options);
            await _client.StartAsync();

            await using var session = await _client.CreateSessionAsync(new SessionConfig
            {
                Model = _model,
                Streaming = true,
                Hooks = new SessionHooks
                {
                    OnPreToolUse = (input, invocation) =>
                    {
                        OnToolCall?.Invoke(input.ToolName, input.ToolArgs?.ToString() ?? "");
                        return Task.FromResult<PreToolUseHookOutput?>(
                            new PreToolUseHookOutput { PermissionDecision = "allow" }
                        );
                    },
                    OnPostToolUse = (input, invocation) =>
                    {
                        OnToolResult?.Invoke(input.ToolName, "completed");
                        iterations++;
                        OnIterationComplete?.Invoke(iterations);
                        return Task.FromResult<PostToolUseHookOutput?>(null);
                    },
                    OnErrorOccurred = (input, invocation) =>
                    {
                        var errMsg = $"{input.ErrorContext}: {input.Error}";
                        errorBuilder.AppendLine(errMsg);
                        OnError?.Invoke(errMsg);
                        return Task.FromResult<ErrorOccurredHookOutput?>(
                            new ErrorOccurredHookOutput { ErrorHandling = "skip" }
                        );
                    }
                }
            });

            var done = new TaskCompletionSource();
            using var ctReg = cancellationToken.Register(() => done.TrySetCanceled());

            session.On(evt =>
            {
                if (_stopRequested || cancellationToken.IsCancellationRequested)
                {
                    done.TrySetResult();
                    return;
                }

                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        var text = delta.Data.DeltaContent;
                        if (!string.IsNullOrEmpty(text))
                        {
                            outputBuilder.Append(text);
                            OnOutput?.Invoke(text);
                        }
                        break;

                    case AssistantMessageEvent msg:
                        // Final message — if we didn't get deltas, capture content
                        if (outputBuilder.Length == 0 && !string.IsNullOrEmpty(msg.Data.Content))
                        {
                            outputBuilder.Append(msg.Data.Content);
                            OnOutput?.Invoke(msg.Data.Content);
                        }
                        break;

                    case SessionIdleEvent:
                        iterations++;
                        OnIterationComplete?.Invoke(iterations);
                        done.TrySetResult();
                        break;
                }
            });

            await session.SendAsync(new MessageOptions { Prompt = prompt });

            try
            {
                await done.Task;
            }
            catch (OperationCanceledException)
            {
                // Cancelled via token — fall through to result
            }

            return new CopilotSdkResult
            {
                Success = errorBuilder.Length == 0,
                Output = outputBuilder.ToString(),
                Error = errorBuilder.ToString(),
                Iterations = iterations
            };
        }
        catch (OperationCanceledException)
        {
            return new CopilotSdkResult
            {
                Success = false,
                Output = outputBuilder.ToString(),
                Error = "Operation cancelled",
                Iterations = iterations
            };
        }
        catch (Exception ex)
        {
            errorBuilder.AppendLine($"Exception: {ex.Message}");
            OnError?.Invoke(ex.Message);

            return new CopilotSdkResult
            {
                Success = false,
                Output = outputBuilder.ToString(),
                Error = errorBuilder.ToString(),
                Iterations = iterations
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _client = null;

        GC.SuppressFinalize(this);
    }
}

public record CopilotSdkResult
{
    public required bool Success { get; init; }
    public required string Output { get; init; }
    public required string Error { get; init; }
    public required int Iterations { get; init; }
}
