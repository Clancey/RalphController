using RalphController.Git;
using RalphController.Models;
using RalphController.Parallel;
using AgentTaskStatus = RalphController.Models.TaskStatus;

namespace RalphController.Merge;

/// <summary>
/// Manages the merge pipeline: queuing completed tasks, tracking file ownership,
/// performing dependency-ordered merges, and coordinating conflict resolution.
///
/// Replaces the sequential "merge all at the end" approach in TeamOrchestrator
/// with incremental merging as tasks complete.
/// </summary>
public class MergeManager : IDisposable
{
    private readonly GitWorktreeManager _worktrees;
    private readonly ConflictNegotiator _negotiator;
    private readonly TaskStore _taskStore;
    private readonly TeamConfig _teamConfig;
    private readonly Queue<string> _mergeQueue = new();
    private readonly Dictionary<string, string> _fileOwnership = new(); // file path -> agentId
    private readonly Dictionary<string, MergeStatus> _mergeStatuses = new();
    private readonly object _lock = new();
    private readonly string _lockFilePath;
    private readonly TimeSpan _lockTimeout = TimeSpan.FromSeconds(10);
    private bool _disposed;

    // Events for UI/orchestrator integration
    /// <summary>Fired when a task is queued for merge</summary>
    public event Action<string>? OnTaskQueued;

    /// <summary>Fired when a merge begins</summary>
    public event Action<string>? OnMergeStarted;

    /// <summary>Fired when a merge completes successfully</summary>
    public event Action<string, MergeResult>? OnMergeCompleted;

    /// <summary>Fired when a merge fails</summary>
    public event Action<string, string>? OnMergeFailed;

    /// <summary>Fired when file overlap is detected between agents</summary>
    public event Action<FileConflictWarning>? OnFileOverlapDetected;

    /// <summary>Fired when conflicts are detected during merge</summary>
    public event Action<string, List<GitConflict>>? OnConflictDetected;

    /// <summary>Fired when conflicts are resolved</summary>
    public event Action<string>? OnConflictResolved;

    public MergeManager(
        GitWorktreeManager worktrees,
        ConflictNegotiator negotiator,
        TaskStore taskStore,
        TeamConfig teamConfig,
        string? lockDirectory = null)
    {
        _worktrees = worktrees;
        _negotiator = negotiator;
        _taskStore = taskStore;
        _teamConfig = teamConfig;

        var lockDir = lockDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ralph", "teams", teamConfig.TeamName ?? "default");
        _lockFilePath = Path.Combine(lockDir, "merge.lock");
    }

    /// <summary>
    /// Detect file ownership overlaps across tasks. Returns warnings for any files
    /// that multiple agents intend to modify, which are likely merge conflict sources.
    /// Call this after task decomposition to surface early warnings.
    /// </summary>
    public List<FileConflictWarning> DetectFileOverlap(IEnumerable<AgentTask> tasks)
    {
        var warnings = new List<FileConflictWarning>();
        var fileToTasks = new Dictionary<string, List<AgentTask>>();

        foreach (var task in tasks)
        {
            foreach (var file in task.Files)
            {
                var normalized = NormalizePath(file);
                if (!fileToTasks.ContainsKey(normalized))
                {
                    fileToTasks[normalized] = new List<AgentTask>();
                }
                fileToTasks[normalized].Add(task);
            }
        }

        foreach (var (file, taskList) in fileToTasks)
        {
            if (taskList.Count <= 1) continue;

            var warning = new FileConflictWarning
            {
                FilePath = file,
                ConflictingTaskIds = taskList.Select(t => t.TaskId).ToList(),
                ConflictingAgentIds = taskList
                    .Where(t => t.ClaimedByAgentId != null)
                    .Select(t => t.ClaimedByAgentId!)
                    .Distinct()
                    .ToList(),
                Severity = DetermineOverlapSeverity(taskList)
            };

            warnings.Add(warning);
            OnFileOverlapDetected?.Invoke(warning);
        }

        return warnings;
    }

