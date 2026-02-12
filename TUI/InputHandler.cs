namespace RalphController.TUI;

/// <summary>
/// Background keyboard input handler for the Teams TUI.
/// Reads keys via Console.ReadKey(intercept: true) in a dedicated thread
/// and dispatches them through <see cref="OnKeyPressed"/>.
/// </summary>
public sealed class InputHandler : IDisposable
{
    private Task? _readTask;
    private bool _disposed;

    /// <summary>
    /// Raised on the reader thread each time a key is pressed.
    /// Subscribers must be thread-safe.
    /// </summary>
    public event Action<ConsoleKeyInfo>? OnKeyPressed;

    /// <summary>
    /// Begin reading keyboard input. Non-blocking; spawns a background task.
    /// </summary>
    public void Start(CancellationToken ct)
    {
        if (_readTask != null)
            return;

        _readTask = Task.Run(() => ReadLoop(ct), ct);
    }

    private void ReadLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Console.KeyAvailable avoids blocking indefinitely so we can
                // respect the cancellation token without Thread.Interrupt.
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(50);
                    continue;
                }

                var key = Console.ReadKey(intercept: true);
                OnKeyPressed?.Invoke(key);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (InvalidOperationException)
        {
            // Console not available (redirected stdin)
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The read loop will exit when the CancellationToken fires;
        // we do not forcefully abort the background thread.
    }
}
