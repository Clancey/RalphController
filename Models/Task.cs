namespace RalphController.Models;

/// <summary>
/// Represents a unit of work to be completed by an agent
/// </summary>
public class AgentTask
{
    /// <summary>Unique task identifier</summary>
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Task description (from implementation_plan.md)</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Original line from implementation_plan.md</summary>
    public string? SourceLine { get; init; }

    /// <summary>Task priority</summary>
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;

    /// <summary>Current status</summary>
    public TaskStatus Status { get; set; } = TaskStatus.Pending;

    /// <summary>Agent ID that claimed this task</summary>
    public string? ClaimedByAgentId { get; set; }

    /// <summary>When the task was claimed</summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>Task result (when complete)</summary>
    public TaskResult? Result { get; set; }

    /// <summary>Error message if failed</summary>
    public string? Error { get; set; }

    /// <summary>Retry count</summary>
    public int RetryCount { get; set; }

    /// <summary>Task dependencies (task IDs that must complete first)</summary>
    public List<string> DependsOn { get; init; } = new();

    /// <summary>Category/section from implementation plan</summary>
    public string? Category { get; init; }

    /// <summary>When the task was created</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When the task was completed</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Likely files to modify (for teams decomposition)</summary>
    public List<string> Files { get; init; } = new();

    /// <summary>Short title for display</summary>
    public string? Title { get; init; }
}

/// <summary>
/// Task priority levels
/// </summary>
public enum TaskPriority
{
    Critical,
    High,
    Normal,
    Low
}

/// <summary>
/// Task status
/// </summary>
public enum TaskStatus
{
    Pending,
    Claimed,
    InProgress,
    Completed,
    Failed
}

/// <summary>
/// Result of a completed task
/// </summary>
public record TaskResult(
    bool Success,
    string Summary,
    List<string> FilesModified,
    string Output = "",
    TimeSpan Duration = default)
{
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
}
