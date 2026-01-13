namespace RalphController.Models;

/// <summary>
/// Manages model selection for multi-model configurations
/// Handles round-robin rotation and verification logic
/// </summary>
public class ModelSelector
{
    private readonly MultiModelConfig _config;
    private readonly AIProviderConfig _fallbackConfig;
    private int _currentIndex;
    private int _iterationsSinceRotation;
    private int _verificationAttempts;
    private bool _pendingVerification;
    private int _filesModifiedBeforeVerification;
    private ModelSpec? _injectedModel; // One-time model override for next iteration

    /// <summary>Current model index in the Models list</summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>Whether a verification pass is pending</summary>
    public bool PendingVerification => _pendingVerification;

    /// <summary>Number of verification attempts for current completion</summary>
    public int VerificationAttempts => _verificationAttempts;

    /// <summary>Whether currently running a verification iteration</summary>
    public bool IsVerificationIteration { get; private set; }

    /// <summary>Whether an injected model is pending</summary>
    public bool HasInjectedModel => _injectedModel != null;

    /// <summary>Files modified before verification started (for comparison)</summary>
    public int FilesModifiedBeforeVerification => _filesModifiedBeforeVerification;

    /// <summary>Fired when model switches</summary>
    public event Action<ModelSpec, string>? OnModelSwitch;

    /// <summary>Fired when verification starts</summary>
    public event Action<ModelSpec>? OnVerificationStart;

    /// <summary>Fired when verification completes</summary>
    public event Action<bool, int>? OnVerificationComplete; // (passed, filesChanged)

    public ModelSelector(MultiModelConfig? config, AIProviderConfig fallbackConfig)
    {
        _config = config ?? new MultiModelConfig();
        _fallbackConfig = fallbackConfig;
        _currentIndex = 0;
        _iterationsSinceRotation = 0;
        _verificationAttempts = 0;
        _pendingVerification = false;
    }

    /// <summary>
    /// Gets the current model spec to use for the next iteration
    /// </summary>
    public ModelSpec? GetCurrentModel()
    {
        // If an injected model is pending, use it (one-time override)
        if (_injectedModel != null)
        {
            var model = _injectedModel;
            _injectedModel = null; // Clear after use
            return model;
        }

        if (!_config.IsEnabled || _config.Models.Count == 0)
            return null;

        // For verification strategy, check if we should use verifier
        if (_config.Strategy == ModelSwitchStrategy.Verification && _pendingVerification)
        {
            var verifierIndex = _config.Verification?.VerifierIndex ?? 1;
            if (verifierIndex < _config.Models.Count)
            {
                IsVerificationIteration = true;
                return _config.Models[verifierIndex];
            }
        }

        IsVerificationIteration = false;
        return _config.Models[_currentIndex];
    }

    /// <summary>
    /// Inject a specific model to use for the next iteration (one-time override)
    /// </summary>
    public void InjectModel(ModelSpec model)
    {
        _injectedModel = model;
    }

    /// <summary>
    /// Gets the AIProviderConfig for the current model
    /// </summary>
    public AIProviderConfig GetCurrentProviderConfig()
    {
        var model = GetCurrentModel();
        return model?.ToProviderConfig() ?? _fallbackConfig;
    }

    /// <summary>
    /// Gets the current provider type
    /// </summary>
    public AIProvider GetCurrentProvider()
    {
        var model = GetCurrentModel();
        return model?.Provider ?? _fallbackConfig.Provider;
    }

    /// <summary>
    /// Called after each iteration completes to advance model if needed
    /// </summary>
    /// <param name="filesModified">Number of files modified in this iteration</param>
    public void AfterIteration(int filesModified)
    {
        if (!_config.IsEnabled)
            return;

        switch (_config.Strategy)
        {
            case ModelSwitchStrategy.RoundRobin:
                HandleRoundRobinAdvance();
                break;

            case ModelSwitchStrategy.Verification:
                HandleVerificationResult(filesModified);
                break;

            case ModelSwitchStrategy.Fallback:
                // Fallback handled separately in OnIterationFailed
                break;
        }
    }

