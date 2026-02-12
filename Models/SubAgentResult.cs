namespace RalphController.Models;

/// <summary>
/// Result from a single sub-agent phase (Plan, Code, or Verify).
/// </summary>
public record SubAgentResult
{
    /// <summary>Whether the phase succeeded</summary>
    public bool Success { get; init; }

    /// <summary>Raw output from the AI process</summary>
    public string Output { get; init; } = "";

    /// <summary>Error message if failed</summary>
    public string? Error { get; init; }

    /// <summary>Which phase produced this result</summary>
    public SubAgentPhase Phase { get; init; }

    /// <summary>Files modified during this phase</summary>
    public List<string> FilesModified { get; init; } = new();
}

/// <summary>
/// Aggregated result from a TaskAgent's full execution (all sub-agent phases).
/// </summary>
public record TaskAgentResult
{
    /// <summary>Plan phase result (if run)</summary>
    public SubAgentResult? Plan { get; init; }

    /// <summary>Code phase result (if run)</summary>
    public SubAgentResult? Code { get; init; }

    /// <summary>Verify phase result (if run)</summary>
    public SubAgentResult? Verify { get; init; }

    /// <summary>Overall success — true only if all executed phases succeeded</summary>
    public bool Success { get; init; }

    /// <summary>Branch name for the worktree used by this task agent</summary>
    public string BranchName { get; init; } = "";

    /// <summary>Summary of what was accomplished</summary>
    public string Summary { get; init; } = "";

    /// <summary>All files modified across all phases</summary>
    public List<string> AllFilesModified { get; init; } = new();
}

/// <summary>
/// Phases a sub-agent can execute within a TaskAgent.
/// </summary>
public enum SubAgentPhase
{
    /// <summary>No phase active</summary>
    None,

    /// <summary>Read-only analysis and planning</summary>
    Plan,

    /// <summary>Implementation (code changes)</summary>
    Code,

    /// <summary>Verification (build, test, review)</summary>
    Verify
}
