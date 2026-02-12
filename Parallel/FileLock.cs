namespace RalphController.Parallel;

/// <summary>
/// File-based lock using FileStream with FileShare.None for cross-process safety.
/// Implements IDisposable to release the lock when done.
/// </summary>
public sealed class FileLock : IDisposable
{
    private FileStream? _lockStream;
    private bool _disposed;

    private FileLock(FileStream lockStream)
    {
        _lockStream = lockStream;
    }

    /// <summary>
    /// Try to acquire a file lock with the specified timeout.
    /// Returns null if the lock cannot be acquired within the timeout.
    /// </summary>
    public static FileLock? TryAcquire(string lockFilePath, TimeSpan timeout)
    {
        var dir = Path.GetDirectoryName(lockFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var deadline = DateTime.UtcNow + timeout;
        var retryDelay = TimeSpan.FromMilliseconds(50);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var stream = new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);

                return new FileLock(stream);
            }
            catch (IOException)
            {
                // Lock held by another process — wait and retry
                Thread.Sleep(retryDelay);
                // Exponential backoff up to 500ms
                if (retryDelay < TimeSpan.FromMilliseconds(500))
                    retryDelay *= 2;
            }
        }

        return null;
    }

    /// <summary>
    /// Acquire a file lock, throwing TimeoutException if it cannot be acquired.
    /// </summary>
    public static FileLock Acquire(string lockFilePath, TimeSpan timeout)
    {
        var result = TryAcquire(lockFilePath, timeout);
        if (result == null)
            throw new TimeoutException($"Could not acquire file lock on '{lockFilePath}' within {timeout.TotalSeconds:F1}s");
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _lockStream?.Dispose();
            _lockStream = null;
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
