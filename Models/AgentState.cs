namespace RalphController.Models;

/// <summary>
/// Lifecycle state of a team agent, matching Claude Code agent-teams model.
/// State transitions: Spawning → Ready → [Claiming → Working → Idle] (loop) → ShuttingDown → Stopped
///                                                                         ↘ Error
/// </summary>
public enum AgentState
{
    /// <summary>Agent process starting, loading context</summary>
    Spawning,

    /// <summary>Loaded and waiting for first task</summary>
    Ready,

    /// <summary>Attempting to claim a task from TaskStore</summary>
    Claiming,

    /// <summary>Executing a claimed task</summary>
    Working,

    /// <summary>In plan mode, waiting for lead approval (read-only)</summary>
    PlanningWork,

    /// <summary>Lead agent is deciding which task to run next</summary>
    Deciding,

    /// <summary>Sub-agent is in the Code phase (implementation)</summary>
    Coding,

    /// <summary>Sub-agent is in the Verify phase (build/test/review)</summary>
    Verifying,

    /// <summary>Lead agent is reviewing a TaskAgent's result</summary>
    Reviewing,

    /// <summary>Lead/orchestrator is merging a completed task's worktree</summary>
    MergingWork,

    /// <summary>No claimable tasks available; waiting for tasks to unblock</summary>
    Idle,

    /// <summary>Received shutdown request, finishing current work</summary>
    ShuttingDown,

    /// <summary>Cleanly exited</summary>
    Stopped,

    /// <summary>Unrecoverable error; requires lead intervention</summary>
    Error
}

/// <summary>
/// Legacy state enum for backwards compatibility during migration.
/// Will be removed once all code is updated to use AgentState.
/// </summary>
[Obsolete("Use AgentState instead. This enum will be removed.")]
public enum ParallelAgentState
{
    Idle,
    Initializing,
    Running,
    Waiting,
    Merging,
    Stopped,
    Failed
}

/// <summary>
/// Statistics for a single agent
/// </summary>
public class AgentStatistics
{
    /// <summary>Agent identifier</summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>Agent display name</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Current state</summary>
    public AgentState State { get; set; }

    /// <summary>Current task (if any)</summary>
    public AgentTask? CurrentTask { get; set; }

    /// <summary>Tasks completed</summary>
    public int TasksCompleted { get; set; }

    /// <summary>Tasks failed</summary>
    public int TasksFailed { get; set; }

    /// <summary>Total iterations run</summary>
    public int Iterations { get; set; }

    /// <summary>When the agent started</summary>
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Total duration active</summary>
    public TimeSpan TotalDuration => DateTime.UtcNow - StartedAt;

    /// <summary>Last activity timestamp</summary>
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    /// <summary>Worktree path</summary>
    public string? WorktreePath { get; set; }

    /// <summary>Branch name</summary>
    public string? BranchName { get; set; }

    /// <summary>Merge success rate</summary>
    public double MergeSuccessRate => MergesAttempted > 0
        ? (double)MergesSucceeded / MergesAttempted
        : 1.0;

    /// <summary>Merges attempted</summary>
    public int MergesAttempted { get; set; }

    /// <summary>Merges succeeded</summary>
    public int MergesSucceeded { get; set; }

    /// <summary>Conflicts detected</summary>
    public int ConflictsDetected { get; set; }

    /// <summary>Cumulative time spent in AI process calls</summary>
    public TimeSpan AITime { get; set; }

    /// <summary>Total output characters generated</summary>
    public long OutputChars { get; set; }

    /// <summary>Total error characters</summary>
    public long ErrorChars { get; set; }

    /// <summary>Provider/model assigned to this agent</summary>
    public ModelSpec? AssignedModel { get; set; }

    /// <summary>Current sub-agent phase (for lead-driven mode TaskAgents)</summary>
    public SubAgentPhase? CurrentSubPhase { get; set; }
}
