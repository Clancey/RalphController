using RalphController.Models;

namespace RalphController;

/// <summary>
/// Controls the Ralph loop - manages state, iterations, and lifecycle
/// </summary>
public class LoopController : IDisposable
{
    private readonly RalphConfig _config;
    private readonly LoopStatistics _statistics;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly ResponseAnalyzer _responseAnalyzer;
    private readonly RateLimiter _rateLimiter;
    private readonly ModelSelector _modelSelector;
    private AIProcess? _currentProcess;
    private OllamaClient? _ollamaClient;
    private CancellationTokenSource? _loopCts;
    private TaskCompletionSource? _pauseTcs;
    private string? _injectedPrompt;
    private readonly object _stateLock = new();
    private bool _disposed;
    private DateTimeOffset? _providerRateLimitUntil;
    private string? _providerRateLimitMessage;
    private bool _pendingFinalVerification;
    private bool _inFinalVerification;

    /// <summary>Current state of the loop</summary>
    public LoopState State { get; private set; } = LoopState.Idle;

    /// <summary>Statistics for the current run</summary>
    public LoopStatistics Statistics => _statistics;

    /// <summary>Configuration for this controller</summary>
    public RalphConfig Config => _config;

    /// <summary>Circuit breaker for stagnation detection</summary>
    public CircuitBreaker CircuitBreaker => _circuitBreaker;

    /// <summary>Response analyzer for completion detection</summary>
    public ResponseAnalyzer ResponseAnalyzer => _responseAnalyzer;

    /// <summary>Rate limiter for API call management</summary>
    public RateLimiter RateLimiter => _rateLimiter;

    /// <summary>Model selector for multi-model support</summary>
    public ModelSelector ModelSelector => _modelSelector;

    /// <summary>Fired when an iteration starts (iteration number, model name or null)</summary>
    public event Action<int, string?>? OnIterationStart;

    /// <summary>Fired when the model switches (for multi-model mode)</summary>
    public event Action<ModelSpec, string>? OnModelSwitch;

    /// <summary>Fired when verification starts</summary>
    public event Action<ModelSpec>? OnVerificationStart;

    /// <summary>Fired when verification completes (passed, filesChanged)</summary>
    public event Action<bool, int>? OnVerificationComplete;

    /// <summary>Fired when final verification starts</summary>
    public event Action? OnFinalVerificationStart;

    /// <summary>Fired when final verification completes (allComplete, incompleteTasks)</summary>
    public event Action<bool, List<string>>? OnFinalVerificationComplete;

    /// <summary>Fired when an iteration completes</summary>
    public event Action<int, AIResult>? OnIterationComplete;

    /// <summary>Fired when output is received from AI</summary>
    public event Action<string>? OnOutput;

    /// <summary>Fired when error output is received from AI</summary>
    public event Action<string>? OnError;

    /// <summary>Fired when the loop state changes</summary>
    public event Action<LoopState>? OnStateChanged;

    /// <summary>Fired when the loop completes (finished or stopped)</summary>
    public event Action<bool>? OnLoopComplete;

    public LoopController(RalphConfig config)
    {
        _config = config;
        _statistics = new LoopStatistics { CostPerHour = config.CostPerHour };
        _circuitBreaker = new CircuitBreaker();
        _responseAnalyzer = new ResponseAnalyzer();
        _rateLimiter = new RateLimiter(config.MaxCallsPerHour);
        _modelSelector = new ModelSelector(config.MultiModel, config.ProviderConfig);

        // Wire up circuit breaker events
        _circuitBreaker.OnStateChanged += (state, reason) =>
        {
            if (state == CircuitState.Open)
            {
                OnError?.Invoke($"Circuit breaker opened: {reason}");
            }
        };

        // Wire up model selector events
        _modelSelector.OnModelSwitch += (model, reason) => OnModelSwitch?.Invoke(model, reason);
        _modelSelector.OnVerificationStart += model => OnVerificationStart?.Invoke(model);
        _modelSelector.OnVerificationComplete += (passed, filesChanged) => OnVerificationComplete?.Invoke(passed, filesChanged);
    }

    /// <summary>
    /// Start the Ralph loop
    /// </summary>
    public async Task StartAsync(CancellationToken externalCancellation = default)
    {
        lock (_stateLock)
        {
            if (State != LoopState.Idle)
            {
                throw new InvalidOperationException($"Cannot start loop from state: {State}");
            }
            SetState(LoopState.Running);
        }

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        _statistics.Reset();
        _modelSelector.Reset();
        _pendingFinalVerification = false;
        _inFinalVerification = false;

        try
        {
            await RunLoopAsync(_loopCts.Token);
            OnLoopComplete?.Invoke(true);
        }
        catch (OperationCanceledException)
        {
            OnLoopComplete?.Invoke(false);
        }
        finally
        {
            lock (_stateLock)
            {
                SetState(LoopState.Idle);
            }
        }
    }