    /// <summary>
    /// Register file ownership for a task. Called when an agent completes work and
    /// we know which files it modified. Used for conflict prediction.
    /// </summary>
    public void RegisterFileOwnership(string agentId, IEnumerable<string> files)
    {
        lock (_lock)
        {
            foreach (var file in files)
            {
                var normalized = NormalizePath(file);
                if (_fileOwnership.TryGetValue(normalized, out var existingOwner) &&
                    existingOwner != agentId)
                {
                    // File was already modified by another agent -- overlap warning
                    OnFileOverlapDetected?.Invoke(new FileConflictWarning
                    {
                        FilePath = normalized,
                        ConflictingAgentIds = new List<string> { existingOwner, agentId },
                        Severity = ConflictSeverity.High
                    });
                }
                _fileOwnership[normalized] = agentId;
            }
        }
    }

    /// <summary>
    /// Get the current file ownership map (file path -> agent ID).
    /// </summary>
    public IReadOnlyDictionary<string, string> GetFileOwnership()
    {
        lock (_lock)
        {
            return new Dictionary<string, string>(_fileOwnership);
        }
    }

    /// <summary>
    /// Queue a completed task for merge. The task must be in Completed status.
    /// Merge will happen in dependency order when ProcessNextMerge is called.
    /// </summary>
    public void QueueForMerge(string taskId)
    {
        lock (_lock)
        {
            var task = _taskStore.GetById(taskId);
            if (task == null)
            {
                OnMergeFailed?.Invoke(taskId, $"Task {taskId} not found in store");
                return;
            }

            if (task.Status != AgentTaskStatus.Completed)
            {
                OnMergeFailed?.Invoke(taskId, $"Task {taskId} is not completed (status: {task.Status})");
                return;
            }

            if (_mergeStatuses.TryGetValue(taskId, out var existingStatus) &&
                existingStatus is MergeStatus.Queued or MergeStatus.Merging or MergeStatus.Merged)
            {
                return; // Already in pipeline
            }

            _mergeQueue.Enqueue(taskId);
            SetMergeStatus(taskId, MergeStatus.Queued);
            task.MergeStatus = MergeStatus.Queued;
            OnTaskQueued?.Invoke(taskId);
        }
    }

    /// <summary>
    /// Check if a task is ready to merge. A task is ready when:
    /// 1. It is completed
    /// 2. All its dependencies have been merged (not just completed)
    /// 3. It is in Queued status
    /// </summary>
    public bool IsReadyToMerge(string taskId)
    {
        lock (_lock)
        {
            if (!_mergeStatuses.TryGetValue(taskId, out var status) || status != MergeStatus.Queued)
                return false;

            var task = _taskStore.GetById(taskId);
            if (task == null) return false;

            // All dependencies must be merged (not just completed)
            return task.DependsOn.All(depId =>
                _mergeStatuses.TryGetValue(depId, out var depStatus) &&
                depStatus == MergeStatus.Merged);
        }
    }

