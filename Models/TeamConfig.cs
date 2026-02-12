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

    /// <summary>
    /// Delegate mode: lead coordinates only, cannot edit files.
    /// When enabled, lead can only spawn/shutdown agents, send messages,
    /// manage tasks, and review/approve plans.
    /// </summary>
    public bool DelegateMode { get; init; } = false;

    /// <summary>
    /// Display mode for teams TUI. InProcess renders within the terminal,
    /// SplitPane opens separate panes (tmux/iTerm2 required).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TeamDisplayMode DisplayMode { get; init; } = TeamDisplayMode.InProcess;

    /// <summary>
    /// Hook commands to run on specific team events.
    /// Key is the hook name (e.g., "TeammateIdle", "TaskCompleted"),
    /// value is the shell command to execute.
    /// </summary>
    public Dictionary<string, string> Hooks { get; init; } = new();

    /// <summary>
    /// Require all agents to submit plans before implementation.
    /// Individual agents can override this via AgentSpawnConfig.
    /// </summary>
    public bool RequirePlanApproval { get; init; } = false;

    /// <summary>
    /// Per-agent plan approval overrides. Key is agent index (0-based),
    /// value is whether that agent requires plan approval.
    /// Agents not listed use the global RequirePlanApproval setting.
    /// </summary>
    public Dictionary<int, bool> AgentPlanApproval { get; init; } = new();

    /// <summary>
    /// Team member configurations with per-agent settings
    /// </summary>
    public List<TeamMemberConfig> Members { get; init; } = new();

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

    /// <summary>Get whether a specific agent requires plan approval</summary>
    public bool GetRequirePlanApproval(int agentIndex)
    {
        if (AgentPlanApproval.TryGetValue(agentIndex, out var override_))
            return override_;

        if (agentIndex < Members.Count)
            return Members[agentIndex].RequirePlanApproval ?? RequirePlanApproval;

        return RequirePlanApproval;
    }

    /// <summary>Get the team storage directory</summary>
    public string GetTeamStoragePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ralph", "teams", TeamName);
    }

    /// <summary>Get the team config file path</summary>
    public string GetTeamConfigPath() =>
        Path.Combine(GetTeamStoragePath(), "config.json");

    /// <summary>Save team config to disk</summary>
    public void SaveToDisk()
    {
        var configPath = GetTeamConfigPath();
        var dir = Path.GetDirectoryName(configPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        });
        File.WriteAllText(configPath, json);
    }

    /// <summary>Load team config from disk, or return null if not found</summary>
    public static TeamConfig? LoadFromDisk(string teamName)
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ralph", "teams", teamName, "config.json");

        if (!File.Exists(configPath)) return null;

        try
        {
            var json = File.ReadAllText(configPath);
            return System.Text.Json.JsonSerializer.Deserialize<TeamConfig>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() }
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Clean up all team artifacts: worktrees, task files, mailbox, config
    /// </summary>
    public async Task CleanupAsync(string projectDirectory)
    {
        var teamDir = GetTeamStoragePath();

        // Remove task files
        var tasksDir = Path.Combine(teamDir, "tasks");
        if (Directory.Exists(tasksDir))
            Directory.Delete(tasksDir, true);

        // Remove mailbox files
        var mailboxDir = Path.Combine(teamDir, "mailbox");
        if (Directory.Exists(mailboxDir))
            Directory.Delete(mailboxDir, true);

        // Remove team config
        var configPath = GetTeamConfigPath();
        if (File.Exists(configPath))
            File.Delete(configPath);

        // Remove worktrees
        if (UseWorktrees)
        {
            var worktreeBase = Path.Combine(projectDirectory, ".ralph-worktrees");
            if (Directory.Exists(worktreeBase))
            {
                // Use git to properly remove worktrees
                var gitManager = new Git.GitWorktreeManager(projectDirectory);
                var worktreeDirs = Directory.GetDirectories(worktreeBase, $"team-*");
                foreach (var wtDir in worktreeDirs)
                {
                    await gitManager.RemoveWorktreeAsync(wtDir);
                }

                // Remove the base directory if empty
                if (Directory.Exists(worktreeBase) && !Directory.EnumerateFileSystemEntries(worktreeBase).Any())
                    Directory.Delete(worktreeBase);
            }
        }

        // Remove team directory if empty
        if (Directory.Exists(teamDir) && !Directory.EnumerateFileSystemEntries(teamDir).Any())
            Directory.Delete(teamDir);
    }
}

/// <summary>
/// Per-agent member configuration
/// </summary>
public record TeamMemberConfig
{
    /// <summary>Agent display name</summary>
    public string? Name { get; init; }

    /// <summary>Model specification for this agent</summary>
    public ModelSpec? Model { get; init; }

    /// <summary>Override plan approval for this agent (null = use team default)</summary>
    public bool? RequirePlanApproval { get; init; }

    /// <summary>Spawn prompt for this agent</summary>
    public string? SpawnPrompt { get; init; }
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

/// <summary>
/// Display mode for teams TUI
/// </summary>
public enum TeamDisplayMode
{
    /// <summary>All agents rendered within the same terminal (default)</summary>
    InProcess,

    /// <summary>Each agent gets a separate tmux/iTerm2 pane</summary>
    SplitPane
}
