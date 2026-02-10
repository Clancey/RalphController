using System.Text.Json.Serialization;

namespace RalphController.Models;

/// <summary>
/// Represents a unit of work to be completed by an agent
/// </summary>
public class AgentTask
{
    /// <summary>Unique task identifier</summary>
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Task description (from implementation_plan.md)</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Original line from implementation_plan.md</summary>
    public string? SourceLine { get; set; }

    /// <summary>Task priority</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;

    /// <summary>Current status</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
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
    public List<string> DependsOn { get; set; } = new();

    /// <summary>Category/section from implementation plan</summary>
    public string? Category { get; set; }

    /// <summary>When the task was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the task was completed</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Likely files to modify (for teams decomposition)</summary>
    public List<string> Files { get; set; } = new();

    /// <summary>Short title for display</summary>
    public string? Title { get; set; }
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