    /// <summary>
    /// Process the next merge from the queue. Uses dependency-ordered selection:
    /// picks the first queued task whose dependencies are all merged.
    /// Returns null if no task is ready to merge.
    /// </summary>
    public async Task<MergeResult?> ProcessNextMerge(CancellationToken ct)
    {
        string? taskId;

        lock (_lock)
        {
            taskId = SelectNextMergeCandidate();
            if (taskId == null) return null;

            SetMergeStatus(taskId, MergeStatus.Merging);
        }

        var task = _taskStore.GetById(taskId);
        if (task == null)
        {
            SetMergeStatusSafe(taskId, MergeStatus.Failed);
            OnMergeFailed?.Invoke(taskId, "Task disappeared from store during merge");
            return new MergeResult { Success = false, Error = "Task not found" };
        }

        task.MergeStatus = MergeStatus.Merging;
        OnMergeStarted?.Invoke(taskId);

        // Acquire file lock for cross-process safety during merge
        using var fileLock = FileLock.TryAcquire(_lockFilePath, _lockTimeout);
        if (fileLock == null)
        {
            SetMergeStatusSafe(taskId, MergeStatus.Queued);
            task.MergeStatus = MergeStatus.Queued;
            return new MergeResult
            {
                Success = false,
                Error = "Could not acquire merge lock (another merge in progress)"
            };
        }

        try
        {
            var result = await ExecuteMerge(task, ct);

            if (result.Success)
            {
                SetMergeStatusSafe(taskId, MergeStatus.Merged);
                task.MergeStatus = MergeStatus.Merged;
                OnMergeCompleted?.Invoke(taskId, result);
            }
            else if (result.Conflicts?.Count > 0)
            {
                SetMergeStatusSafe(taskId, MergeStatus.ConflictDetected);
                task.MergeStatus = MergeStatus.ConflictDetected;
                OnConflictDetected?.Invoke(taskId, result.Conflicts);

                // Attempt AI-negotiated conflict resolution
                var resolved = await TryResolveConflicts(task, result, ct);
                if (resolved)
                {
                    SetMergeStatusSafe(taskId, MergeStatus.Merged);
                    task.MergeStatus = MergeStatus.Merged;
                    OnConflictResolved?.Invoke(taskId);
                }
                else
                {
                    SetMergeStatusSafe(taskId, MergeStatus.Failed);
                    task.MergeStatus = MergeStatus.Failed;
                    OnMergeFailed?.Invoke(taskId, "Conflict resolution failed");
                }
            }
            else
            {
                SetMergeStatusSafe(taskId, MergeStatus.Failed);
                task.MergeStatus = MergeStatus.Failed;
                OnMergeFailed?.Invoke(taskId, result.Error ?? "Unknown merge error");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            // Put the task back in queue on cancellation
            SetMergeStatusSafe(taskId, MergeStatus.Queued);
            task.MergeStatus = MergeStatus.Queued;
            throw;
        }
        catch (Exception ex)
        {
            SetMergeStatusSafe(taskId, MergeStatus.Failed);
            task.MergeStatus = MergeStatus.Failed;
            OnMergeFailed?.Invoke(taskId, ex.Message);
            return new MergeResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Process all queued merges in dependency order until the queue is empty
    /// or no more tasks are ready. Returns the count of successful merges.
    /// </summary>
    public async Task<int> ProcessAllMerges(CancellationToken ct)
    {
        var mergedCount = 0;

        while (!ct.IsCancellationRequested)
        {
            var result = await ProcessNextMerge(ct);
            if (result == null) break; // No more tasks ready to merge

            if (result.Success)
            {
                mergedCount++;
            }
        }

        return mergedCount;
    }

    /// <summary>
    /// Get the merge status for all tracked tasks.
    /// </summary>
    public IReadOnlyDictionary<string, MergeStatus> GetMergeStatuses()
    {
        lock (_lock)
        {
            return new Dictionary<string, MergeStatus>(_mergeStatuses);
        }
    }

    /// <summary>
    /// Get the merge status for a specific task.
    /// </summary>
    public MergeStatus GetMergeStatus(string taskId)
    {
        lock (_lock)
        {
            return _mergeStatuses.TryGetValue(taskId, out var status)
                ? status
                : MergeStatus.Pending;
        }
    }

    /// <summary>
    /// Get the count of tasks in each merge status.
    /// </summary>
    public MergeStatistics GetStatistics()
    {
        lock (_lock)
        {
            var statuses = _mergeStatuses.Values.ToList();
            return new MergeStatistics
            {
                Pending = statuses.Count(s => s == MergeStatus.Pending),
                Queued = statuses.Count(s => s == MergeStatus.Queued),
                Merging = statuses.Count(s => s == MergeStatus.Merging),
                Merged = statuses.Count(s => s == MergeStatus.Merged),
                ConflictDetected = statuses.Count(s => s == MergeStatus.ConflictDetected),
                Resolved = statuses.Count(s => s == MergeStatus.Resolved),
                Failed = statuses.Count(s => s == MergeStatus.Failed),
                QueueDepth = _mergeQueue.Count
            };
        }
    }

    /// <summary>
    /// Get the ordered list of task IDs that should be merged, respecting the
    /// dependency DAG. Uses Kahn's algorithm for topological sort.
    /// </summary>
    public List<string> GetTopologicalMergeOrder()
    {
        var tasks = _taskStore.GetAll()
            .Where(t => t.Status == AgentTaskStatus.Completed)
            .ToList();

        return TopologicalSort(tasks);
    }

    /// <summary>
    /// Perform topological sort of tasks using Kahn's algorithm.
    /// Returns task IDs in dependency order (dependencies first).
    /// Tasks with no dependencies come first; if there are cycles,
    /// the remaining tasks are appended at the end.
    /// </summary>
    internal static List<string> TopologicalSort(IReadOnlyList<AgentTask> tasks)
    {
        var taskMap = tasks.ToDictionary(t => t.TaskId);
        var result = new List<string>();

        // Build in-degree map (only count edges within the provided task set)
        var inDegree = new Dictionary<string, int>();
        var adjacency = new Dictionary<string, List<string>>(); // taskId -> dependents

        foreach (var task in tasks)
        {
            if (!inDegree.ContainsKey(task.TaskId))
                inDegree[task.TaskId] = 0;

            if (!adjacency.ContainsKey(task.TaskId))
                adjacency[task.TaskId] = new List<string>();

            foreach (var dep in task.DependsOn)
            {
                if (!taskMap.ContainsKey(dep)) continue; // Skip external/missing deps

                if (!adjacency.ContainsKey(dep))
                    adjacency[dep] = new List<string>();

                adjacency[dep].Add(task.TaskId);
                inDegree[task.TaskId] = inDegree.GetValueOrDefault(task.TaskId) + 1;
            }
        }

        // Kahn's: start with nodes that have no in-edges
        var queue = new Queue<string>(
            inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            result.Add(current);

            if (!adjacency.TryGetValue(current, out var dependents)) continue;

            foreach (var dependent in dependents)
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        // If there are cycles, append remaining tasks (best effort)
        foreach (var task in tasks)
        {
            if (!result.Contains(task.TaskId))
            {
                result.Add(task.TaskId);
            }
        }

        return result;
    }

    // --- Private helpers ---

    /// <summary>
    /// Select the next task to merge from the queue. Scans queued tasks
    /// and returns the first one whose dependencies are all merged.
    /// Must be called under _lock.
    /// </summary>
    private string? SelectNextMergeCandidate()
    {
        // Get dependency-ordered list of completed tasks
        var mergeOrder = GetTopologicalMergeOrder();
        var queuedSet = new HashSet<string>(
            _mergeStatuses
                .Where(kv => kv.Value == MergeStatus.Queued)
                .Select(kv => kv.Key));

        // Find the first task in topological order that is queued and ready
        foreach (var taskId in mergeOrder)
        {
            if (!queuedSet.Contains(taskId)) continue;

            var task = _taskStore.GetById(taskId);
            if (task == null) continue;

            // Check all dependencies are merged
            var depsReady = task.DependsOn.Count == 0 ||
                task.DependsOn.All(depId =>
                    _mergeStatuses.TryGetValue(depId, out var depStatus) &&
                    depStatus == MergeStatus.Merged);

            if (depsReady)
            {
                // Remove from queue (rebuild without this task)
                RebuildQueueWithout(taskId);
                return taskId;
            }
        }

        return null;
    }

    /// <summary>
    /// Rebuild the merge queue without the specified task ID.
    /// Must be called under _lock.
    /// </summary>
    private void RebuildQueueWithout(string taskIdToRemove)
    {
        var remaining = new Queue<string>();
        while (_mergeQueue.Count > 0)
        {
            var id = _mergeQueue.Dequeue();
            if (id != taskIdToRemove)
            {
                remaining.Enqueue(id);
            }
        }
        while (remaining.Count > 0)
        {
            _mergeQueue.Enqueue(remaining.Dequeue());
        }
    }

    /// <summary>
    /// Execute the actual git merge for a task using GitWorktreeManager.
    /// Delegates to the appropriate merge strategy.
    /// </summary>
    private async Task<MergeResult> ExecuteMerge(AgentTask task, CancellationToken ct)
    {
        if (!_teamConfig.UseWorktrees)
        {
            return new MergeResult { Success = true };
        }

        var agentId = task.ClaimedByAgentId;
        if (string.IsNullOrEmpty(agentId))
        {
            return new MergeResult
            {
                Success = false,
                Error = $"Task {task.TaskId} has no assigned agent"
            };
        }

        // Derive worktree path and branch name from the agent ID convention
        var worktreePath = Path.Combine(
            _teamConfig.UseWorktrees ? Path.Combine(
                Directory.GetCurrentDirectory(), ".ralph-worktrees", $"team-{agentId}") : Directory.GetCurrentDirectory());
        var branchName = $"team-agent-{agentId}"; // Prefix match; exact branch determined at spawn

        var targetBranch = _teamConfig.TargetBranch;
        if (string.IsNullOrEmpty(targetBranch))
        {
            targetBranch = await _worktrees.GetCurrentBranchAsync(ct);
        }

        // Register files modified by this task for ownership tracking
        if (task.Result?.FilesModified != null)
        {
            RegisterFileOwnership(agentId, task.Result.FilesModified);
        }

        return _teamConfig.MergeStrategy switch
        {
            MergeStrategy.RebaseThenMerge => await _worktrees.RebaseAndMergeAsync(
                worktreePath, branchName, targetBranch, ct),
            MergeStrategy.MergeDirect => await _worktrees.MergeDirectAsync(
                worktreePath, branchName, targetBranch, ct),
            _ => await _worktrees.SequentialMergeAsync(
                worktreePath, branchName, targetBranch, ct)
        };
    }

    /// <summary>
    /// Attempt AI-negotiated conflict resolution for a failed merge.
    /// Uses the ConflictNegotiator with enhanced prompts that include task context.
    /// </summary>
    private async Task<bool> TryResolveConflicts(
        AgentTask task,
        MergeResult mergeResult,
        CancellationToken ct)
    {
        if (_teamConfig.ConflictResolution == ConflictResolutionMode.Manual)
            return false;

        if (mergeResult.Conflicts == null || mergeResult.Conflicts.Count == 0)
            return false;

        var agentId = task.ClaimedByAgentId ?? "unknown";
        var targetBranch = _teamConfig.TargetBranch;
        if (string.IsNullOrEmpty(targetBranch))
        {
            targetBranch = await _worktrees.GetCurrentBranchAsync(ct);
        }

        // Find the "other" agent -- the one whose merged work is on the target branch.
        // This is the agent whose changes were most recently merged.
        var lastMergedAgent = GetLastMergedAgent(task.TaskId);

        SetMergeStatusSafe(task.TaskId, MergeStatus.ConflictDetected);

        var resolution = await _negotiator.NegotiateResolutionAsync(
            mergeResult.Conflicts,
            agentId,
            $"team-agent-{agentId}",
            lastMergedAgent ?? "target",
            targetBranch,
            ct);

        if (!resolution.Success || resolution.RequiresManualIntervention)
        {
            return false;
        }

        // Apply the resolution
        var worktreePath = _teamConfig.UseWorktrees
            ? Path.Combine(Directory.GetCurrentDirectory(), ".ralph-worktrees", $"team-{agentId}")
            : Directory.GetCurrentDirectory();

        var applied = await _negotiator.ApplyResolutionAsync(resolution, worktreePath, ct);
        if (applied)
        {
            SetMergeStatusSafe(task.TaskId, MergeStatus.Resolved);
            task.MergeStatus = MergeStatus.Resolved;
        }

        return applied;
    }

    /// <summary>
    /// Find the agent whose work was most recently merged (for conflict context).
    /// </summary>
    private string? GetLastMergedAgent(string excludeTaskId)
    {
        lock (_lock)
        {
            var mergedTasks = _mergeStatuses
                .Where(kv => kv.Value == MergeStatus.Merged && kv.Key != excludeTaskId)
                .Select(kv => _taskStore.GetById(kv.Key))
                .Where(t => t != null)
                .OrderByDescending(t => t!.CompletedAt)
                .FirstOrDefault();

            return mergedTasks?.ClaimedByAgentId;
        }
    }

    /// <summary>
    /// Determine the severity of a file overlap based on the tasks involved.
    /// </summary>
    private static ConflictSeverity DetermineOverlapSeverity(List<AgentTask> overlappingTasks)
    {
        // High severity: tasks that have no dependency relationship (truly parallel)
        var taskIds = overlappingTasks.Select(t => t.TaskId).ToHashSet();
        var hasNoDependencyRelation = overlappingTasks.All(t =>
            !t.DependsOn.Any(dep => taskIds.Contains(dep)));

        if (hasNoDependencyRelation && overlappingTasks.Count > 2)
            return ConflictSeverity.Critical;

        if (hasNoDependencyRelation)
            return ConflictSeverity.High;

        // Medium: tasks have a dependency chain (sequential, less likely to conflict)
        return ConflictSeverity.Medium;
    }

    private void SetMergeStatus(string taskId, MergeStatus status)
    {
        // Must be called under _lock
        _mergeStatuses[taskId] = status;
    }

    private void SetMergeStatusSafe(string taskId, MergeStatus status)
    {
        lock (_lock)
        {
            _mergeStatuses[taskId] = status;
        }
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Warning about files that multiple agents intend to modify.
/// </summary>
public class FileConflictWarning
{
    /// <summary>Path of the contested file</summary>
    public required string FilePath { get; init; }

    /// <summary>Task IDs that target this file</summary>
    public List<string> ConflictingTaskIds { get; init; } = new();

    /// <summary>Agent IDs that target this file</summary>
    public List<string> ConflictingAgentIds { get; init; } = new();

    /// <summary>Estimated severity of the conflict</summary>
    public ConflictSeverity Severity { get; init; } = ConflictSeverity.Medium;

    public override string ToString() =>
        $"[{Severity}] {FilePath} modified by {ConflictingTaskIds.Count} tasks: {string.Join(", ", ConflictingTaskIds)}";
}

/// <summary>
/// Severity of a predicted file conflict.
/// </summary>
public enum ConflictSeverity
{
    /// <summary>Low risk: tasks have dependency chain, sequential access</summary>
    Low,

    /// <summary>Medium risk: tasks share files but have some dependency relation</summary>
    Medium,

    /// <summary>High risk: independent parallel tasks modifying the same file</summary>
    High,

    /// <summary>Critical: 3+ independent tasks modifying the same file</summary>
    Critical
}

/// <summary>
/// Aggregate statistics for the merge pipeline.
/// </summary>
public class MergeStatistics
{
    public int Pending { get; init; }
    public int Queued { get; init; }
    public int Merging { get; init; }
    public int Merged { get; init; }
    public int ConflictDetected { get; init; }
    public int Resolved { get; init; }
    public int Failed { get; init; }
    public int QueueDepth { get; init; }

    /// <summary>Total tasks tracked in the merge pipeline</summary>
    public int Total => Pending + Queued + Merging + Merged + ConflictDetected + Resolved + Failed;

    /// <summary>Merge success rate</summary>
    public double SuccessRate => (Merged + Resolved) > 0 && Total > 0
        ? (double)(Merged + Resolved) / Total
        : 0;
}
