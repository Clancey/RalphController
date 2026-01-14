namespace RalphController.Models;

/// <summary>
/// Represents the current state of the Ralph loop
/// </summary>
public enum LoopState
{
    /// <summary>Loop is not running</summary>
    Idle,

    /// <summary>Loop is actively running iterations</summary>
    Running,

    /// <summary>Loop is paused between iterations</summary>
    Paused,

    /// <summary>Loop is stopping after current iteration</summary>
    Stopping
}

/// <summary>
/// Represents the status of required project files
/// </summary>
public record ProjectStructure
{
    public required string TargetDirectory { get; init; }
    public bool HasAgentsMd { get; init; }
    public bool HasSpecsDirectory { get; init; }
    public bool HasPromptMd { get; init; }
    public bool HasImplementationPlan { get; init; }
    public bool HasPromptsDirectory { get; init; }

    /// <summary>Core files needed to run (specs is optional)</summary>
    public bool IsComplete => HasAgentsMd && HasPromptMd && HasImplementationPlan;

    /// <summary>Whether the project has agent collaboration prompts set up</summary>
    public bool HasCollaborationSetup => HasPromptsDirectory;

    public List<string> MissingItems
    {
        get
        {
            var missing = new List<string>();
            if (!HasAgentsMd) missing.Add("agents.md");
            if (!HasPromptMd) missing.Add("prompt.md");
            if (!HasImplementationPlan) missing.Add("implementation_plan.md");
            // specs/ is now optional - don't add to missing
            return missing;
        }
    }

    public List<string> OptionalMissingItems
    {
        get
        {
            var missing = new List<string>();
            if (!HasSpecsDirectory) missing.Add("specs/");
            if (!HasPromptsDirectory) missing.Add("prompts/");
            return missing;
        }
    }
}
