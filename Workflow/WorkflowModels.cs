using System.Text.Json.Serialization;
using RalphController.Models;

namespace RalphController.Workflow;

/// <summary>
/// Context passed to an agent during workflow execution
/// </summary>
public class AgentContext
{
    /// <summary>Role this agent is playing</summary>
    public AgentRole Role { get; init; }

    /// <summary>System prompt for the agent</summary>
    public string SystemPrompt { get; init; } = "";

    /// <summary>Model specification to use</summary>
    public ModelSpec Model { get; init; } = new();

    /// <summary>Stance for consensus workflows</summary>
    public ConsensusStance? Stance { get; init; }

    /// <summary>Whether this agent is blinded to other responses</summary>
    public bool Blinded { get; init; }

    /// <summary>Additional metadata for the agent</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// A single step in a workflow
/// </summary>
public class WorkflowStep
{
    /// <summary>Unique ID for this step</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Step number in sequence (1-based)</summary>
    public int StepNumber { get; init; }

    /// <summary>Agent role executing this step</summary>
    public AgentRole Agent { get; init; }

    /// <summary>Model used for this step</summary>
    public string ModelName { get; set; } = "";

    /// <summary>Prompt sent to the agent</summary>
    public string Prompt { get; init; } = "";

    /// <summary>Response from the agent</summary>
    public string? Response { get; set; }

    /// <summary>Time taken for this step</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>When this step started</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When this step completed</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Current status of this step</summary>
    public WorkflowStepStatus Status { get; set; } = WorkflowStepStatus.Pending;

    /// <summary>Error message if failed</summary>
    public string? Error { get; set; }

    /// <summary>Token count for the response</summary>
    public int? TokenCount { get; set; }
}

/// <summary>
/// A complete collaboration workflow
/// </summary>
public class CollaborationWorkflow
{
    /// <summary>Unique ID for this workflow</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Type of workflow</summary>
    public WorkflowType Type { get; init; }

    /// <summary>Original user request that triggered this workflow</summary>
    public string OriginalRequest { get; init; } = "";

    /// <summary>Steps in this workflow</summary>
    public List<WorkflowStep> Steps { get; init; } = new();

    /// <summary>Context files included in the workflow</summary>
    public List<string> ContextFiles { get; init; } = new();

    /// <summary>Final synthesized output</summary>
    public string? FinalOutput { get; set; }

    /// <summary>Current workflow status</summary>
    public WorkflowStatus Status { get; set; } = WorkflowStatus.NotStarted;

    /// <summary>When the workflow started</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When the workflow completed</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Total duration of the workflow</summary>
    [JsonIgnore]
    public TimeSpan? TotalDuration => CompletedAt.HasValue && StartedAt.HasValue
        ? CompletedAt.Value - StartedAt.Value
        : null;

    /// <summary>Error message if workflow failed</summary>
    public string? Error { get; set; }

    /// <summary>Get the current step (last in-progress or pending)</summary>
    [JsonIgnore]
    public WorkflowStep? CurrentStep => Steps
        .FirstOrDefault(s => s.Status == WorkflowStepStatus.InProgress)
        ?? Steps.FirstOrDefault(s => s.Status == WorkflowStepStatus.Pending);

    /// <summary>Get completed steps</summary>
    [JsonIgnore]
    public IEnumerable<WorkflowStep> CompletedSteps => Steps
        .Where(s => s.Status == WorkflowStepStatus.Completed);
}

/// <summary>
/// Result of a spec workflow
/// </summary>
public class SpecResult
{
    /// <summary>Initial spec from the planner</summary>
    public string InitialSpec { get; init; } = "";

    /// <summary>Challenges/critiques from the challenger</summary>
    public string? Challenges { get; init; }

    /// <summary>Final refined spec from the synthesizer</summary>
    public string FinalSpec { get; init; } = "";

    /// <summary>The workflow that produced this result</summary>
    public CollaborationWorkflow Workflow { get; init; } = new();

    /// <summary>Whether the spec was approved</summary>
    public bool Approved { get; set; }

    /// <summary>Extracted tasks from the spec</summary>
    public List<SpecTask> Tasks { get; set; } = new();
}

/// <summary>
/// A task extracted from a spec
/// </summary>
public class SpecTask
{
    public int Order { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Complexity { get; init; } = "medium";
    public List<string> Files { get; init; } = new();
    public List<string> Dependencies { get; init; } = new();
}

/// <summary>
/// Result of a code review workflow
/// </summary>
public class ReviewResult
{
    /// <summary>All findings from the review</summary>
    public List<ReviewFinding> Findings { get; init; } = new();

    /// <summary>Summary of the review</summary>
    public string Summary { get; init; } = "";

    /// <summary>The workflow that produced this result</summary>
    public CollaborationWorkflow Workflow { get; init; } = new();

