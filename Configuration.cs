using RalphController.Models;

namespace RalphController;

/// <summary>
/// Configuration for the Ralph Controller
/// </summary>
public record RalphConfig
{
    /// <summary>Target directory where the AI will work</summary>
    public required string TargetDirectory { get; init; }

    /// <summary>AI provider to use (Claude, Codex, etc.)</summary>
    public AIProvider Provider { get; init; } = AIProvider.Claude;

    /// <summary>Provider-specific configuration</summary>
    public AIProviderConfig ProviderConfig { get; init; } = AIProviderConfig.ForClaude();

    /// <summary>Multi-model configuration (rotation, verification)</summary>
    public MultiModelConfig? MultiModel { get; init; }

    /// <summary>Teams mode configuration</summary>
    public TeamConfig? Teams { get; init; }

    /// <summary>Path to AI CLI executable (overrides provider default)</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Name of the prompt file</summary>
    public string PromptFile { get; init; } = "prompt.md";

    /// <summary>Name of the implementation plan file</summary>
    public string PlanFile { get; init; } = "implementation_plan.md";

    /// <summary>Name of the agents file (self-improvement notes)</summary>
    public string AgentsFile { get; init; } = "agents.md";

    /// <summary>Name of the specs directory</summary>
    public string SpecsDirectory { get; init; } = "specs";

    /// <summary>Optional folder for Ralph project files (relative to TargetDirectory, or absolute path)</summary>
    public string? RalphFolder { get; init; }

    /// <summary>Directory where project files (prompt.md, plan, agents, specs) are stored</summary>
    public string ProjectFilesDirectory => RalphFolder switch
    {
        null or "" => TargetDirectory,
        _ when Path.IsPathRooted(RalphFolder) => RalphFolder,
        _ => Path.Combine(TargetDirectory, RalphFolder)
    };

    /// <summary>Optional maximum number of iterations (null = unlimited)</summary>
    public int? MaxIterations { get; init; }

    /// <summary>Cost per hour estimate for tracking</summary>
    public double CostPerHour { get; init; } = 10.50;

    /// <summary>Delay between iterations in milliseconds</summary>
    public int IterationDelayMs { get; init; } = 1000;

    /// <summary>Maximum API calls per hour (rate limiting)</summary>
    public int MaxCallsPerHour { get; init; } = 100;

    /// <summary>Enable circuit breaker to detect stagnation</summary>
    public bool EnableCircuitBreaker { get; init; } = true;

    /// <summary>Enable response analyzer for completion detection</summary>
    public bool EnableResponseAnalyzer { get; init; } = true;

    /// <summary>Auto-exit when completion signals detected</summary>
    public bool AutoExitOnCompletion { get; init; } = true;

    /// <summary>Enable final verification step before completing</summary>
    public bool EnableFinalVerification { get; init; } = true;

    /// <summary>Maximum time for a single iteration in minutes (default: 30, 0 = no timeout)</summary>
    public int IterationTimeoutMinutes { get; init; } = 30;

    /// <summary>Full path to prompt file</summary>
    public string PromptFilePath => Path.Combine(ProjectFilesDirectory, PromptFile);

    /// <summary>Full path to plan file</summary>
    public string PlanFilePath => Path.Combine(ProjectFilesDirectory, PlanFile);

    /// <summary>Full path to agents file</summary>
    public string AgentsFilePath => Path.Combine(ProjectFilesDirectory, AgentsFile);

    /// <summary>Full path to specs directory</summary>
    public string SpecsDirectoryPath => Path.Combine(ProjectFilesDirectory, SpecsDirectory);
}

/// <summary>
/// Validates and checks project structure
/// </summary>
public static class ProjectValidator
{
    /// <summary>
    /// Checks the target directory for required Ralph project files
    /// </summary>
    public static ProjectStructure ValidateProject(RalphConfig config)
    {
        return new ProjectStructure
        {
            TargetDirectory = config.TargetDirectory,
            HasAgentsMd = File.Exists(config.AgentsFilePath),
            HasSpecsDirectory = Directory.Exists(config.SpecsDirectoryPath),
            HasPromptMd = File.Exists(config.PromptFilePath),
            HasImplementationPlan = File.Exists(config.PlanFilePath)
        };
    }

    /// <summary>
    /// Validates the target directory exists
    /// </summary>
    public static bool ValidateTargetDirectory(string path, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Target directory path cannot be empty";
            return false;
        }

        if (!Directory.Exists(path))
        {
            error = $"Target directory does not exist: {path}";
            return false;
        }

        return true;
    }
}
