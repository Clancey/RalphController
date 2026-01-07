using RalphController.Models;

namespace RalphController;

/// <summary>
/// Scaffolds new Ralph projects by generating required files using AI
/// </summary>
public class ProjectScaffolder
{
    private readonly RalphConfig _config;

    /// <summary>Fired when scaffolding starts for a file</summary>
    public event Action<string>? OnScaffoldStart;

    /// <summary>Fired when scaffolding completes for a file</summary>
    public event Action<string, bool>? OnScaffoldComplete;

    /// <summary>Fired when output is received from AI</summary>
    public event Action<string>? OnOutput;

    /// <summary>
    /// Project description/context provided by the user.
    /// This is included in all scaffolding prompts so the AI understands the project.
    /// </summary>
    public string? ProjectContext { get; set; }

    public ProjectScaffolder(RalphConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Check what project files are missing
    /// </summary>
    public ProjectStructure ValidateProject()
    {
        return ProjectValidator.ValidateProject(_config);
    }

    /// <summary>
    /// Scaffold all missing files using AI
    /// </summary>
    public async Task<bool> ScaffoldMissingAsync(CancellationToken cancellationToken = default)
    {
        var structure = ValidateProject();

        if (structure.IsComplete)
        {
            return true;
        }

        // Ensure we have project context
        var context = ProjectContext ?? "No project description provided. Create generic templates.";

        var success = true;

        // Create specs directory first (doesn't need AI)
        if (!structure.HasSpecsDirectory)
        {
            Directory.CreateDirectory(_config.SpecsDirectoryPath);
        }

        // Generate agents.md
        if (!structure.HasAgentsMd)
        {
            success &= await ScaffoldFileAsync("agents.md", ScaffoldPrompts.GetAgentsMdPrompt(context), cancellationToken);
        }

        // Generate specs files
        if (!structure.HasSpecsDirectory || !Directory.EnumerateFiles(_config.SpecsDirectoryPath, "*.md").Any())
        {
            success &= await ScaffoldFileAsync("specs/", ScaffoldPrompts.GetSpecsDirectoryPrompt(context), cancellationToken);
        }

        // Generate prompt.md
        if (!structure.HasPromptMd)
        {
            success &= await ScaffoldFileAsync("prompt.md", ScaffoldPrompts.GetPromptMdPrompt(context), cancellationToken);
        }

        // Generate implementation_plan.md
        if (!structure.HasImplementationPlan)
        {
            success &= await ScaffoldFileAsync("implementation_plan.md", ScaffoldPrompts.GetImplementationPlanPrompt(context), cancellationToken);
        }

        return success;
    }

    /// <summary>
    /// Scaffold a specific file using AI
    /// </summary>
    public async Task<bool> ScaffoldFileAsync(string fileName, string prompt, CancellationToken cancellationToken = default)
    {
        OnScaffoldStart?.Invoke(fileName);

        var process = new AIProcess(_config);
        process.OnOutput += line => OnOutput?.Invoke(line);
        process.OnError += line => OnOutput?.Invoke($"[ERROR] {line}");

        try
        {
            var result = await process.RunAsync(prompt, cancellationToken);

            if (!result.Success)
            {
                OnOutput?.Invoke($"[FAILED] Exit code: {result.ExitCode}");
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    OnOutput?.Invoke($"[STDERR] {result.Error}");
                }
            }

            OnScaffoldComplete?.Invoke(fileName, result.Success);
            return result.Success;
        }
        catch (Exception ex)
        {
            OnOutput?.Invoke($"[EXCEPTION] {ex.Message}");
            OnScaffoldComplete?.Invoke(fileName, false);
            return false;
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Create default files without AI (minimal templates)
    /// </summary>
    public async Task CreateDefaultFilesAsync()
    {
        var structure = ValidateProject();

        // Create specs directory
        if (!structure.HasSpecsDirectory)
        {
            Directory.CreateDirectory(_config.SpecsDirectoryPath);
        }

        // Create agents.md with minimal template
        if (!structure.HasAgentsMd)
        {
            await File.WriteAllTextAsync(_config.AgentsFilePath, GetDefaultAgentsMd());
        }

        // Create specs README
        var specsReadme = Path.Combine(_config.SpecsDirectoryPath, "README.md");
        if (!File.Exists(specsReadme))
        {
            await File.WriteAllTextAsync(specsReadme, GetDefaultSpecsReadme());
        }

        // Create prompt.md with minimal template
        if (!structure.HasPromptMd)
        {
            await File.WriteAllTextAsync(_config.PromptFilePath, GetDefaultPromptMd());
        }

        // Create implementation_plan.md with minimal template
        if (!structure.HasImplementationPlan)
        {
            await File.WriteAllTextAsync(_config.PlanFilePath, GetDefaultImplementationPlan());
        }
    }

    private static string GetDefaultAgentsMd() => """
        # Agent Notes

        This file contains learnings and notes for the AI agent. Update this file when you discover:
        - How to build/test the project
        - Common errors and solutions
        - Project-specific patterns

        ## Build Commands

        ```bash
        # Add build commands here
        ```

        ## Test Commands

        ```bash
        # Add test commands here
        ```

        ## Common Issues

        - None documented yet

        ## Learnings

        - None documented yet
        """;

    private static string GetDefaultSpecsReadme() => """
        # Specifications

        This directory contains specification files that describe what needs to be built.

        ## Writing Specs

        Each spec file should include:
        1. **Purpose**: What the feature/component does
        2. **Requirements**: Technical requirements and constraints
        3. **Acceptance Criteria**: How to verify it's complete

        ## Example

        See `example.md` for a template.
        """;

    private static string GetDefaultPromptMd() => """
        Study agents.md for project context.
        Study specs/* for requirements.
        Study implementation_plan.md for progress.

        Choose the most important incomplete task.
        Implement ONE thing.
        Run tests after changes.
        Update implementation_plan.md with progress.
        Commit on success.

        Don't assume not implemented - search first.

        ## Agent Usage
        - Use Task tool to spawn agents for parallel work (research, exploring, generating code)
        - Spawn multiple agents when tasks are independent
        - NEVER run builds/tests in parallel - one at a time only
        - Use agents liberally for reading, exploring, and generating
        """;

    private static string GetDefaultImplementationPlan() => """
        # Implementation Plan

        ## Completed
        - None yet

        ## In Progress
        - None

        ## Pending
        - [ ] Initial setup
        - [ ] Define project structure
        - [ ] Implement core features

        ## Bugs/Issues
        - None

        ## Notes
        - Project initialized
        """;
}
