using System.Text.Json.Serialization;

namespace RalphController.Merge;

/// <summary>
/// Tracks the merge lifecycle of a completed task's worktree branch.
/// Transitions: Pending -> Queued -> Merging -> Merged
///                                           -> ConflictDetected -> Resolved -> Merged
///                                           -> ConflictDetected -> Failed
///                                -> Failed
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MergeStatus
{
    /// <summary>Task not yet ready for merge (still in progress or dependencies incomplete)</summary>
    Pending,

    /// <summary>Task completed and queued for merge</summary>
    Queued,

    /// <summary>Merge operation is actively running</summary>
    Merging,

    /// <summary>Successfully merged into target branch</summary>
    Merged,

    /// <summary>Merge produced conflicts that need resolution</summary>
    ConflictDetected,

    /// <summary>Conflicts were resolved (by AI or manually), ready to finalize</summary>
    Resolved,

    /// <summary>Merge failed permanently (after conflict resolution attempts)</summary>
    Failed
}
