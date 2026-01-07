namespace RalphController;

/// <summary>
/// Rate limiter for API calls with hourly reset
/// </summary>
public class RateLimiter
{
    private readonly object _lock = new();
    private int _callCount;
    private DateTime _windowStart;

    /// <summary>Maximum calls allowed per hour</summary>
    public int MaxCallsPerHour { get; }

    /// <summary>Current number of calls in this window</summary>
    public int CurrentCalls
    {
        get { lock (_lock) return _callCount; }
    }

    /// <summary>Remaining calls in this window</summary>
    public int RemainingCalls
    {
        get { lock (_lock) return Math.Max(0, MaxCallsPerHour - _callCount); }
    }

    /// <summary>Time until the rate limit window resets</summary>
    public TimeSpan TimeUntilReset
    {
        get
        {
            lock (_lock)
            {
                var elapsed = DateTime.UtcNow - _windowStart;
                var remaining = TimeSpan.FromHours(1) - elapsed;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Fired when rate limit is reached</summary>
    public event Action<TimeSpan>? OnRateLimitReached;

    public RateLimiter(int maxCallsPerHour)
    {
        MaxCallsPerHour = maxCallsPerHour;
        _windowStart = DateTime.UtcNow;
        _callCount = 0;
    }

    /// <summary>
    /// Try to acquire a call slot
    /// </summary>
    /// <returns>True if call is allowed, false if rate limited</returns>
    public bool TryAcquire()
    {
        lock (_lock)
        {
            CheckWindowReset();

            if (_callCount >= MaxCallsPerHour)
            {
                OnRateLimitReached?.Invoke(TimeUntilReset);
                return false;
            }

            _callCount++;
            return true;
        }
    }

    /// <summary>
    /// Wait until a call slot is available
    /// </summary>
    public async Task<bool> WaitForSlotAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (TryAcquire())
                return true;

            // Wait for a bit before trying again
            var waitTime = TimeUntilReset;
            if (waitTime > TimeSpan.Zero)
            {
                // Wait in 1-minute increments, checking for cancellation
                var sleepTime = TimeSpan.FromMinutes(Math.Min(1, waitTime.TotalMinutes));
                await Task.Delay(sleepTime, cancellationToken);
            }
        }

        return false;
    }

    /// <summary>
    /// Reset the rate limiter
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _callCount = 0;
            _windowStart = DateTime.UtcNow;
        }
    }

    private void CheckWindowReset()
    {
        var elapsed = DateTime.UtcNow - _windowStart;
        if (elapsed >= TimeSpan.FromHours(1))
        {
            _windowStart = DateTime.UtcNow;
            _callCount = 0;
        }
    }
}
