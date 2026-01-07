namespace RalphController;

/// <summary>
/// Circuit breaker pattern implementation to detect execution stagnation.
/// Prevents runaway loops by detecting when the AI is stuck or making no progress.
/// Based on Michael Nygard's "Release It!" pattern.
/// </summary>
public class CircuitBreaker
{
    private readonly object _lock = new();
    private CircuitState _state = CircuitState.Closed;
    private int _noProgressCount;
    private int _sameErrorCount;
    private string? _lastError;
    private int _lastFileCount;
    private DateTime _lastStateChange = DateTime.UtcNow;

    /// <summary>Threshold for consecutive loops without file changes</summary>
    public int NoProgressThreshold { get; set; } = 3;

    /// <summary>Threshold for consecutive loops with same error</summary>
    public int SameErrorThreshold { get; set; } = 5;

    /// <summary>Current circuit state</summary>
    public CircuitState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>Reason the circuit opened (if open)</summary>
    public string? OpenReason { get; private set; }

    /// <summary>Fired when circuit state changes</summary>
    public event Action<CircuitState, string?>? OnStateChanged;

    /// <summary>
    /// Record the result of a loop iteration
    /// </summary>
    /// <param name="result">The AI execution result</param>
    /// <param name="filesModified">Number of files modified in this iteration</param>
    public void RecordResult(AIResult result, int filesModified)
    {
        lock (_lock)
        {
            // Check for progress (file modifications)
            if (filesModified > 0 || filesModified != _lastFileCount)
            {
                _noProgressCount = 0;
                _lastFileCount = filesModified;
            }
            else
            {
                _noProgressCount++;
            }

            // Check for repeated errors
            if (!result.Success && !string.IsNullOrEmpty(result.Error))
            {
                var errorKey = ExtractErrorKey(result.Error);
                if (errorKey == _lastError)
                {
                    _sameErrorCount++;
                }
                else
                {
                    _sameErrorCount = 1;
                    _lastError = errorKey;
                }
            }
            else
            {
                _sameErrorCount = 0;
                _lastError = null;
            }

            // Evaluate circuit state
            EvaluateState();
        }
    }

    /// <summary>
    /// Check if execution can proceed
    /// </summary>
    public bool CanExecute()
    {
        lock (_lock)
        {
            return _state != CircuitState.Open;
        }
    }

    /// <summary>
    /// Manually reset the circuit breaker to closed state
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _state = CircuitState.Closed;
            _noProgressCount = 0;
            _sameErrorCount = 0;
            _lastError = null;
            OpenReason = null;
            _lastStateChange = DateTime.UtcNow;
            OnStateChanged?.Invoke(CircuitState.Closed, null);
        }
    }

    /// <summary>
    /// Attempt to transition from HalfOpen to Closed after successful execution
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_state == CircuitState.HalfOpen)
            {
                SetState(CircuitState.Closed, null);
            }
            _noProgressCount = 0;
            _sameErrorCount = 0;
        }
    }

    private void EvaluateState()
    {
        // Check for no progress threshold
        if (_noProgressCount >= NoProgressThreshold)
        {
            SetState(CircuitState.Open, $"No progress detected for {_noProgressCount} consecutive loops");
            return;
        }

        // Check for repeated error threshold
        if (_sameErrorCount >= SameErrorThreshold)
        {
            SetState(CircuitState.Open, $"Same error repeated {_sameErrorCount} times: {_lastError}");
            return;
        }

        // If we're open and conditions improved, go to half-open
        if (_state == CircuitState.Open)
        {
            var timeSinceOpen = DateTime.UtcNow - _lastStateChange;
            if (timeSinceOpen > TimeSpan.FromMinutes(1))
            {
                SetState(CircuitState.HalfOpen, "Attempting recovery");
            }
        }
    }

    private void SetState(CircuitState newState, string? reason)
    {
        if (_state != newState)
        {
            _state = newState;
            OpenReason = reason;
            _lastStateChange = DateTime.UtcNow;
            OnStateChanged?.Invoke(newState, reason);
        }
    }

    private static string ExtractErrorKey(string error)
    {
        // Extract first line or first 100 chars as error key
        var firstLine = error.Split('\n')[0];
        return firstLine.Length > 100 ? firstLine[..100] : firstLine;
    }
}

/// <summary>
/// Circuit breaker states
/// </summary>
public enum CircuitState
{
    /// <summary>Normal operation - execution allowed</summary>
    Closed,

    /// <summary>Monitoring mode - checking for recovery</summary>
    HalfOpen,

    /// <summary>Failure detected - execution halted</summary>
    Open
}
