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

    /// <summary>Full path to prompt file</summary>
    public string PromptFilePath => Path.Combine(TargetDirectory, PromptFile);

    /// <summary>Full path to plan file</summary>
    public string PlanFilePath => Path.Combine(TargetDirectory, PlanFile);

    /// <summary>Full path to agents file</summary>
    public string AgentsFilePath => Path.Combine(TargetDirectory, AgentsFile);

    /// <summary>Full path to specs directory</summary>
    public string SpecsDirectoryPath => Path.Combine(TargetDirectory, SpecsDirectory);
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
