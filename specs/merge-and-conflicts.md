# Merge and Conflict Resolution

## Status
Draft

## Summary
Handle merging parallel agent work back to the target branch. This includes merge strategies, proactive conflict avoidance through file ownership, AI-driven conflict resolution, and proper ordering of merges.

## Reference
From Claude Code docs:
> Two teammates editing the same file leads to overwrites. Break the work so each teammate owns a different set of files.

The Claude Code approach avoids conflicts at the task design level rather than resolving them after the fact. Ralph should adopt this proactive approach while keeping the existing reactive conflict resolution as a fallback.

## Current State

**What exists:**
- `Git/GitWorktreeManager.cs` - Creates worktrees, detects conflicts, merges with 3 strategies
- `Parallel/ConflictNegotiator.cs` - AI-based resolution of merge conflicts
- 3 merge strategies: `RebaseThenMerge`, `MergeDirect`, `Sequential`
- Conflict detection via `git diff --name-only --diff-filter=U`
- AI resolution produces `---RESOLUTION---` blocks with resolved file content
- Auto-commits resolved files

**What works well:**
- Worktree isolation prevents runtime conflicts
- AI negotiation produces reasonable resolutions
- Multiple strategies give flexibility

**What's broken/missing:**
1. No proactive conflict avoidance — files not reserved during task assignment
2. Merge happens only after ALL agents finish (sequential phases)
3. No merge ordering based on dependencies
4. No incremental merge (agent finishes → immediately merge)
5. Conflict resolution prompt could be improved

## Target State

### Proactive: File Ownership

During decomposition, tasks include `Files: [list of expected files]`. The orchestrator should:

1. **Detect overlap** - If two tasks list the same file, warn and optionally split
2. **Soft reservation** - Track which agent is working on which files. Log warnings on overlap but don't hard-block (AI decomposition estimates aren't perfect)
3. **Merge ordering** - If tasks A and B both touch `auth.cs`, merge A first, then rebase B onto the result

### Reactive: Conflict Resolution

Keep existing AI negotiation but improve:

1. **Incremental merging** - Don't wait for all agents. Merge completed agent work as soon as their tasks are done and dependencies are satisfied.
2. **Merge ordering by dependency** - Tasks with no dependents merge first. Tasks that others depend on merge before their dependents.
3. **Better conflict prompt** - Include task descriptions so the AI understands *intent* not just *diff*.

### Merge Flow

```
Agent completes task
  → Commits to worktree branch
  → Notifies orchestrator
  → Orchestrator checks: all dependencies merged?
    → Yes: Initiate merge
      → Success: Mark merged, check if this unblocks other merges
      → Conflict: Run AI negotiation
        → Success: Mark merged
        → Fail: Log, flag for manual resolution
    → No: Queue for later merge
```

## Technical Requirements

1. **File ownership tracking** - `Dictionary<string, string>` mapping file path → agent ID. Populated from task `Files` list during assignment. Warn on overlap.

2. **Incremental merge** - After each task completion, attempt merge if dependencies satisfied. Don't wait for all agents to finish.

3. **Dependency-ordered merging** - Merge tasks in topological order of the dependency DAG. Leaf tasks (no dependents) merge first.

4. **Improved conflict prompt** - Include both agents' task descriptions and intent, not just the diff blocks.

5. **Merge queue** - Orchestrator maintains a merge queue. Tasks enter queue on completion. Merges execute one at a time (sequential by default, configurable concurrency).

6. **Merge status tracking** - Per-task: `MergeStatus` enum (Pending, Queued, Merging, Merged, ConflictDetected, Resolved, Failed).

## Architecture

### MergeManager (extracted from GitWorktreeManager)

```csharp
public class MergeManager
{
    private readonly GitWorktreeManager _worktrees;
    private readonly ConflictNegotiator _negotiator;
    private readonly TaskStore _taskStore;
    private readonly Queue<string> _mergeQueue;
    private readonly Dictionary<string, string> _fileOwnership; // file → agentId

    /// Check for file conflicts between tasks before execution
    public List<FileConflictWarning> DetectFileOverlap(IEnumerable<AgentTask> tasks);

    /// Queue a completed task for merge
    public void QueueForMerge(string taskId);

    /// Process merge queue (called from orchestration loop)
    public async Task<MergeResult> ProcessNextMerge(CancellationToken ct);

    /// Check if a task is ready to merge (deps merged, in queue)
    public bool IsReadyToMerge(string taskId);

    /// Get merge status for all tasks
    public IReadOnlyDictionary<string, MergeStatus> GetMergeStatuses();
}
```

### Improved Conflict Negotiation Prompt

```
Two agents made conflicting changes to the same files.

Agent A ({agentName}): {taskDescription}
Their changes:
{diffA}

Agent B ({agentName}): {taskDescription}
Their changes:
{diffB}

Resolve the conflict by producing the merged file content that
preserves both agents' intended changes. If the changes are
fundamentally incompatible, prefer Agent A's changes (merged first)
and note what was lost.
```

## Acceptance Criteria

- [ ] File ownership tracked per-agent during task execution
- [ ] Overlapping file warnings produced during task assignment
- [ ] Completed tasks merge incrementally (don't wait for all agents)
- [ ] Merges respect dependency ordering (topological sort)
- [ ] Conflict resolution includes task descriptions for context
- [ ] Merge status tracked per-task (Pending → Merged or Failed)
- [ ] Failed merges flagged for manual resolution without blocking other merges
- [ ] Merge queue processes one merge at a time by default
- [ ] All existing merge strategies (Rebase, Direct, Sequential) still work

## Files to Modify/Create

| Action | File |
|--------|------|
| Create | `Merge/MergeManager.cs` |
| Create | `Merge/MergeStatus.cs` |
| Modify | `Git/GitWorktreeManager.cs` (extract merge logic) |
| Modify | `Parallel/ConflictNegotiator.cs` (improved prompt) |
| Modify | `Models/AgentTask.cs` (add MergeStatus field) |

## Dependencies
- [task-system.md](task-system.md) - Dependency DAG for merge ordering
- [agent-lifecycle.md](agent-lifecycle.md) - Task completion events trigger merge
- [orchestration.md](orchestration.md) - Orchestrator drives merge queue
