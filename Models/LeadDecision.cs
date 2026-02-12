namespace RalphController.Models;

/// <summary>
/// Decision produced by the Lead AI agent.
/// Parsed from structured output in the AI CLI response.
/// </summary>
public record LeadDecision
{
    /// <summary>Action the lead wants to take</summary>
    public LeadAction Action { get; init; }

    /// <summary>Task ID (for NextTask, RetryTask, SkipTask)</summary>
    public string? TaskId { get; init; }

    /// <summary>Reason for the decision</summary>
    public string? Reason { get; init; }

    /// <summary>New task description (for AddTask)</summary>
    public string? NewTaskDescription { get; init; }

    /// <summary>New task title (for AddTask)</summary>
    public string? NewTaskTitle { get; init; }

    /// <summary>Priority for new task (for AddTask)</summary>
    public TaskPriority? NewTaskPriority { get; init; }
}

/// <summary>
/// Actions the lead agent can take in its decision loop.
/// </summary>
public enum LeadAction
{
    /// <summary>Pick the next task and create a TaskAgent for it</summary>
    NextTask,

    /// <summary>Retry a previously failed task</summary>
    RetryTask,

    /// <summary>Add a new task to the queue</summary>
    AddTask,

    /// <summary>Skip a task (mark as not needed)</summary>
    SkipTask,

    /// <summary>All tasks done, declare the run complete</summary>
    DeclareComplete
}
