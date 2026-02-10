namespace RalphController.Models;

/// <summary>
/// State of a parallel agent
/// </summary>
public enum ParallelAgentState
{
    /// <summary>Agent not started</summary>
    Idle,

    /// <summary>Setting up worktree and environment</summary>
    Initializing,

    /// <summary>Actively working on a task</summary>
    Running,

    /// <summary>Waiting for merge slot or conflict resolution</summary>
    Waiting,

    /// <summary>Merging work back to main</summary>
    Merging,

    /// <summary>Agent stopped successfully</summary>
    Stopped,

    /// <summary>Agent failed (will retry)</summary>
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
    public ParallelAgentState State { get; set; }

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

    /// <summary>Total output characters generated</summary>
    public long OutputChars { get; set; }

    /// <summary>Total error characters</summary>
    public long ErrorChars { get; set; }

    /// <summary>Provider/model assigned to this agent</summary>
    public ModelSpec? AssignedModel { get; set; }
}