    /// <summary>
    /// Pause the loop after the current iteration completes
    /// </summary>
    public void Pause()
    {
        lock (_stateLock)
        {
            if (State != LoopState.Running)
            {
                return;
            }
            SetState(LoopState.Paused);
            _pauseTcs = new TaskCompletionSource();
        }
    }

    /// <summary>
    /// Resume a paused loop
    /// </summary>
    public void Resume()
    {
        lock (_stateLock)
        {
            if (State != LoopState.Paused)
            {
                return;
            }
            SetState(LoopState.Running);
            _pauseTcs?.TrySetResult();
            _pauseTcs = null;
        }
    }

    /// <summary>
    /// Stop the loop after the current iteration completes
    /// </summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            if (State == LoopState.Idle || State == LoopState.Stopping)
            {
                return;
            }

            SetState(LoopState.Stopping);

            // If paused, release the pause wait
            _pauseTcs?.TrySetResult();
            _pauseTcs = null;
        }
    }

    /// <summary>
    /// Force stop immediately (kills current process)
    /// </summary>
    public async Task ForceStopAsync()
    {
        Stop();

        if (_currentProcess is not null)
        {
            await _currentProcess.StopAsync(TimeSpan.FromSeconds(2));
        }

        _loopCts?.Cancel();
    }

    /// <summary>
    /// Inject a one-time prompt to use for the next iteration
    /// </summary>
    public void InjectPrompt(string prompt)
    {
        _injectedPrompt = prompt;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Check if we should stop
            if (State == LoopState.Stopping)
            {
                break;
            }

            if (await WaitForProviderRateLimitAsync(cancellationToken))
            {
                continue;
            }

            // Check for max iterations
            if (_config.MaxIterations.HasValue && _statistics.CurrentIteration >= _config.MaxIterations.Value)
            {
                OnOutput?.Invoke($"Max iterations ({_config.MaxIterations}) reached");
                break;
            }

            // Check circuit breaker
            if (_config.EnableCircuitBreaker && !_circuitBreaker.CanExecute())
            {
                OnError?.Invoke($"Circuit breaker is open: {_circuitBreaker.OpenReason}");
                break;
            }

            // Check rate limiter
            if (!_rateLimiter.TryAcquire())
            {
                OnOutput?.Invoke($"Rate limit reached ({_rateLimiter.MaxCallsPerHour}/hour). Waiting {_rateLimiter.TimeUntilReset:mm\\:ss}...");
                await _rateLimiter.WaitForSlotAsync(cancellationToken);
                continue;
            }

            // Handle pause
            if (State == LoopState.Paused)
            {
                var pauseTcs = _pauseTcs;
                if (pauseTcs is not null)
                {
                    await pauseTcs.Task;
                }
                continue;
            }

            // Run an iteration
            await RunIterationAsync(cancellationToken);

            // Delay between iterations
            if (_config.IterationDelayMs > 0 && State == LoopState.Running)
            {
                await Task.Delay(_config.IterationDelayMs, cancellationToken);
            }
        }
    }

    private async Task RunIterationAsync(CancellationToken cancellationToken)
    {
        _statistics.StartIteration();

        // Get current model name for display (before firing event)
        var currentModel = _modelSelector.GetCurrentModel();
        var modelName = (_config.MultiModel?.IsEnabled == true && currentModel != null)
            ? currentModel.DisplayName
            : null;
        OnIterationStart?.Invoke(_statistics.CurrentIteration, modelName);

        // Get prompt (injected, final verification, or from file)
        string prompt;
        if (_pendingFinalVerification)
        {
            // Inject the final verification prompt
            _pendingFinalVerification = false;
            _inFinalVerification = true;
            OnFinalVerificationStart?.Invoke();
            OnOutput?.Invoke("");
            OnOutput?.Invoke("╔══════════════════════════════════════════════════════════════╗");
            OnOutput?.Invoke("║           FINAL VERIFICATION - Reviewing all tasks           ║");
            OnOutput?.Invoke("╚══════════════════════════════════════════════════════════════╝");
            OnOutput?.Invoke("");
            prompt = FinalVerification.GetVerificationPrompt(_config.PlanFilePath);
        }
        else if (_injectedPrompt is not null)
        {
            prompt = _injectedPrompt;
            _injectedPrompt = null;
        }
        else
        {
            prompt = await GetPromptAsync();
        }

        // Get current provider from ModelSelector (handles multi-model)
        // Note: currentModel already retrieved above for the iteration header
        var currentProvider = _modelSelector.GetCurrentProvider();
        var currentProviderConfig = _modelSelector.GetCurrentProviderConfig();
        var isVerification = _modelSelector.IsVerificationIteration;

        if (isVerification && currentModel != null)
        {
            OnOutput?.Invoke($"[Verification] Running with {currentModel.DisplayName}...");
        }
        // Note: Model name is now shown in the iteration header, so no need to output here

        // Create and run process - use OllamaClient for Ollama provider
        AIResult result;
        if (currentProvider == AIProvider.Ollama)
        {
            // For Ollama, use OllamaClient with streaming support
            var baseUrl = currentProviderConfig.ExecutablePath ?? "http://localhost:11434";
            var model = currentProviderConfig.Arguments ?? "llama3.1:8b";

            _ollamaClient = new OllamaClient(baseUrl, model, _config.TargetDirectory);
            _ollamaClient.OnOutput += text => OnOutput?.Invoke(text);
            _ollamaClient.OnToolCall += (name, args) => OnOutput?.Invoke($"[Tool: {name}]");
            _ollamaClient.OnToolResult += (name, res) =>
            {
                var preview = res.Length > 500 ? res.Substring(0, 500) + "..." : res;
                OnOutput?.Invoke($"[Result: {preview}]");
            };
            _ollamaClient.OnError += err => OnError?.Invoke(err);

            try
            {
                var ollamaResult = await _ollamaClient.RunAsync(prompt, cancellationToken);
                result = new AIResult
                {
                    Success = ollamaResult.Success,
                    ExitCode = ollamaResult.Success ? 0 : 1,
                    Output = ollamaResult.Output,
                    Error = ollamaResult.Error
                };
            }
            finally
            {
                _ollamaClient.Dispose();
                _ollamaClient = null;
            }
        }
        else
        {
            // For other providers, use AIProcess with dynamic config
            var dynamicConfig = _config with { ProviderConfig = currentProviderConfig, Provider = currentProvider };
            _currentProcess = new AIProcess(dynamicConfig);
            _currentProcess.OnOutput += line => OnOutput?.Invoke(line);
            _currentProcess.OnError += line => OnError?.Invoke(line);

            try
            {
                result = await _currentProcess.RunAsync(prompt, cancellationToken);
            }
            finally
            {
                _currentProcess.Dispose();
                _currentProcess = null;
            }
        }

        _statistics.CompleteIteration(result.Success);
        OnIterationComplete?.Invoke(_statistics.CurrentIteration, result);

        // Count modified files for circuit breaker and verification
        var filesModified = CountModifiedFiles();

        // Record result with circuit breaker
        if (_config.EnableCircuitBreaker)
        {
            _circuitBreaker.RecordResult(result, filesModified);
        }

        // Handle multi-model logic (rotation, verification)
        _modelSelector.AfterIteration(filesModified);

        // Check if this was a verification iteration
        if (isVerification)
        {
            var verificationPassed = _modelSelector.CheckVerificationPassed(filesModified);
            if (verificationPassed)
            {
                OnOutput?.Invoke("[Verification PASSED] No changes made - task complete!");
                Stop();
                return;
            }
            else
            {
                OnOutput?.Invoke($"[Verification FAILED] Verifier made changes - continuing work...");
                _modelSelector.ResetVerification();
                // Don't stop - continue loop with primary model
                return;
            }
        }

        // Check if this was a final verification iteration
        if (_inFinalVerification)
        {
            _inFinalVerification = false;
            var verificationResult = FinalVerification.ParseVerificationResult(result.Output);

            if (verificationResult != null)
            {
                OnFinalVerificationComplete?.Invoke(verificationResult.AllTasksComplete, verificationResult.IncompleteTasks);

                if (verificationResult.AllTasksComplete)
                {
                    OnOutput?.Invoke($"[Final Verification PASSED] All {verificationResult.CompletedTasks.Count} tasks verified complete!");
                    if (!string.IsNullOrEmpty(verificationResult.Summary))
                    {
                        OnOutput?.Invoke($"Summary: {verificationResult.Summary}");
                    }
                    Stop();
                    return;
                }
                else
                {
                    OnOutput?.Invoke($"[Final Verification INCOMPLETE] Found {verificationResult.IncompleteTasks.Count} incomplete task(s):");
                    foreach (var task in verificationResult.IncompleteTasks)
                    {
                        OnOutput?.Invoke($"  - {task}");
                    }
                    OnOutput?.Invoke("Continuing work on incomplete tasks...");
                    // Don't stop - continue with standard prompt
                    return;
                }
            }
            else
            {
                // Couldn't parse verification result, check if output indicates more work
                OnOutput?.Invoke("[Final Verification] Could not parse structured result, continuing...");
                // Don't stop - let the AI continue working
                return;
            }
        }

        // Analyze response for completion signals
        if (_config.EnableResponseAnalyzer)
        {
            var analysis = _responseAnalyzer.Analyze(result);

            if (analysis.ShouldExit && _config.AutoExitOnCompletion)
            {
                // Check if we need to run multi-model verification first
                if (_config.MultiModel?.Strategy == ModelSwitchStrategy.Verification)
                {
                    _modelSelector.OnCompletionDetected(filesModified);
                    _responseAnalyzer.Reset(); // Reset to prevent re-triggering during verification
                    OnOutput?.Invoke($"Completion detected: {analysis.ExitReason} - running model verification...");
                    // Don't stop - let next iteration run with verifier
                    return;
                }
                // Check if we need to run final task verification
                else if (_config.EnableFinalVerification)
                {
                    _pendingFinalVerification = true;
                    _responseAnalyzer.Reset(); // Reset to prevent re-triggering during verification
                    OnOutput?.Invoke("");
                    OnOutput?.Invoke($">>> Completion signal detected: {analysis.ExitReason}");
                    OnOutput?.Invoke(">>> Scheduling final verification for next iteration...");
                    // Don't stop - let next iteration run verification prompt
                    return;
                }
                else
                {
                    OnOutput?.Invoke($"Completion detected: {analysis.ExitReason}");
                    Stop();
                }
            }
        }

        // Handle rate limits with fallback
        var rateLimitInfo = ResponseAnalyzer.TryDetectRateLimit(result);
        if (rateLimitInfo is not null)
        {
            // Notify model selector for fallback strategy
            _modelSelector.OnIterationFailed(isRateLimit: true);

            var resetAt = rateLimitInfo.ResetAt ?? DateTimeOffset.UtcNow.AddMinutes(30);
            _providerRateLimitUntil = resetAt;
            _providerRateLimitMessage = rateLimitInfo.Message;

            var localReset = resetAt.ToLocalTime();
            var resetText = rateLimitInfo.ResetAt.HasValue
                ? $"{localReset:MMM d h:mm tt}"
                : $"{localReset:MMM d h:mm tt} (fallback)";

            var message = string.IsNullOrWhiteSpace(_providerRateLimitMessage)
                ? "Provider rate limit detected"
                : $"Provider rate limit detected: {_providerRateLimitMessage}";

            OnOutput?.Invoke($"{message}. Waiting until {resetText}.");
        }
        else if (!result.Success)
        {
            // Notify model selector for fallback strategy on failures
            _modelSelector.OnIterationFailed(isRateLimit: false);
        }
    }

    private int CountModifiedFiles()
    {
        try
        {
            // Use git to count recently modified files
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "status --porcelain",
                WorkingDirectory = _config.TargetDirectory,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return 0;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // Count lines (each modified file is one line)
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<string> GetPromptAsync()
    {
        var promptPath = _config.PromptFilePath;
        if (!File.Exists(promptPath))
        {
            throw new FileNotFoundException($"Prompt file not found: {promptPath}");
        }

        return await File.ReadAllTextAsync(promptPath);
    }

    private void SetState(LoopState newState)
    {
        if (State != newState)
        {
            State = newState;
            OnStateChanged?.Invoke(newState);
        }
    }

    private async Task<bool> WaitForProviderRateLimitAsync(CancellationToken cancellationToken)
    {
        if (!_providerRateLimitUntil.HasValue)
            return false;

        var until = _providerRateLimitUntil.Value;
        var now = DateTimeOffset.UtcNow;
        if (until <= now)
        {
            _providerRateLimitUntil = null;
            _providerRateLimitMessage = null;
            return false;
        }

        var remaining = until - now;
        var localReset = until.ToLocalTime();
        OnOutput?.Invoke($"Provider rate limit active. Waiting {remaining:hh\\:mm} (resets {localReset:MMM d h:mm tt}).");

        while (DateTimeOffset.UtcNow < until && !cancellationToken.IsCancellationRequested)
        {
            var delay = until - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.FromMinutes(1))
            {
                delay = TimeSpan.FromMinutes(1);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        _providerRateLimitUntil = null;
        _providerRateLimitMessage = null;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _currentProcess?.Dispose();
        _ollamaClient?.Dispose();
        _pauseTcs?.TrySetCanceled();

        GC.SuppressFinalize(this);
    }
}
