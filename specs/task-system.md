# Task System: Shared Task List with Dependencies

## Status
Draft

## Summary
Fix and extend the task system so tasks respect dependency ordering, use file-lock-based claiming, and support the full task lifecycle expected by agent teams.

## Reference
From Claude Code docs:
> The shared task list coordinates work across the team. Tasks have three states: pending, in progress, and completed. Tasks can also depend on other tasks: a pending task with unresolved dependencies cannot be claimed until those dependencies are completed.
> Task claiming uses file locking to prevent race conditions when multiple teammates try to claim the same task simultaneously.

## Current State

**What exists:**
- `Parallel/TaskQueue.cs` - Thread-safe queue with `ConcurrentDictionary`, priority queues, disk persistence
- `Models/AgentTask.cs` - Task model with `DependsOn: List<string>` field
- Tasks parsed from AI decomposition with `DEPENDS_ON:` field
- Claim mechanism via `Interlocked.CompareExchange` (in-process only)
- Stale claim release after configurable timeout
- Disk persistence to `.ralph-queue.json`

**What's broken:**
1. `DependsOn` is parsed during decomposition but **never checked** during `TryClaim()`
2. No file-locking for cross-process safety (all agents run in same process currently, but architecture should support external processes)
3. Task states don't match Claude Code's model (we have 5 states; they use 3)
4. No way to query "all tasks blocked on task X"
5. `TaskId` is a GUID; dependency references use task **titles** — mismatch

## Target State

### Task States
Simplify to match Claude Code's 3-state model:

```
Pending → InProgress → Completed
                    ↘ Failed (retry → Pending)
```

- **Pending**: Not yet claimed. May be blocked by dependencies.
- **InProgress**: Claimed by an agent, actively being worked on.
- **Completed**: Done. Unblocks dependent tasks automatically.
- **Failed**: Agent reported failure. Can be retried (returns to Pending) or left as terminal.

Remove `Claimed` as separate state — it's just early `InProgress`.

### Dependency Resolution

A task is **claimable** when:
1. Status == Pending
2. All tasks in `DependsOn` have Status == Completed
3. No other agent has already claimed it (atomic check)

```csharp
public bool IsClaimable(IReadOnlyDictionary<string, AgentTask> allTasks)
{
    if (Status != TaskStatus.Pending) return false;
    if (DependsOn == null || DependsOn.Count == 0) return true;

    return DependsOn.All(depId =>
        allTasks.TryGetValue(depId, out var dep) && dep.Status == TaskStatus.Completed);
}
```

### Dependency References

Change `DependsOn` from title-based to ID-based:
- During decomposition, assign stable IDs (e.g., `task-1`, `task-2`, ...)
- `DependsOn` stores task IDs, not titles
- Add a `Title` → `TaskId` lookup during decomposition

### File-Lock Claiming

```csharp
public AgentTask? TryClaim(string agentId)
{
    using var lockFile = AcquireFileLock("claims.lock", timeout: 5s);

    var task = FindNextClaimable();  // Respects dependencies + priority
    if (task == null) return null;

    task.Status = TaskStatus.InProgress;
    task.ClaimedByAgentId = agentId;
    task.ClaimedAt = DateTime.UtcNow;

    SaveToDisk();
    return task;
}
```

File lock implementation: use `FileStream` with `FileShare.None` on a lock file. Timeout after 5 seconds (another agent is claiming).

## Technical Requirements

1. **Enforce dependencies in `TryClaim()`** - Only return tasks whose `DependsOn` are all Completed.

2. **Change dependency references to task IDs** - Update decomposition parser to assign sequential IDs and resolve `DEPENDS_ON:` title references to IDs.

3. **Add file-lock mechanism** - Create `FileLock` helper class using `FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)`.

4. **Simplify task states** - Remove `Claimed` state. Merge into `InProgress`.

5. **Add `GetBlockedBy(taskId)` query** - Returns all tasks that are waiting on a given task. Used by orchestrator to understand impact.

6. **Add `GetClaimableTasks()` query** - Returns all tasks that are Pending with satisfied dependencies. Used by TUI to show available work.

7. **Auto-unblock on completion** - When a task completes, log which tasks are now unblocked. Emit event for orchestrator.

8. **Persist to `~/.ralph/teams/{team}/tasks/tasks.json`** - Move from `.ralph-queue.json` in project root to team-scoped location.

## Architecture

### Updated TaskQueue (rename to TaskStore)

```csharp
public class TaskStore : IDisposable
{
    private readonly string _storePath;        // ~/.ralph/teams/{team}/tasks/
    private readonly string _lockPath;         // tasks/claims.lock
    private Dictionary<string, AgentTask> _tasks;

    // Events
    public event Action<AgentTask>? TaskCompleted;
    public event Action<AgentTask>? TaskUnblocked;
    public event Action<AgentTask>? TaskFailed;

    // Queries
    public IReadOnlyList<AgentTask> GetAll();
    public IReadOnlyList<AgentTask> GetClaimable();
    public IReadOnlyList<AgentTask> GetBlockedBy(string taskId);
    public IReadOnlyList<AgentTask> GetInProgress();
    public AgentTask? GetById(string taskId);

    // Mutations (all acquire file lock)
    public AgentTask? TryClaim(string agentId);
    public void Complete(string taskId, TaskResult result);
    public void Fail(string taskId, string error);
    public void AddTasks(IEnumerable<AgentTask> tasks);

    // Persistence
    public void SaveToDisk();
    public static TaskStore LoadFromDisk(string storePath);
}
```

### Updated AgentTask

```csharp
public class AgentTask
{
    public string TaskId { get; set; }           // "task-1", "task-2", etc.
    public string Title { get; set; }            // Short display name
    public string Description { get; set; }      // Full description for agent prompt
    public TaskPriority Priority { get; set; }
    public TaskStatus Status { get; set; }       // Pending, InProgress, Completed, Failed
    public List<string> DependsOn { get; set; }  // Task IDs (not titles!)
    public List<string> Files { get; set; }      // Expected files to modify
    public string? ClaimedByAgentId { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public TaskResult? Result { get; set; }
    public string? Error { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; }          // Default: 2

    [JsonIgnore]
    public bool IsClaimable(IReadOnlyDictionary<string, AgentTask> allTasks) { ... }
}
```

## Acceptance Criteria

- [ ] Tasks with unresolved dependencies cannot be claimed
- [ ] Completing a task automatically makes dependent tasks claimable
- [ ] Two agents cannot claim the same task (file-lock enforced)
- [ ] Failed tasks are retried up to MaxRetries, then left as Failed
- [ ] Task state persists to disk after every mutation
- [ ] Task store can be loaded from disk on restart
- [ ] Decomposition produces tasks with stable IDs and ID-based dependencies
- [ ] `GetClaimable()` returns only tasks with all dependencies satisfied
- [ ] Stale claims (agent crashed) are released after timeout

## Files to Modify/Create

| Action | File |
|--------|------|
| Rename+Rewrite | `Parallel/TaskQueue.cs` → `Parallel/TaskStore.cs` |
| Create | `Parallel/FileLock.cs` |
| Modify | `Models/AgentTask.cs` (remove `Claimed` status, ensure ID-based deps) |
| Modify | `TeamController.cs` (update decomposition to produce ID-based deps) |
| Modify | `TeamAgent.cs` (use new `TaskStore.TryClaim()`) |

## Dependencies
None - this is the foundation spec.