    /// <summary>Whether the code is approved for merge</summary>
    public bool Approved => !Findings.Any(f =>
        f.Severity == ReviewSeverity.Critical ||
        f.Severity == ReviewSeverity.High);

    /// <summary>Count of findings by severity</summary>
    [JsonIgnore]
    public Dictionary<ReviewSeverity, int> SeverityCounts => Findings
        .GroupBy(f => f.Severity)
        .ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>
/// A single finding from a code review
/// </summary>
public class ReviewFinding
{
    /// <summary>Unique ID for this finding</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Severity of the finding</summary>
    public ReviewSeverity Severity { get; init; }

    /// <summary>Category (security, performance, etc.)</summary>
    public string Category { get; init; } = "";

    /// <summary>Brief title of the issue</summary>
    public string Title { get; init; } = "";

    /// <summary>File path where the issue was found</summary>
    public string FilePath { get; init; } = "";

    /// <summary>Line number (if applicable)</summary>
    public int? LineNumber { get; init; }

    /// <summary>Detailed description of the issue</summary>
    public string Description { get; init; } = "";

    /// <summary>Why this issue matters</summary>
    public string? Impact { get; init; }

    /// <summary>Suggested fix</summary>
    public string? Suggestion { get; init; }

    /// <summary>Confidence level of the finding</summary>
    public string Confidence { get; init; } = "Medium";

    /// <summary>Which reviewer found this</summary>
    public string? FoundBy { get; init; }

    /// <summary>Whether this was validated by expert review</summary>
    public bool ExpertValidated { get; set; }

    /// <summary>Format as location string</summary>
    [JsonIgnore]
    public string Location => LineNumber.HasValue
        ? $"{FilePath}:{LineNumber}"
        : FilePath;
}

/// <summary>
/// Result of a consensus workflow
/// </summary>
public class ConsensusResult
{
    /// <summary>Original proposal being discussed</summary>
    public string Proposal { get; init; } = "";

    /// <summary>Opinions from each participant</summary>
    public List<ConsensusOpinion> Opinions { get; init; } = new();

    /// <summary>Synthesized recommendation</summary>
    public string? Synthesis { get; init; }

    /// <summary>Points of agreement across opinions</summary>
    public List<string> Agreements { get; init; } = new();

    /// <summary>Points of disagreement</summary>
    public List<string> Disagreements { get; init; } = new();

    /// <summary>Final recommendations</summary>
    public List<string> Recommendations { get; init; } = new();

    /// <summary>The workflow that produced this result</summary>
    public CollaborationWorkflow Workflow { get; init; } = new();
}

/// <summary>
/// A single opinion in a consensus workflow
/// </summary>
public class ConsensusOpinion
{
    /// <summary>Model that provided this opinion</summary>
    public string Model { get; init; } = "";

    /// <summary>Stance taken (for, against, neutral)</summary>
    public ConsensusStance Stance { get; init; }

    /// <summary>The analysis/opinion content</summary>
    public string Analysis { get; init; } = "";

    /// <summary>Key points extracted from the analysis</summary>
    public List<string> KeyPoints { get; init; } = new();

    /// <summary>Concerns raised</summary>
    public List<string> Concerns { get; init; } = new();

    /// <summary>Strengths identified</summary>
    public List<string> Strengths { get; init; } = new();
}

/// <summary>
/// Result of a verification workflow (replaces single-model verification)
/// </summary>
public class WorkflowVerificationResult
{
    /// <summary>Whether verification passed (no critical issues found)</summary>
    public bool Passed { get; init; }

    /// <summary>Number of reviewers who approved</summary>
    public int ApprovingReviewers { get; init; }

    /// <summary>Total number of reviewers</summary>
    public int TotalReviewers { get; init; }

    /// <summary>Findings from all reviewers</summary>
    public List<ReviewFinding> Findings { get; init; } = new();

    /// <summary>Changes that should be made (if any)</summary>
    public List<string> RequiredChanges { get; init; } = new();

    /// <summary>Summary of the verification</summary>
    public string Summary { get; init; } = "";

    /// <summary>Number of files modified during verification</summary>
    public int FilesModified { get; init; }

    /// <summary>Challenge points raised (if challenger enabled)</summary>
    public List<string> ChallengePoints { get; init; } = new();

    /// <summary>The workflow that produced this result</summary>
    public CollaborationWorkflow Workflow { get; init; } = new();

    /// <summary>Whether it was a unanimous approval</summary>
    public bool IsUnanimous => ApprovingReviewers == TotalReviewers && TotalReviewers > 0;

    /// <summary>Approval percentage</summary>
    public double ApprovalRate => TotalReviewers > 0
        ? (double)ApprovingReviewers / TotalReviewers * 100
        : 0;
}
