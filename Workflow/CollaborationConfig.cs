using System.Text.Json.Serialization;
using RalphController.Models;

namespace RalphController.Workflow;

/// <summary>
/// Configuration for agent collaboration workflows
/// </summary>
public class CollaborationConfig
{
    /// <summary>Enable collaboration workflows</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Default workflow type when not specified</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WorkflowType DefaultWorkflow { get; set; } = WorkflowType.Verification;

    /// <summary>Directory containing system prompts (relative to target directory)</summary>
    public string PromptsDirectory { get; set; } = "prompts";

    /// <summary>Agent configurations by role (supports multiple agents per role)</summary>
    public Dictionary<AgentRole, List<AgentConfig>> Agents { get; set; } = new();

    /// <summary>Spec workflow settings</summary>
    public SpecWorkflowConfig? Spec { get; set; }

    /// <summary>Review workflow settings</summary>
    public ReviewWorkflowConfig? Review { get; set; }

    /// <summary>Consensus workflow settings</summary>
    public ConsensusWorkflowConfig? Consensus { get; set; }

    /// <summary>Verification workflow settings (replaces single-model verification)</summary>
    public VerificationWorkflowConfig? Verification { get; set; }

    /// <summary>
    /// Custom model tier overrides (pattern → tier mapping)
    /// Example: { "my-custom-model": "Expert", "cheap-model": "Fast" }
    /// Patterns are matched as substrings (case-insensitive)
    /// </summary>
    public Dictionary<string, ModelTier> TierOverrides { get; set; } = new();

    /// <summary>
    /// Apply tier overrides from config to static ModelSpec.TierOverrides
    /// Call this after loading configuration
    /// </summary>
    public void ApplyTierOverrides()
    {
        if (TierOverrides.Count > 0)
        {
            foreach (var (pattern, tier) in TierOverrides)
            {
                ModelSpec.TierOverrides[pattern] = tier;
            }
        }
    }

    /// <summary>
    /// Get all agent configs for a role, or create default
    /// </summary>
    public List<AgentConfig> GetAgentConfigs(AgentRole role)
    {
        if (Agents.TryGetValue(role, out var configs) && configs.Count > 0)
            return configs;

        return new List<AgentConfig> { new AgentConfig() };
    }

    /// <summary>
    /// Get the primary (first) agent config for a role
    /// </summary>
    public AgentConfig GetAgentConfig(AgentRole role)
    {
        return GetAgentConfigs(role).First();
    }

    /// <summary>
    /// Add an agent to a role
    /// </summary>
    public void AddAgent(AgentRole role, AgentConfig config)
    {
        if (!Agents.ContainsKey(role))
            Agents[role] = new List<AgentConfig>();
        Agents[role].Add(config);
    }

    /// <summary>
    /// Get the total number of agents configured for a role
    /// </summary>
    public int GetAgentCount(AgentRole role)
    {
        if (Agents.TryGetValue(role, out var configs))
            return configs.Count;
        return 1; // Default agent
    }
}

/// <summary>
/// Configuration for a specific agent role
/// </summary>
public class AgentConfig
{
    /// <summary>Model to use for this agent (takes precedence over pool)</summary>
    public ModelSpec? Model { get; set; }

    /// <summary>Pool of models for round-robin selection (used for parallel reviewers, etc.)</summary>
    public List<ModelSpec> ModelPool { get; set; } = new();

    /// <summary>Current index in model pool for round-robin</summary>
    [JsonIgnore]
    private int _poolIndex = 0;

    /// <summary>Path to custom system prompt file (relative to prompts directory)</summary>
    public string? SystemPromptFile { get; set; }

    /// <summary>Max tokens for response</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>Temperature for response generation</summary>
    public float Temperature { get; set; } = 0.7f;

    /// <summary>Additional instructions to append to system prompt</summary>
    public string? AdditionalInstructions { get; set; }

    /// <summary>Get the next model from the pool (round-robin), or the configured model</summary>
    public ModelSpec? GetNextModel()
    {
        // If a specific model is configured, use it
        if (Model != null)
            return Model;

        // If pool is empty, return null
        if (ModelPool.Count == 0)
            return null;

        // Round-robin through pool
        var model = ModelPool[_poolIndex];
        _poolIndex = (_poolIndex + 1) % ModelPool.Count;
        return model;
    }

