using System.Text.Json.Serialization;

namespace RalphController.Models;

/// <summary>
/// Represents a unit of work to be completed by an agent
/// </summary>
public class AgentTask
{
    /// <summary>Stable task identifier (e.g., "task-1", "task-2")</summary>
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Short title for display</summary>
    public string? Title { get; set; }

    /// <summary>Task description (full description for agent prompt)</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Original line from implementation_plan.md</summary>
    public string? SourceLine { get; set; }

    /// <summary>Task priority</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;

    /// <summary>Current status (Pending, InProgress, Completed, Failed)</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TaskStatus Status { get; set; } = TaskStatus.Pending;

    /// <summary>Task dependencies — task IDs (not titles) that must complete first</summary>
    public List<string> DependsOn { get; set; } = new();

    /// <summary>Likely files to modify (for teams decomposition)</summary>
    public List<string> Files { get; set; } = new();

    /// <summary>Agent ID that claimed this task</summary>
    public string? ClaimedByAgentId { get; set; }

    /// <summary>When the task was claimed</summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>Task result (when complete)</summary>
    public TaskResult? Result { get; set; }

    /// <summary>Error message if failed</summary>
    public string? Error { get; set; }

    /// <summary>Number of times this task has been retried</summary>
    public int RetryCount { get; set; }

    /// <summary>Maximum retries before marking as permanently Failed (default: 2)</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Category/section from implementation plan</summary>
    public string? Category { get; set; }

    /// <summary>When the task was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the task was completed</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Returns true if this task can be claimed: Pending status with all dependencies completed.
    /// </summary>
    public bool IsClaimable(IReadOnlyDictionary<string, AgentTask> allTasks)
    {
        if (Status != TaskStatus.Pending) return false;
        if (DependsOn.Count == 0) return true;

        return DependsOn.All(depId =>
            allTasks.TryGetValue(depId, out var dep) && dep.Status == TaskStatus.Completed);
    }
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
/// Task status — 3+1 state model matching Claude Code:
/// Pending → InProgress → Completed
///                     ↘ Failed (retry → Pending)
/// </summary>
public enum TaskStatus
{
    Pending,
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
