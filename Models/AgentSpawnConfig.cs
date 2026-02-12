namespace RalphController.Models;

/// <summary>
/// Configuration for spawning a new team agent
/// </summary>
public record AgentSpawnConfig
{
    /// <summary>Human-readable name for the agent</summary>
    public required string Name { get; init; }

    /// <summary>Model specification (null = use lead's model)</summary>
    public ModelSpec? Model { get; init; }

    /// <summary>Task-specific context prepended to agent's AI prompt</summary>
    public string? SpawnPrompt { get; init; }

    /// <summary>If true, agent must produce a plan and wait for lead approval before implementing</summary>
    public bool RequirePlanApproval { get; init; }

    /// <summary>Working directory for the agent (typically a worktree path)</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment variables to set for the agent process</summary>
    public Dictionary<string, string>? Environment { get; init; }

    /// <summary>Agent index for naming (e.g., "Agent 1")</summary>
    public int AgentIndex { get; init; }
}
