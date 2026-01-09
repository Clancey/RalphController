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

    /// <summary>
    /// When true, overwrites existing files during scaffolding.
    /// Used for re-initializing a project with a new spec.
    /// </summary>
    public bool ForceOverwrite { get; set; }

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

        if (structure.IsComplete && !ForceOverwrite)
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
        if (!structure.HasAgentsMd || ForceOverwrite)
        {
            success &= await ScaffoldFileAsync("agents.md", ScaffoldPrompts.GetAgentsMdPrompt(context), cancellationToken);
        }

        // Generate specs files
        if (!structure.HasSpecsDirectory || !Directory.EnumerateFiles(_config.SpecsDirectoryPath, "*.md").Any() || ForceOverwrite)
        {
            success &= await ScaffoldFileAsync("specs/", ScaffoldPrompts.GetSpecsDirectoryPrompt(context), cancellationToken);
        }

        // Generate prompt.md
        if (!structure.HasPromptMd || ForceOverwrite)
        {
            success &= await ScaffoldFileAsync("prompt.md", ScaffoldPrompts.GetPromptMdPrompt(context), cancellationToken);
        }

        // Generate implementation_plan.md
        if (!structure.HasImplementationPlan || ForceOverwrite)
        {
            success &= await ScaffoldFileAsync("implementation_plan.md", ScaffoldPrompts.GetImplementationPlanPrompt(context), cancellationToken);
        }

        return success;
    }

    /// <summary>
    /// Scaffold all project files using AI (overwrites existing files)
    /// </summary>
    public async Task<bool> ScaffoldAllAsync(CancellationToken cancellationToken = default)
    {
        ForceOverwrite = true;
        return await ScaffoldMissingAsync(cancellationToken);
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
        # Agent Configuration

        ## Core Principle: Monolithic Scheduler with Subagents

        This project uses a single autonomous agent running in a bash loop. The primary context window operates as a **scheduler** that spawns subagents for specific tasks. This extends effective context while maintaining a single source of truth.

        ## Agent Architecture

        ```
        ┌─────────────────────────────────────────────────────────────┐
        │                    Primary Agent (Scheduler)                 │
        │                                                              │
        │  - Reads PROMPT.md each loop                                │
        │  - Loads IMPLEMENTATION_PLAN.md                             │
        │  - Selects ONE task per loop                                │
        │  - Spawns subagents for work                                │
        │  - Updates plan after completion                            │
        │  - Commits when tests pass                                  │
        └─────────────────────────────────────────────────────────────┘
                   │                    │                    │
                   ▼                    ▼                    ▼
            ┌─────────────┐     ┌─────────────┐     ┌─────────────┐
            │  Explore    │     │  Implement  │     │  Build/Test │
            │  Subagent   │     │  Subagent   │     │  Subagent   │
            │  (parallel) │     │  (parallel) │     │  (serial)   │
            └─────────────┘     └─────────────┘     └─────────────┘
        ```

        ## Subagent Types

        ### 1. Explore Agent
        **Purpose**: Search and understand the codebase before making changes.
        **Subagent type**: `Explore`
        **Parallelism**: Up to 5 concurrent

        **When to use**:
        - Before implementing ANY feature
        - When unsure if something exists
        - To find related code patterns

        ### 2. Implementation Agent
        **Purpose**: Write and modify code.
        **Subagent type**: Use language-specific (e.g., `csharp-pro`, `python-pro`, `typescript-pro`)
        **Parallelism**: Up to 3 concurrent

        ### 3. Build/Test Agent
        **Purpose**: Run builds and tests to verify changes.
        **Subagent type**: `debugger`
        **CRITICAL**: Only ONE at a time.

        ## Agent Rules

        1. **One Task Per Loop**: Pick ONE task from implementation_plan.md per iteration
        2. **Search Before Implementing**: Always spawn Explore agent before coding
        3. **Parallel Exploration, Serial Building**: Multiple explores OK, but only 1 build/test
        4. **No Placeholders**: Every implementation must be complete
        5. **Tests as Backpressure**: Fix failures before proceeding
        6. **Commit When Green**: Commit after tests pass
        7. **Update the Plan**: Mark tasks complete and add discoveries

        ## Task Selection Algorithm

        1. Read IMPLEMENTATION_PLAN.md
        2. Find first incomplete task (pending/in_progress, no blockers, highest priority)
        3. If blocked, complete blocking task first
        4. Execute selected task
        5. Update plan
        6. Loop

        ## Build Commands

        ```bash
        # Add build commands for this project
        ```

        ## Test Commands

        ```bash
        # Add test commands for this project
        ```

        ## Error Handling

        ### Build Errors
        1. Spawn debugger agent to analyze
        2. Spawn explore agent to find similar working code
        3. Implement fix
        4. Re-run build

        ### Stuck Agent
        If no progress after 3 attempts:
        1. Document blocker in IMPLEMENTATION_PLAN.md
        2. Move to next non-blocked task
        3. Return later with fresh context

        ## See Also

        - [prompt.md](./prompt.md) - Main loop prompt
        - [implementation_plan.md](./implementation_plan.md) - Current task list
        - [specs/](./specs/) - Specifications
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

        Choose the most important incomplete task (High Priority first).
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

        ## Status Reporting
        End every response with:
        ```
        ---RALPH_STATUS---
        STATUS: IN_PROGRESS | COMPLETE | BLOCKED
        TASKS_COMPLETED: <number>
        FILES_MODIFIED: <number>
        TESTS_PASSED: true | false
        EXIT_SIGNAL: true | false (true only when ALL work is done)
        NEXT_STEP: <what to do next>
        ---END_STATUS---
        ```
        """;

    private static string GetDefaultImplementationPlan() => """
        # Implementation Plan

        ## Completed
        - [x] Project initialized

        ## High Priority
        - [ ] Set up project structure and build system
        - [ ] Implement core data structures
        - [ ] Create basic input/output handling

        ## Medium Priority
        - [ ] Add error handling
        - [ ] Implement main features
        - [ ] Add configuration support
        - [ ] Write user documentation

        ## Low Priority
        - [ ] Performance optimization
        - [ ] Additional features
        - [ ] Polish and refinements

        ## Bugs/Issues
        - None

        ## Notes
        - Focus on MVP first, then iterate
        - Test each component before moving on
        """;
}