    /// <summary>
    /// Called when completion is detected (for verification strategy)
    /// </summary>
    /// <param name="currentFilesModified">Current modified file count before verification</param>
    public void OnCompletionDetected(int currentFilesModified)
    {
        if (_config.Strategy != ModelSwitchStrategy.Verification)
            return;

        var maxAttempts = _config.Verification?.MaxVerificationAttempts ?? 3;
        if (_verificationAttempts >= maxAttempts)
        {
            // Max attempts reached, allow exit
            return;
        }

        _pendingVerification = true;
        _filesModifiedBeforeVerification = currentFilesModified;
        _verificationAttempts++;

        var verifierIndex = _config.Verification?.VerifierIndex ?? 1;
        if (verifierIndex < _config.Models.Count)
        {
            OnVerificationStart?.Invoke(_config.Models[verifierIndex]);
        }
    }

    /// <summary>
    /// Check if verification passed (no changes made by verifier)
    /// </summary>
    public bool CheckVerificationPassed(int filesModifiedAfter)
    {
        if (!IsVerificationIteration)
            return false;

        // Verification passes if no new files were modified
        var passed = filesModifiedAfter <= _filesModifiedBeforeVerification;
        var filesChanged = filesModifiedAfter - _filesModifiedBeforeVerification;

        OnVerificationComplete?.Invoke(passed, Math.Max(0, filesChanged));

        return passed;
    }

    /// <summary>
    /// Called when an iteration fails (for fallback strategy)
    /// </summary>
    public void OnIterationFailed(bool isRateLimit = false)
    {
        if (_config.Strategy != ModelSwitchStrategy.Fallback)
            return;

        if (_config.Models.Count <= 1)
            return;

        // Switch to next model as fallback
        var previousModel = GetCurrentModel();
        _currentIndex = (_currentIndex + 1) % _config.Models.Count;
        var newModel = GetCurrentModel();

        var reason = isRateLimit ? "rate limit" : "failure";
        OnModelSwitch?.Invoke(newModel!, $"Fallback due to {reason}");
    }

    /// <summary>
    /// Reset verification state (call when continuing after failed verification)
    /// </summary>
    public void ResetVerification()
    {
        _pendingVerification = false;
        IsVerificationIteration = false;
    }

    /// <summary>
    /// Fully reset (call when restarting or after successful exit)
    /// </summary>
    public void Reset()
    {
        _currentIndex = 0;
        _iterationsSinceRotation = 0;
        _verificationAttempts = 0;
        _pendingVerification = false;
        IsVerificationIteration = false;
    }

    /// <summary>
    /// Get statistics for display
    /// </summary>
    public ModelSelectorStats GetStats()
    {
        return new ModelSelectorStats
        {
            Strategy = _config.Strategy,
            CurrentModelIndex = _currentIndex,
            CurrentModelName = GetCurrentModel()?.DisplayName ?? "Default",
            TotalModels = _config.Models.Count,
            VerificationAttempts = _verificationAttempts,
            PendingVerification = _pendingVerification,
            IsVerificationIteration = IsVerificationIteration
        };
    }

    private void HandleRoundRobinAdvance()
    {
        _iterationsSinceRotation++;

        if (_iterationsSinceRotation >= _config.RotateEveryN)
        {
            _iterationsSinceRotation = 0;
            var previousModel = GetCurrentModel();
            _currentIndex = (_currentIndex + 1) % _config.Models.Count;
            var newModel = GetCurrentModel();

            if (previousModel != newModel)
            {
                OnModelSwitch?.Invoke(newModel!, "Round-robin rotation");
            }
        }
    }

    private void HandleVerificationResult(int filesModified)
    {
        if (!IsVerificationIteration)
            return;

        var passed = filesModified <= _filesModifiedBeforeVerification;
        var filesChanged = Math.Max(0, filesModified - _filesModifiedBeforeVerification);

        OnVerificationComplete?.Invoke(passed, filesChanged);

        if (passed)
        {
            // Verification passed - keep pending verification to signal should exit
            // The LoopController will check this and exit
        }
        else
        {
            // Verification failed (verifier made changes) - continue with primary
            _pendingVerification = false;
            IsVerificationIteration = false;
            // Don't reset _verificationAttempts - we track total attempts
        }
    }
}

/// <summary>
/// Statistics from the ModelSelector for display
/// </summary>
public class ModelSelectorStats
{
    public ModelSwitchStrategy Strategy { get; init; }
    public int CurrentModelIndex { get; init; }
    public string CurrentModelName { get; init; } = "";
    public int TotalModels { get; init; }
    public int VerificationAttempts { get; init; }
    public bool PendingVerification { get; init; }
    public bool IsVerificationIteration { get; init; }
}
