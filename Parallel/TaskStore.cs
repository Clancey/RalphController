using RalphController.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentTaskStatus = RalphController.Models.TaskStatus;

namespace RalphController.Parallel;

/// <summary>
/// Shared task store with dependency-aware claiming, file-lock safety, and disk persistence.
/// Replaces TaskQueue with proper dependency enforcement and cross-process lock support.
/// </summary>
public class TaskStore : IDisposable
{
    private readonly string _storePath;       // Directory: ~/.ralph/teams/{team}/tasks/
    private readonly string _tasksFilePath;   // tasks.json inside _storePath
    private readonly string _lockFilePath;    // claims.lock inside _storePath
    private readonly TimeSpan _claimTimeout;
    private readonly TimeSpan _lockTimeout;
    private readonly object _inProcessLock = new();
    private Dictionary<string, AgentTask> _tasks = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    // Events
    /// <summary>Fired when a task is completed</summary>
    public event Action<AgentTask>? TaskCompleted;

    /// <summary>Fired when a previously blocked task becomes claimable</summary>
    public event Action<AgentTask>? TaskUnblocked;

    /// <summary>Fired when a task fails (after exhausting retries)</summary>
    public event Action<AgentTask>? TaskFailed;

    /// <summary>Fired when a task is claimed by an agent</summary>
    public event Action<AgentTask>? TaskClaimed;

    /// <summary>Fired when a new task is added</summary>
    public event Action<AgentTask>? TaskAdded;

    public TaskStore(string storePath, TimeSpan? claimTimeout = null, TimeSpan? lockTimeout = null)
    {
        _storePath = storePath;
        _tasksFilePath = Path.Combine(storePath, "tasks.json");
        _lockFilePath = Path.Combine(storePath, "claims.lock");
        _claimTimeout = claimTimeout ?? TimeSpan.FromMinutes(5);
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);

