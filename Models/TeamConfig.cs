using System.Text.Json.Serialization;

namespace RalphController.Models;

/// <summary>
/// Configuration for teams mode AI agent execution
/// </summary>
public record TeamConfig
{
    /// <summary>Team name for scoped storage (defaults to "default")</summary>
    public string TeamName { get; init; } = "default";

    /// <summary>Number of sub-agents (2-8)</summary>
    public int AgentCount
    {
        get => _agentCount;
        set => _agentCount = Math.Clamp(value, 2, 8);
    }
    private int _agentCount = 2;

    /// <summary>Branch to create worktrees from (detected from current git branch)</summary>
    public string SourceBranch { get; init; } = "";

    /// <summary>Branch to merge completed work into (detected from current git branch)</summary>
    public string TargetBranch { get; init; } = "";

    /// <summary>Strategy for merging agent work back to target</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MergeStrategy MergeStrategy { get; init; } = MergeStrategy.Sequential;

    /// <summary>How to resolve conflicts between agents</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ConflictResolutionMode ConflictResolution { get; init; } = ConflictResolutionMode.AINegotiated;

    /// <summary>Maximum concurrent merges</summary>
    public int MaxConcurrentMerges { get; init; } = 1;

    /// <summary>Task claim timeout in seconds</summary>
    public int TaskClaimTimeoutSeconds { get; init; } = 300;

    /// <summary>Cleanup worktrees after successful merge</summary>
    public bool CleanupWorktreesOnSuccess { get; init; } = true;

    /// <summary>Maximum retries for failed tasks</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Provider/model for the lead/coordinator agent</summary>
    public ModelSpec? LeadModel { get; init; }

    /// <summary>Provider/model assignments for sub-agents</summary>
    public List<ModelSpec> AgentModels { get; init; } = new();

    /// <summary>How sub-agent models are assigned</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentModelAssignment ModelAssignment { get; init; } = AgentModelAssignment.SameAsLead;

    /// <summary>How the lead decomposes tasks</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DecompositionStrategy DecompositionStrategy { get; init; } = DecompositionStrategy.AIDecomposed;

    /// <summary>Whether to isolate agents in git worktrees (default true)</summary>
    public bool UseWorktrees { get; init; } = true;

    /// <summary>Teams mode is enabled</summary>
    [JsonIgnore]
    public bool IsEnabled => AgentCount > 1;

    /// <summary>Get the model spec for a given agent index</summary>
    public ModelSpec? GetAgentModel(int agentIndex)
    {
        return ModelAssignment switch
        {
            AgentModelAssignment.SameAsLead => LeadModel,
            AgentModelAssignment.PerAgent => agentIndex < AgentModels.Count ? AgentModels[agentIndex] : LeadModel,
            AgentModelAssignment.RoundRobin => AgentModels.Count > 0 ? AgentModels[agentIndex % AgentModels.Count] : LeadModel,
            _ => LeadModel
        };
    }
}

/// <summary>
/// How sub-agent models are assigned
/// </summary>
public enum AgentModelAssignment
{
    /// <summary>All sub-agents use the same model as the lead</summary>
    SameAsLead,

    /// <summary>Each agent gets a specific model</summary>
    PerAgent,

    /// <summary>Cycle through a list of models</summary>
    RoundRobin
}

/// <summary>
/// How the lead decomposes tasks
/// </summary>
public enum DecompositionStrategy
{
    /// <summary>Parse tasks from implementation_plan.md</summary>
    FromPlan,

    /// <summary>AI lead decomposes the prompt into subtasks</summary>
    AIDecomposed
}

/// <summary>
/// Strategy for merging agent work back to target branch
/// </summary>
public enum MergeStrategy
{
    /// <summary>Rebase onto target, then merge (minimizes conflicts)</summary>
    RebaseThenMerge,

    /// <summary>Direct merge (faster but more conflicts)</summary>
    MergeDirect,

    /// <summary>One merge at a time (safest)</summary>
    Sequential
}

/// <summary>
/// How to handle conflicts between agents
/// </summary>
public enum ConflictResolutionMode
{
    /// <summary>AI discussion to resolve</summary>
    AINegotiated,

    /// <summary>Last modification by timestamp wins</summary>
    LastWriterWins,

    /// <summary>Require manual intervention</summary>
    Manual
}
