using RalphController.Models;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentTaskStatus = RalphController.Models.TaskStatus;

namespace RalphController.Parallel;

/// <summary>
/// Thread-safe shared task queue for parallel agents
/// </summary>
public class TaskQueue
{
    private readonly ConcurrentDictionary<string, AgentTask> _tasks = new();
    private readonly ConcurrentQueue<string> _pendingQueue = new();
    private readonly ConcurrentBag<string> _priorityQueue = new();
    private readonly object _lock = new();
    private readonly TimeSpan _claimTimeout;
    private readonly string? _persistPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Fired when a new task is added</summary>
    public event Action<AgentTask>? OnTaskEnqueued;

    /// <summary>Fired when a task is claimed</summary>
    public event Action<AgentTask>? OnTaskClaimed;

    /// <summary>Fired when a task is completed</summary>
    public event Action<AgentTask>? OnTaskCompleted;

    /// <summary>Fired when a task fails</summary>
    public event Action<AgentTask>? OnTaskFailed;

    public TaskQueue(TimeSpan? claimTimeout = null, string? persistPath = null)
    {
        _claimTimeout = claimTimeout ?? TimeSpan.FromMinutes(5);
        _persistPath = persistPath;

        if (_persistPath != null)
            TryLoadFromDisk();
    }

    /// <summary>
    /// Enqueue a new task
    /// </summary>
    public void Enqueue(AgentTask task)
    {
        _tasks[task.TaskId] = task;

        if (task.Priority == TaskPriority.Critical || task.Priority == TaskPriority.High)
        {
            _priorityQueue.Add(task.TaskId);
        }
        else
        {
            _pendingQueue.Enqueue(task.TaskId);
        }

        OnTaskEnqueued?.Invoke(task);
        SaveToDisk();
    }

    /// <summary>
    /// Enqueue multiple tasks
    /// </summary>
    public void EnqueueRange(IEnumerable<AgentTask> tasks)
    {
        foreach (var task in tasks)
        {
            Enqueue(task);
        }
    }