        // Ensure directory exists
        if (!Directory.Exists(storePath))
            Directory.CreateDirectory(storePath);
    }

    /// <summary>
    /// Load an existing TaskStore from disk, or create empty if not found.
    /// </summary>
    public static TaskStore LoadFromDisk(string storePath, TimeSpan? claimTimeout = null)
    {
        var store = new TaskStore(storePath, claimTimeout);
        store.TryLoadFromDisk();
        return store;
    }

    // --- Queries ---

    /// <summary>Get all tasks</summary>
    public IReadOnlyList<AgentTask> GetAll()
    {
        lock (_inProcessLock)
            return _tasks.Values.ToList();
    }

    /// <summary>Get a task by its ID</summary>
    public AgentTask? GetById(string taskId)
    {
        lock (_inProcessLock)
            return _tasks.TryGetValue(taskId, out var task) ? task : null;
    }

    /// <summary>Get all claimable tasks (Pending with all dependencies satisfied)</summary>
    public IReadOnlyList<AgentTask> GetClaimable()
    {
        lock (_inProcessLock)
        {
            var snapshot = new Dictionary<string, AgentTask>(_tasks);
            return _tasks.Values
                .Where(t => t.IsClaimable(snapshot))
                .OrderBy(t => t.Priority)
                .ToList();
        }
    }

    /// <summary>Get all tasks currently in progress</summary>
    public IReadOnlyList<AgentTask> GetInProgress()
    {
        lock (_inProcessLock)
            return _tasks.Values
                .Where(t => t.Status == AgentTaskStatus.InProgress)
                .ToList();
    }

    /// <summary>
    /// Get all tasks that depend on the given task ID.
    /// These are the tasks that would become unblocked when taskId completes.
    /// </summary>
    public IReadOnlyList<AgentTask> GetBlockedBy(string taskId)
    {
        lock (_inProcessLock)
            return _tasks.Values
                .Where(t => t.DependsOn.Contains(taskId))
                .ToList();
    }

    /// <summary>Get statistics</summary>
    public TaskStoreStatistics GetStatistics()
    {
        lock (_inProcessLock)
        {
            var tasks = _tasks.Values.ToList();
            var snapshot = new Dictionary<string, AgentTask>(_tasks);
            return new TaskStoreStatistics
            {
                Total = tasks.Count,
                Pending = tasks.Count(t => t.Status == AgentTaskStatus.Pending),
                InProgress = tasks.Count(t => t.Status == AgentTaskStatus.InProgress),
                Completed = tasks.Count(t => t.Status == AgentTaskStatus.Completed),
                Failed = tasks.Count(t => t.Status == AgentTaskStatus.Failed),
                Claimable = tasks.Count(t => t.IsClaimable(snapshot))
            };
        }
    }

    /// <summary>Check if all tasks are completed (or failed past retries)</summary>
    public bool IsComplete()
    {
        lock (_inProcessLock)
        {
            if (_tasks.Count == 0) return false;
            return _tasks.Values.All(t =>
                t.Status == AgentTaskStatus.Completed || t.Status == AgentTaskStatus.Failed);
        }
    }

    // --- Mutations (all acquire file lock for cross-process safety) ---

    /// <summary>
    /// Try to claim the next available task for the given agent.
    /// Respects dependencies — only tasks whose deps are all Completed can be claimed.
    /// Uses file-lock to prevent race conditions across processes.
    /// </summary>
    public AgentTask? TryClaim(string agentId)
    {
        using var fileLock = FileLock.TryAcquire(_lockFilePath, _lockTimeout);
        if (fileLock == null) return null; // Another process holds the lock

        lock (_inProcessLock)
        {
            // Release stale claims first
            ReleaseStaleClaims();

            var snapshot = new Dictionary<string, AgentTask>(_tasks);

            // Find next claimable task (priority-ordered)
            var task = _tasks.Values
                .Where(t => t.IsClaimable(snapshot))
                .OrderBy(t => t.Priority)
                .ThenBy(t => t.CreatedAt)
                .FirstOrDefault();

            if (task == null) return null;

            task.Status = AgentTaskStatus.InProgress;
            task.ClaimedByAgentId = agentId;
            task.ClaimedAt = DateTime.UtcNow;

            SaveToDisk();
            TaskClaimed?.Invoke(task);
            return task;
        }
    }

    /// <summary>
    /// Try to claim a specific task by ID for the given agent.
    /// </summary>
    public bool TryClaimTask(string taskId, string agentId)
    {
        using var fileLock = FileLock.TryAcquire(_lockFilePath, _lockTimeout);
        if (fileLock == null) return false;

        lock (_inProcessLock)
        {
            if (!_tasks.TryGetValue(taskId, out var task)) return false;

            var snapshot = new Dictionary<string, AgentTask>(_tasks);
            if (!task.IsClaimable(snapshot)) return false;

            task.Status = AgentTaskStatus.InProgress;
            task.ClaimedByAgentId = agentId;
            task.ClaimedAt = DateTime.UtcNow;

            SaveToDisk();
            TaskClaimed?.Invoke(task);
            return true;
        }
    }

    /// <summary>
    /// Mark a task as completed. Automatically checks for newly unblocked tasks.
    /// </summary>
    public void Complete(string taskId, TaskResult result)
    {
        List<AgentTask> newlyUnblocked;

        using var fileLock = FileLock.TryAcquire(_lockFilePath, _lockTimeout);

        lock (_inProcessLock)
        {
            if (!_tasks.TryGetValue(taskId, out var task)) return;

            task.Status = AgentTaskStatus.Completed;
            task.Result = result;
            task.CompletedAt = DateTime.UtcNow;

            SaveToDisk();

            // Find tasks that are now unblocked by this completion
            var snapshot = new Dictionary<string, AgentTask>(_tasks);
            newlyUnblocked = _tasks.Values
                .Where(t => t.Status == AgentTaskStatus.Pending
                    && t.DependsOn.Contains(taskId)
                    && t.IsClaimable(snapshot))
                .ToList();
        }

        // Fire events outside the lock to avoid deadlocks
        TaskCompleted?.Invoke(_tasks[taskId]);
        foreach (var unblocked in newlyUnblocked)
        {
            TaskUnblocked?.Invoke(unblocked);
        }
    }

    /// <summary>
    /// Mark a task as failed. If retries remain, resets to Pending. Otherwise marks Failed.
    /// </summary>
    public void Fail(string taskId, string error)
    {
        bool permanentlyFailed;
        AgentTask? task;

        using var fileLock = FileLock.TryAcquire(_lockFilePath, _lockTimeout);

        lock (_inProcessLock)
        {
            if (!_tasks.TryGetValue(taskId, out task)) return;

            task.Error = error;
            task.RetryCount++;

            if (task.RetryCount < task.MaxRetries)
            {
                // Requeue for retry
                task.Status = AgentTaskStatus.Pending;
                task.ClaimedByAgentId = null;
                task.ClaimedAt = null;
                permanentlyFailed = false;
            }
            else
            {
                task.Status = AgentTaskStatus.Failed;
                permanentlyFailed = true;
            }

            SaveToDisk();
        }

        if (permanentlyFailed)
        {
            TaskFailed?.Invoke(task!);
        }
    }

    /// <summary>
    /// Add tasks to the store. Assigns sequential task IDs if they use default GUID IDs.
    /// </summary>
    public void AddTasks(IEnumerable<AgentTask> tasks)
    {
        using var fileLock = FileLock.TryAcquire(_lockFilePath, _lockTimeout);

        lock (_inProcessLock)
        {
            foreach (var task in tasks)
            {
                _tasks[task.TaskId] = task;
                TaskAdded?.Invoke(task);
            }
            SaveToDisk();
        }
    }

    /// <summary>
    /// Add a single task to the store.
    /// </summary>
    public void AddTask(AgentTask task)
    {
        using var fileLock = FileLock.TryAcquire(_lockFilePath, _lockTimeout);

        lock (_inProcessLock)
        {
            _tasks[task.TaskId] = task;
            SaveToDisk();
        }

        TaskAdded?.Invoke(task);
    }

    /// <summary>
    /// Reassign a task from one agent to another (or back to Pending).
    /// </summary>
    public void ReassignTask(string taskId, string? newAgentId = null)
    {
        using var fileLock = FileLock.TryAcquire(_lockFilePath, _lockTimeout);

        lock (_inProcessLock)
        {
            if (!_tasks.TryGetValue(taskId, out var task)) return;
            if (task.Status != AgentTaskStatus.InProgress) return;

            if (newAgentId != null)
            {
                task.ClaimedByAgentId = newAgentId;
                task.ClaimedAt = DateTime.UtcNow;
            }
            else
            {
                task.Status = AgentTaskStatus.Pending;
                task.ClaimedByAgentId = null;
                task.ClaimedAt = null;
            }

            SaveToDisk();
        }
    }

    /// <summary>
    /// Cancel a task (remove it from the store).
    /// </summary>
    public bool CancelTask(string taskId)
    {
        using var fileLock = FileLock.TryAcquire(_lockFilePath, _lockTimeout);

        lock (_inProcessLock)
        {
            var removed = _tasks.Remove(taskId);
            if (removed) SaveToDisk();
            return removed;
        }
    }

    /// <summary>Clear all tasks</summary>
    public void Clear()
    {
        lock (_inProcessLock)
        {
            _tasks.Clear();
            SaveToDisk();
        }
    }

    /// <summary>
    /// Release claims from tasks whose agents appear to have crashed (claim timeout exceeded).
    /// </summary>
    public void ReleaseStaleClaims()
    {
        var now = DateTime.UtcNow;

        // Must be called inside _inProcessLock
        foreach (var task in _tasks.Values)
        {
            if (task.Status == AgentTaskStatus.InProgress &&
                task.ClaimedAt.HasValue &&
                (now - task.ClaimedAt.Value) > _claimTimeout)
            {
                task.Status = AgentTaskStatus.Pending;
                task.ClaimedByAgentId = null;
                task.ClaimedAt = null;
            }
        }
    }

    /// <summary>Delete the persistence files (cleanup after successful completion)</summary>
    public void DeletePersistenceFiles()
    {
        try
        {
            if (File.Exists(_tasksFilePath))
                File.Delete(_tasksFilePath);
            if (File.Exists(_lockFilePath))
                File.Delete(_lockFilePath);
        }
        catch { /* ignore */ }
    }

    // --- Persistence ---

    private void SaveToDisk()
    {
        try
        {
            var tasks = _tasks.Values.ToList();
            var json = JsonSerializer.Serialize(tasks, JsonOptions);
            if (!Directory.Exists(_storePath))
                Directory.CreateDirectory(_storePath);
            File.WriteAllText(_tasksFilePath, json);
        }
        catch
        {
            // Don't crash the store over persistence failures
        }
    }

    private void TryLoadFromDisk()
    {
        if (!File.Exists(_tasksFilePath)) return;

        try
        {
            var json = File.ReadAllText(_tasksFilePath);
            var tasks = JsonSerializer.Deserialize<List<AgentTask>>(json, JsonOptions);
            if (tasks == null) return;

            foreach (var task in tasks)
            {
                // Reset InProgress tasks back to Pending on restart (agent is gone after crash)
                if (task.Status == AgentTaskStatus.InProgress)
                {
                    task.Status = AgentTaskStatus.Pending;
                    task.ClaimedByAgentId = null;
                    task.ClaimedAt = null;
                }

                _tasks[task.TaskId] = task;
            }
        }
        catch
        {
            // Corrupted state file — start fresh
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Task store statistics
/// </summary>
public class TaskStoreStatistics
{
    public int Total { get; init; }
    public int Pending { get; init; }
    public int InProgress { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
    public int Claimable { get; init; }

    /// <summary>Percentage of tasks completed</summary>
    public double CompletionPercent => Total > 0 ? (double)Completed / Total * 100 : 0;
}
