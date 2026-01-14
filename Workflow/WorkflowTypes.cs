using System.Text.Json.Serialization;

namespace RalphController.Workflow;

/// <summary>
/// Agent roles in collaborative workflows
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentRole
{
    /// <summary>Primary coding agent that implements features</summary>
    Implementer,

    /// <summary>Code review specialist that finds issues</summary>
    Reviewer,

    /// <summary>Devil's advocate that challenges assumptions</summary>
    Challenger,

    /// <summary>Defends the approach and highlights strengths</summary>
    Advocate,

    /// <summary>Breaks down complex tasks into actionable steps</summary>
    Planner,

    /// <summary>Combines multiple opinions into recommendations</summary>
    Synthesizer
}

/// <summary>
/// Types of collaborative workflows
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowType
{
    /// <summary>Feature specification workflow: Planner → Challenger → Synthesizer</summary>
    Spec,

    /// <summary>Code review workflow with severity tracking</summary>
    Review,

    /// <summary>Multi-opinion gathering with synthesis</summary>
    Consensus,

    /// <summary>Enhanced verification (existing, integrated)</summary>
    Verification
}

/// <summary>
/// Status of a workflow step
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowStepStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Skipped
}

/// <summary>
/// Overall workflow status
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowStatus
{
    NotStarted,
    InProgress,
    AwaitingInput,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Stance for consensus workflow participants
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsensusStance
{
    /// <summary>Argue in favor, highlight strengths</summary>
    For,

    /// <summary>Argue against, find problems</summary>
    Against,

    /// <summary>Balanced analysis</summary>
    Neutral
}

/// <summary>
/// Severity levels for code review findings
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewSeverity
{
    /// <summary>Must fix before merge (security, data loss, crashes)</summary>
    Critical,

    /// <summary>Should fix before merge (bugs, significant issues)</summary>
    High,

    /// <summary>Consider fixing (code quality, minor issues)</summary>
    Medium,

    /// <summary>Nice to have (style, minor improvements)</summary>
    Low,

    /// <summary>Observations (not issues, just notes)</summary>
    Info
}

/// <summary>
/// Focus areas for code review
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewFocus
{
    /// <summary>Complete review of all aspects</summary>
    Full,

    /// <summary>Security vulnerabilities and unsafe practices</summary>
    Security,

    /// <summary>Performance bottlenecks and inefficiencies</summary>
    Performance,

    /// <summary>Architecture and design patterns</summary>
    Architecture,

    /// <summary>Test coverage and quality</summary>
    Testing
}
