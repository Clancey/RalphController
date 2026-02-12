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
    private CopilotSdkClient? _copilotSdkClient;
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _iterationSkipCts;
    private TaskCompletionSource? _pauseTcs;
    private string? _injectedPrompt;
    private readonly object _stateLock = new();
    private bool _disposed;
    private DateTimeOffset? _providerRateLimitUntil;
    private string? _providerRateLimitMessage;
    private bool _pendingFinalVerification;
    private bool _inFinalVerification;
    private bool _circuitBreakerVerificationDone;

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
        _circuitBreakerVerificationDone = false;

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
                OnOutput?.Invoke($"[Stop] Called but already {State}, ignoring");
                return;
            }

            OnOutput?.Invoke($"[Stop] Stopping loop from state {State}");
            // Log stack trace to see who called Stop
            var stackTrace = Environment.StackTrace;
            var callerInfo = stackTrace.Split('\n').Skip(1).Take(3);
            OnOutput?.Invoke($"[Stop] Called from: {string.Join(" <- ", callerInfo.Select(s => s.Trim()))}");

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
    /// Skip the current iteration and move to the next one
    /// </summary>
    public void SkipIteration()
    {
        if (State != LoopState.Running)
        {
            return;
        }

        OnOutput?.Invoke("[Skip] Skipping current iteration...");
        _iterationSkipCts?.Cancel();
    }

    /// <summary>
    /// Inject a one-time prompt to use for the next iteration
    /// </summary>
    public void InjectPrompt(string prompt)
    {
        _injectedPrompt = prompt;
    }

    /// <summary>
    /// Inject a one-time prompt with a specific model to use for the next iteration
    /// </summary>
    public void InjectPrompt(string prompt, ModelSpec model)
    {
        _injectedPrompt = prompt;
        _modelSelector.InjectModel(model);
    }

    /// <summary>
    /// Get the model selector for advanced operations
    /// </summary>
    public ModelSelector GetModelSelector() => _modelSelector;

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        OnOutput?.Invoke("[Loop] Starting main loop...");

        while (!cancellationToken.IsCancellationRequested)
        {
            // Check if we should stop
            if (State == LoopState.Stopping)
            {
                OnOutput?.Invoke("[Loop] Exiting: State is Stopping");
                break;
            }

            if (await WaitForProviderRateLimitAsync(cancellationToken))
            {
                continue;
            }

            // Check for max iterations
            if (_config.MaxIterations.HasValue && _statistics.CurrentIteration >= _config.MaxIterations.Value)
            {
                OnOutput?.Invoke("");
                OnOutput?.Invoke($"[Loop] Exiting: Max iterations ({_config.MaxIterations}) reached");
                break;
            }

            // Check circuit breaker
            if (_config.EnableCircuitBreaker && !_circuitBreaker.CanExecute())
            {
                // If final verification is enabled and we haven't run it yet, run it before stopping
                if (_config.EnableFinalVerification && !_circuitBreakerVerificationDone && !_pendingFinalVerification && !_inFinalVerification)
                {
                    OnOutput?.Invoke("");
                    OnOutput?.Invoke("╔══════════════════════════════════════════════════════════════╗");
                    OnOutput?.Invoke("║  CIRCUIT BREAKER - No progress, running final verification   ║");
                    OnOutput?.Invoke("╚══════════════════════════════════════════════════════════════╝");
                    OnOutput?.Invoke($"Reason: {_circuitBreaker.OpenReason}");
                    OnOutput?.Invoke("Running final verification before stopping...");
                    _pendingFinalVerification = true;
                    _circuitBreakerVerificationDone = true; // Mark that we've triggered verification
                    _circuitBreaker.Reset(); // Reset to allow verification iteration to run
                    continue; // Continue to run the verification iteration
                }

                // Circuit breaker triggered after verification or verification disabled
                OnOutput?.Invoke("");
                OnOutput?.Invoke("╔══════════════════════════════════════════════════════════════╗");
                OnOutput?.Invoke("║  CIRCUIT BREAKER OPEN - Loop stopped due to lack of progress ║");
                OnOutput?.Invoke("╚══════════════════════════════════════════════════════════════╝");
                OnOutput?.Invoke($"Reason: {_circuitBreaker.OpenReason}");
                OnOutput?.Invoke("Press Enter to restart, or 'R' to reset and continue.");
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

            // Run an iteration - wrapped in try/catch so unexpected errors don't kill the loop
            try
            {
                OnOutput?.Invoke($"[Loop] Running iteration {_statistics.CurrentIteration + 1}...");
                await RunIterationAsync(cancellationToken);
                OnOutput?.Invoke($"[Loop] Iteration {_statistics.CurrentIteration} completed, State={State}");
            }
            catch (OperationCanceledException)
            {
                // Cancellation is expected - re-throw to exit the loop
                throw;
            }
            catch (Exception ex)
            {
                // Unexpected error in iteration - log and continue to next iteration
                OnError?.Invoke($"[Loop Error] Iteration failed unexpectedly: {ex.Message}");
                OnOutput?.Invoke("");
                OnOutput?.Invoke("╔══════════════════════════════════════════════════════════════╗");
                OnOutput?.Invoke("║  ITERATION FAILED - Recovering and continuing                ║");
                OnOutput?.Invoke("╚══════════════════════════════════════════════════════════════╝");
                OnOutput?.Invoke($"Error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    OnOutput?.Invoke($"Inner: {ex.InnerException.Message}");
                }
                OnOutput?.Invoke("");

                // In multi-model mode, rotate to next model
                if (_config.MultiModel?.IsEnabled == true)
                {
                    _modelSelector.AfterIteration(0);
                    OnOutput?.Invoke("[Loop] Rotating to next model and continuing...");
                }

                // Continue to next iteration
                continue;
            }

            // Delay between iterations
            if (_config.IterationDelayMs > 0 && State == LoopState.Running)
            {
                await Task.Delay(_config.IterationDelayMs, cancellationToken);
            }
        }

        OnOutput?.Invoke($"[Loop] Main loop exited. Final state: {State}, Iterations: {_statistics.CurrentIteration}");
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
        // Wrap in try-catch to handle agent failures gracefully
        // Apply inactivity timeout if configured (timeout resets on any output)
        AIResult result;
        using var timeoutCts = new CancellationTokenSource();
        _iterationSkipCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, _iterationSkipCts.Token);
        var iterationToken = linkedCts.Token;

        // Track last activity for inactivity-based timeout
        var lastActivityTime = DateTime.UtcNow;
        var inactivityTimeout = _config.IterationTimeoutMinutes > 0
            ? TimeSpan.FromMinutes(_config.IterationTimeoutMinutes)
            : TimeSpan.MaxValue;

        // Background task to check for inactivity timeout
        var timeoutTask = Task.Run(async () =>
        {
            while (!timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                var elapsed = DateTime.UtcNow - lastActivityTime;
                if (elapsed >= inactivityTimeout)
                {
                    timeoutCts.Cancel();
                    break;
                }
            }
        }, CancellationToken.None);

        // Action to reset the inactivity timer
        void ResetActivityTimer() => lastActivityTime = DateTime.UtcNow;

        try
        {
            if (currentProvider == AIProvider.Ollama)
            {
                // For Ollama, use OllamaClient with streaming support
                var baseUrl = currentProviderConfig.ExecutablePath ?? "http://localhost:11434";
                var model = currentProviderConfig.Arguments ?? "llama3.1:8b";

                _ollamaClient = new OllamaClient(baseUrl, model, _config.TargetDirectory);
                _ollamaClient.OnOutput += text =>
                {
                    ResetActivityTimer();
                    OnOutput?.Invoke(text);
                };
                _ollamaClient.OnToolCall += (name, args) =>
                {
                    ResetActivityTimer();
                    OnOutput?.Invoke($"[Tool: {name}]");
                };
                _ollamaClient.OnToolResult += (name, res) =>
                {
                    ResetActivityTimer();
                    var preview = res.Length > 500 ? res.Substring(0, 500) + "..." : res;
                    OnOutput?.Invoke($"[Result: {preview}]");
                };
                _ollamaClient.OnError += err =>
                {
                    ResetActivityTimer();
                    OnError?.Invoke(err);
                };

                try
                {
                    var ollamaResult = await _ollamaClient.RunAsync(prompt, iterationToken);
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
            else if (currentProvider == AIProvider.CopilotSdk)
            {
                // For CopilotSdk, use CopilotSdkClient with streaming support
                var model = currentProviderConfig.Arguments ?? "gpt-5";
                var token = string.IsNullOrEmpty(currentProviderConfig.ExecutablePath) ? null : currentProviderConfig.ExecutablePath;

                _copilotSdkClient = new CopilotSdkClient(_config.TargetDirectory, model, token);
                _copilotSdkClient.OnOutput += text =>
                {
                    ResetActivityTimer();
                    OnOutput?.Invoke(text);
                };
                _copilotSdkClient.OnToolCall += (name, args) =>
                {
                    ResetActivityTimer();
                    OnOutput?.Invoke($"[Tool: {name}]");
                };
                _copilotSdkClient.OnToolResult += (name, res) =>
                {
                    ResetActivityTimer();
                    OnOutput?.Invoke($"[Result: {name} completed]");
                };
                _copilotSdkClient.OnError += err =>
                {
                    ResetActivityTimer();
                    OnError?.Invoke(err);
                };

                try
                {
                    var sdkResult = await _copilotSdkClient.RunAsync(prompt, iterationToken);
                    result = new AIResult
                    {
                        Success = sdkResult.Success,
                        ExitCode = sdkResult.Success ? 0 : 1,
                        Output = sdkResult.Output,
                        Error = sdkResult.Error
                    };
                }
                finally
                {
                    _copilotSdkClient.Dispose();
                    _copilotSdkClient = null;
                }
            }
            else
            {
                // For other providers, use AIProcess with dynamic config
                var dynamicConfig = _config with { ProviderConfig = currentProviderConfig, Provider = currentProvider };
                _currentProcess = new AIProcess(dynamicConfig);
                _currentProcess.OnOutput += line =>
                {
                    ResetActivityTimer();
                    OnOutput?.Invoke(line);
                };
                _currentProcess.OnError += line =>
                {
                    ResetActivityTimer();
                    OnError?.Invoke(line);
                };

                try
                {
                    result = await _currentProcess.RunAsync(prompt, iterationToken);
                }
                finally
                {
                    _currentProcess.Dispose();
                    _currentProcess = null;
                }
            }

            // Cancel the timeout checker once we're done
            timeoutCts.Cancel();
        }
        catch (OperationCanceledException) when (_iterationSkipCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            // User skipped the iteration - log and continue to next iteration
            var providerName = currentModel?.DisplayName ?? currentProvider.ToString();
            OnOutput?.Invoke("");
            OnOutput?.Invoke("╔══════════════════════════════════════════════════════════════╗");
            OnOutput?.Invoke($"║  ITERATION SKIPPED: {providerName,-40} ║");
            OnOutput?.Invoke("╚══════════════════════════════════════════════════════════════╝");
            OnOutput?.Invoke("");

            // Create a skipped result
            result = new AIResult
            {
                Success = false,
                ExitCode = -3, // Use -3 to indicate skip
                Output = "",
                Error = "Iteration skipped by user"
            };

            // Record iteration as skipped but continue
            _statistics.CompleteIteration(false);
            OnIterationComplete?.Invoke(_statistics.CurrentIteration, result);

            // Move to next model if in multi-model mode
            if (_config.MultiModel?.IsEnabled == true)
            {
                _modelSelector.AfterIteration(0);
                OnOutput?.Invoke("[Loop] Rotating to next model and continuing...");
            }

            return; // Continue to next iteration
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Iteration timed out due to inactivity - log and continue to next iteration
            var providerName = currentModel?.DisplayName ?? currentProvider.ToString();
            OnError?.Invoke($"[Timeout] No activity for {_config.IterationTimeoutMinutes} minutes");
            OnOutput?.Invoke("");
            OnOutput?.Invoke("╔══════════════════════════════════════════════════════════════╗");
            OnOutput?.Invoke($"║  INACTIVITY TIMEOUT ({_config.IterationTimeoutMinutes} min): {providerName,-34} ║");
            OnOutput?.Invoke("╚══════════════════════════════════════════════════════════════╝");
            OnOutput?.Invoke($"No output received for {_config.IterationTimeoutMinutes} minutes - model appears hung.");
            OnOutput?.Invoke("");

            // Create a timed-out result
            result = new AIResult
            {
                Success = false,
                ExitCode = -2, // Use -2 to indicate timeout
                Output = "",
                Error = $"No activity for {_config.IterationTimeoutMinutes} minutes - model appears hung"
            };

            // Notify model selector of failure for fallback strategy
            _modelSelector.OnIterationFailed(isRateLimit: false);

            // Record iteration as failed but continue
            _statistics.CompleteIteration(false);
            OnIterationComplete?.Invoke(_statistics.CurrentIteration, result);

            // Move to next model if in multi-model mode
            if (_config.MultiModel?.IsEnabled == true)
            {
                _modelSelector.AfterIteration(0);
                OnOutput?.Invoke("[Loop] Rotating to next model and continuing...");
            }

            return; // Continue to next iteration
        }
        catch (OperationCanceledException)
        {
            // User cancellation - this is expected behavior, re-throw to exit loop
            throw;
        }
        catch (Exception ex)
        {
            // Agent failed - log the error and continue to next iteration
            var providerName = currentModel?.DisplayName ?? currentProvider.ToString();
            OnError?.Invoke($"[Agent Error] {providerName} failed: {ex.Message}");
            OnOutput?.Invoke("");
            OnOutput?.Invoke($"╔══════════════════════════════════════════════════════════════╗");
            OnOutput?.Invoke($"║  AGENT FAILED - Moving to next iteration                     ║");
            OnOutput?.Invoke($"╚══════════════════════════════════════════════════════════════╝");
            OnOutput?.Invoke($"Provider: {providerName}");
            OnOutput?.Invoke($"Error: {ex.Message}");
            OnOutput?.Invoke("");

            // Create a failed result
            result = new AIResult
            {
                Success = false,
                ExitCode = -1,
                Output = "",
                Error = ex.Message
            };

            // Notify model selector of failure for fallback strategy
            _modelSelector.OnIterationFailed(isRateLimit: false);

            // Record iteration as failed but continue
            _statistics.CompleteIteration(false);
            OnIterationComplete?.Invoke(_statistics.CurrentIteration, result);

            // Move to next model if in multi-model mode
            if (_config.MultiModel?.IsEnabled == true)
            {
                _modelSelector.AfterIteration(0);
                OnOutput?.Invoke("[Loop] Rotating to next model and continuing...");
            }

            return; // Continue to next iteration
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
            OnOutput?.Invoke($"[Analysis] ShouldExit={analysis.ShouldExit}, Confidence={analysis.ConfidenceScore}, Signal={analysis.HasCompletionSignal}");

            // Reset circuit breaker if we detect any completion signal (to allow time for signal threshold)
            // Only reset on successful iterations - don't let error messages with false positive signals reset the breaker
            if (analysis.HasCompletionSignal && _config.EnableCircuitBreaker && result.Success)
            {
                _circuitBreaker.Reset();
                OnOutput?.Invoke("[Analysis] Completion signal detected - circuit breaker reset");
            }

            // Only consider exiting on successful iterations - failed iterations shouldn't trigger completion
            if (analysis.ShouldExit && _config.AutoExitOnCompletion && result.Success)
            {
                OnOutput?.Invoke($"[Analysis] Exit triggered: {analysis.ExitReason}");
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
                    _circuitBreaker.Reset(); // Reset circuit breaker - verification won't modify files
                    OnOutput?.Invoke("");
                    OnOutput?.Invoke($">>> Completion signal detected: {analysis.ExitReason}");
                    OnOutput?.Invoke(">>> Scheduling final verification for next iteration...");
                    // Don't stop - let next iteration run verification prompt
                    return;
                }
                else
                {
                    OnOutput?.Invoke($"[Analysis] No verification enabled, stopping directly");
                    OnOutput?.Invoke($"Completion detected: {analysis.ExitReason}");
                    Stop();
                }
            }
        }
        else
        {
            OnOutput?.Invoke("[Analysis] Response analyzer disabled");
        }

        // Handle failed iterations FIRST (before rate limit check)
        // This ensures we always rotate on failure even if rate limit isn't detected
        if (!result.Success)
        {
            var providerName = currentModel?.DisplayName ?? currentProvider.ToString();
            OnOutput?.Invoke("");
            OnOutput?.Invoke("╔══════════════════════════════════════════════════════════════╗");
            OnOutput?.Invoke($"║  MODEL FAILED: {providerName,-44} ║");
            OnOutput?.Invoke("╚══════════════════════════════════════════════════════════════╝");
            OnOutput?.Invoke($"Exit code: {result.ExitCode}");
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                // Show first few lines of error
                var errorLines = result.Error.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(3);
                foreach (var line in errorLines)
                {
                    OnOutput?.Invoke($"Error: {line}");
                }
            }

            // Notify model selector for fallback strategy on failures
            _modelSelector.OnIterationFailed(isRateLimit: false);

            // In multi-model mode, rotate to next model
            if (_config.MultiModel?.IsEnabled == true)
            {
                _modelSelector.AfterIteration(0);
                OnOutput?.Invoke("[Rotating to next model...]");
            }
            OnOutput?.Invoke("");
            return; // Continue to next iteration
        }

        // Handle rate limits (in case result.Success is true but there's a rate limit message in output)
        var rateLimitInfo = ResponseAnalyzer.TryDetectRateLimit(result);
        if (rateLimitInfo is not null)
        {
            // Notify model selector for fallback strategy
            _modelSelector.OnIterationFailed(isRateLimit: true);

            var providerName = currentModel?.DisplayName ?? currentProvider.ToString();
            var message = string.IsNullOrWhiteSpace(rateLimitInfo.Message)
                ? "Provider rate limit detected"
                : $"Provider rate limit detected: {rateLimitInfo.Message}";

            // In multi-model mode, skip to next model instead of waiting
            if (_config.MultiModel?.IsEnabled == true)
            {
                OnOutput?.Invoke("");
                OnOutput?.Invoke($"[Rate Limit] {providerName}: {message}");
                OnOutput?.Invoke("[Rate Limit] Rotating to next model...");
                OnOutput?.Invoke("");
                _modelSelector.AfterIteration(0); // Rotate to next model
            }
            else
            {
                // Single model mode - wait for rate limit reset
                var resetAt = rateLimitInfo.ResetAt ?? DateTimeOffset.UtcNow.AddMinutes(30);
                _providerRateLimitUntil = resetAt;
                _providerRateLimitMessage = rateLimitInfo.Message;

                var localReset = resetAt.ToLocalTime();
                var resetText = rateLimitInfo.ResetAt.HasValue
                    ? $"{localReset:MMM d h:mm tt}"
                    : $"{localReset:MMM d h:mm tt} (fallback)";

                OnOutput?.Invoke($"{message}. Waiting until {resetText}.");
            }
        }
    }

    private int CountModifiedFiles()
    {
        try
        {
            var count = 0;

            // Count uncommitted changes (staged and unstaged)
            var statusPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "status --porcelain",
                WorkingDirectory = _config.TargetDirectory,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var statusProcess = System.Diagnostics.Process.Start(statusPsi))
            {
                if (statusProcess is not null)
                {
                    var output = statusProcess.StandardOutput.ReadToEnd();
                    statusProcess.WaitForExit();
                    count += output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                }
            }

            // Also check for recent commits (within last 2 minutes) as evidence of progress
            // This handles the case where AI commits its changes
            var logPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "log --oneline --since='2 minutes ago' --format='%h'",
                WorkingDirectory = _config.TargetDirectory,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var logProcess = System.Diagnostics.Process.Start(logPsi))
            {
                if (logProcess is not null)
                {
                    var logOutput = logProcess.StandardOutput.ReadToEnd();
                    logProcess.WaitForExit();
                    var recentCommits = logOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                    if (recentCommits > 0)
                    {
                        count += recentCommits;
                    }
                }
            }

            return count;
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

        // In multi-model mode, don't wait - just clear the limit and continue with next model
        if (_config.MultiModel?.IsEnabled == true)
        {
            OnOutput?.Invoke("[Rate Limit] Skipping wait in multi-model mode, continuing with next model...");
            _providerRateLimitUntil = null;
            _providerRateLimitMessage = null;
            return false;
        }

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
        _iterationSkipCts?.Cancel();
        _iterationSkipCts?.Dispose();
        _currentProcess?.Dispose();
        _ollamaClient?.Dispose();
        _copilotSdkClient?.Dispose();
        _pauseTcs?.TrySetCanceled();

        GC.SuppressFinalize(this);
    }
}