    /// <summary>Reset the pool index (for new workflow runs)</summary>
    public void ResetPoolIndex() => _poolIndex = 0;
}

/// <summary>
/// Configuration for spec (feature specification) workflow
/// </summary>
public class SpecWorkflowConfig
{
    /// <summary>Include challenger pass to critique the initial spec</summary>
    public bool EnableChallenger { get; set; } = true;

    /// <summary>Maximum refinement iterations if issues found</summary>
    public int MaxRefinements { get; set; } = 2;

    /// <summary>Auto-approve if challenger finds no significant issues</summary>
    public bool AutoApprove { get; set; } = false;

    /// <summary>Include implementation after spec approval</summary>
    public bool AutoImplement { get; set; } = false;

    /// <summary>Files to include as context for planning</summary>
    public List<string> ContextPatterns { get; set; } = new() { "*.md", "*.json" };
}

/// <summary>
/// Configuration for code review workflow
/// </summary>
public class ReviewWorkflowConfig
{
    /// <summary>Minimum severity level to report</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ReviewSeverity MinSeverity { get; set; } = ReviewSeverity.Low;

    /// <summary>Number of independent reviewers to use</summary>
    public int ReviewerCount { get; set; } = 1;

    /// <summary>Require expert validation for critical issues</summary>
    public bool ExpertValidation { get; set; } = true;

    /// <summary>Focus areas for the review</summary>
    public List<ReviewFocus> FocusAreas { get; set; } = new() { ReviewFocus.Full };

    /// <summary>Block merge recommendations for critical/high issues</summary>
    public bool BlockOnHighSeverity { get; set; } = true;

    /// <summary>Include suggested fixes in findings</summary>
    public bool IncludeSuggestions { get; set; } = true;

    /// <summary>Git commit range to review (e.g., "HEAD~3..HEAD")</summary>
    public string? DefaultCommitRange { get; set; }
}

/// <summary>
/// Configuration for consensus workflow
/// </summary>
public class ConsensusWorkflowConfig
{
    /// <summary>Participants with their models and stances</summary>
    public List<ConsensusParticipant> Participants { get; set; } = new();

    /// <summary>Blind each model to others' responses (prevents groupthink)</summary>
    public bool BlindedAnalysis { get; set; } = true;

    /// <summary>Include final synthesis step to combine opinions</summary>
    public bool EnableSynthesis { get; set; } = true;

    /// <summary>Run participant analyses in parallel</summary>
    public bool ParallelExecution { get; set; } = false;

    /// <summary>Timeout per participant in seconds</summary>
    public int ParticipantTimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// Configuration for verification workflow (replaces single-model verification)
/// </summary>
public class VerificationWorkflowConfig
{
    /// <summary>Enable multi-agent verification workflow</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Number of reviewers for verification</summary>
    public int ReviewerCount { get; set; } = 2;

    /// <summary>Require unanimous approval (all reviewers agree no changes needed)</summary>
    public bool RequireUnanimous { get; set; } = false;

    /// <summary>Include challenger to find edge cases</summary>
    public bool EnableChallenger { get; set; } = true;

    /// <summary>Run verification steps in parallel where possible</summary>
    public bool ParallelExecution { get; set; } = true;

    /// <summary>Maximum verification attempts before allowing completion</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Focus areas for verification review</summary>
    public List<ReviewFocus> FocusAreas { get; set; } = new()
    {
        ReviewFocus.Full,
        ReviewFocus.Testing
    };
}

/// <summary>
/// A participant in a consensus workflow
/// </summary>
public class ConsensusParticipant
{
    /// <summary>Model to use for this participant</summary>
    public ModelSpec Model { get; set; } = new();

    /// <summary>Stance this participant should take</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ConsensusStance Stance { get; set; } = ConsensusStance.Neutral;

    /// <summary>Custom prompt override for this participant</summary>
    public string? CustomPrompt { get; set; }

    /// <summary>Display name for this participant</summary>
    public string? Label { get; set; }

    /// <summary>Get display name (label or model name with stance)</summary>
    [JsonIgnore]
    public string DisplayName => Label ?? $"{Model.DisplayName} ({Stance})";
}