    /// <summary>
    /// Try to claim a task (with priority given to high-priority tasks)
    /// </summary>
    public AgentTask? TryClaim(string agentId)
    {
        // Release stale claims first
        ReleaseStaleClaims();

        // Try priority queue first
        while (_priorityQueue.TryTake(out var taskId))
        {
            if (_tasks.TryGetValue(taskId, out var task) && task.Status == AgentTaskStatus.Pending)
            {
                if (TryClaimTask(task, agentId))
                {
                    return task;
                }
            }
        }

        // Try regular queue
        while (_pendingQueue.TryDequeue(out var taskId))
        {
            if (_tasks.TryGetValue(taskId, out var task) && task.Status == AgentTaskStatus.Pending)
            {
                if (TryClaimTask(task, agentId))
                {
                    return task;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Claim a specific task by ID
    /// </summary>
    public bool TryClaimTask(string taskId, string agentId)
    {
        if (_tasks.TryGetValue(taskId, out var task) && task.Status == AgentTaskStatus.Pending)
        {
            return TryClaimTask(task, agentId);
        }
        return false;
    }

    private bool TryClaimTask(AgentTask task, string agentId)
    {
        lock (_lock)
        {
            if (task.Status != AgentTaskStatus.Pending)
                return false;

            task.Status = AgentTaskStatus.Claimed;
            task.ClaimedByAgentId = agentId;
            task.ClaimedAt = DateTime.UtcNow;
            OnTaskClaimed?.Invoke(task);
            SaveToDisk();
            return true;
        }
    }

    /// <summary>
    /// Mark a task as in progress
    /// </summary>
    public void StartTask(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.Status = AgentTaskStatus.InProgress;
        }
    }

    /// <summary>
    /// Complete a task with result
    /// </summary>
    public void Complete(string taskId, TaskResult result)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.Status = AgentTaskStatus.Completed;
            task.Result = result;
            task.CompletedAt = DateTime.UtcNow;
            OnTaskCompleted?.Invoke(task);
            SaveToDisk();
        }
    }

    /// <summary>
    /// Mark a task as failed (will be requeued if retries remain)
    /// </summary>
    public void Fail(string taskId, string error, int maxRetries)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.Error = error;
            task.RetryCount++;

            if (task.RetryCount < maxRetries)
            {
                // Requeue the task
                task.Status = AgentTaskStatus.Pending;
                task.ClaimedByAgentId = null;
                task.ClaimedAt = null;

                if (task.Priority == TaskPriority.Critical || task.Priority == TaskPriority.High)
                {
                    _priorityQueue.Add(task.TaskId);
                }
                else
                {
                    _pendingQueue.Enqueue(task.TaskId);
                }
            }
            else
            {
                task.Status = AgentTaskStatus.Failed;
                OnTaskFailed?.Invoke(task);
            }

            SaveToDisk();
        }
    }

    /// <summary>
    /// Release claims from tasks that have timed out
    /// </summary>
    public void ReleaseStaleClaims()
    {
        var now = DateTime.UtcNow;

        foreach (var task in _tasks.Values)
        {
            if (task.Status == AgentTaskStatus.Claimed &&
                task.ClaimedAt.HasValue &&
                (now - task.ClaimedAt.Value) > _claimTimeout)
            {
                task.Status = AgentTaskStatus.Pending;
                task.ClaimedByAgentId = null;
                task.ClaimedAt = null;

                // Re-add to appropriate queue
                if (task.Priority == TaskPriority.Critical || task.Priority == TaskPriority.High)
                {
                    _priorityQueue.Add(task.TaskId);
                }
                else
                {
                    _pendingQueue.Enqueue(task.TaskId);
                }
            }
        }
    }

    /// <summary>
    /// Get all tasks
    /// </summary>
    public IReadOnlyList<AgentTask> GetAllTasks() => _tasks.Values.ToList();

    /// <summary>
    /// Get tasks by status
    /// </summary>
    public List<AgentTask> GetTasksByStatus(AgentTaskStatus status) =>
        _tasks.Values.Where(t => t.Status == status).ToList();

    /// <summary>
    /// Get statistics
    /// </summary>
    public TaskQueueStatistics GetStatistics()
    {
        var tasks = _tasks.Values.ToList();
        return new TaskQueueStatistics
        {
            Total = tasks.Count,
            Pending = tasks.Count(t => t.Status == AgentTaskStatus.Pending),
            Claimed = tasks.Count(t => t.Status == AgentTaskStatus.Claimed),
            InProgress = tasks.Count(t => t.Status == AgentTaskStatus.InProgress),
            Completed = tasks.Count(t => t.Status == AgentTaskStatus.Completed),
            Failed = tasks.Count(t => t.Status == AgentTaskStatus.Failed)
        };
    }

    /// <summary>
    /// Check if all tasks are completed
    /// </summary>
    public bool IsComplete()
    {
        var stats = GetStatistics();
        return stats.Total > 0 && stats.Pending == 0 && stats.Claimed == 0 &&
               stats.InProgress == 0 && stats.Failed == 0;
    }

    /// <summary>
    /// Clear all tasks
    /// </summary>
    public void Clear()
    {
        _tasks.Clear();
        while (_pendingQueue.TryDequeue(out _)) { }
        while (_priorityQueue.TryTake(out _)) { }
        SaveToDisk();
    }

    /// <summary>
    /// Persist current task state to disk
    /// </summary>
    private void SaveToDisk()
    {
        if (_persistPath == null) return;

        try
        {
            var tasks = _tasks.Values.ToList();
            var json = JsonSerializer.Serialize(tasks, JsonOptions);
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_persistPath, json);
        }
        catch
        {
            // Don't crash the queue over persistence failures
        }
    }

    /// <summary>
    /// Restore task state from disk
    /// </summary>
    private void TryLoadFromDisk()
    {
        if (_persistPath == null || !File.Exists(_persistPath)) return;

        try
        {
            var json = File.ReadAllText(_persistPath);
            var tasks = JsonSerializer.Deserialize<List<AgentTask>>(json, JsonOptions);
            if (tasks == null) return;

            foreach (var task in tasks)
            {
                // Reset claimed/in-progress tasks back to pending (agent is gone after crash)
                if (task.Status == AgentTaskStatus.Claimed || task.Status == AgentTaskStatus.InProgress)
                {
                    task.Status = AgentTaskStatus.Pending;
                    task.ClaimedByAgentId = null;
                    task.ClaimedAt = null;
                }

                _tasks[task.TaskId] = task;

                // Re-enqueue pending tasks
                if (task.Status == AgentTaskStatus.Pending)
                {
                    if (task.Priority == TaskPriority.Critical || task.Priority == TaskPriority.High)
                        _priorityQueue.Add(task.TaskId);
                    else
                        _pendingQueue.Enqueue(task.TaskId);
                }
            }
        }
        catch
        {
            // Corrupted state file — start fresh
        }
    }

    /// <summary>
    /// Delete the persistence file (cleanup after successful completion)
    /// </summary>
    public void DeletePersistenceFile()
    {
        if (_persistPath != null && File.Exists(_persistPath))
        {
            try { File.Delete(_persistPath); }
            catch { /* ignore */ }
        }
    }
}

/// <summary>
/// Task queue statistics
/// </summary>
public class TaskQueueStatistics
{
    public int Total { get; init; }
    public int Pending { get; init; }
    public int Claimed { get; init; }
    public int InProgress { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }

    /// <summary>Percentage of tasks completed</summary>
    public double CompletionPercent => Total > 0 ? (double)Completed / Total * 100 : 0;
}
