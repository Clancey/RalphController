namespace RalphController.Models;

/// <summary>
/// Contains prompt templates for scaffolding new Ralph projects.
/// These prompts follow the Ralph Wiggum principles - concise, focused, one task at a time.
/// </summary>
public static class ScaffoldPrompts
{
    /// <summary>
    /// Generates the prompt for creating agents.md
    /// </summary>
    public static string GetAgentsMdPrompt(string projectContext) => $"""
        You are setting up a new project for autonomous AI development.

        PROJECT CONTEXT:
        {projectContext}

        Create an AGENTS.md file for this project. This is the operational guide for the AI agent - it defines
        HOW the agent should operate, what subagents to spawn, rules for operation, and workflows.

        CRITICAL: This is NOT just a notes file. It's the agent's operating manual.

        ## Required Structure:

        ### 1. Core Principle Section
        Start with a "Core Principle" section explaining the monolithic scheduler pattern:
        - The primary context window operates as a SCHEDULER that spawns subagents
        - Subagents handle specific tasks (exploration, implementation, testing)
        - This extends effective context while maintaining a single source of truth

        ### 2. Agent Architecture Diagram
        Include an ASCII diagram showing:
        ```
        ┌─────────────────────────────────────────────────────────────┐
        │                    Primary Agent (Scheduler)                 │
        │  - Reads PROMPT.md each loop                                │
        │  - Loads IMPLEMENTATION_PLAN.md                             │
        │  - Selects ONE task per loop                                │
        │  - Spawns subagents for work                                │
        │  - Updates plan after completion                            │
        └─────────────────────────────────────────────────────────────┘
                   │                    │                    │
                   ▼                    ▼                    ▼
            ┌─────────────┐     ┌─────────────┐     ┌─────────────┐
            │  Explore    │     │  Implement  │     │  Build/Test │
            │  Subagent   │     │  Subagent   │     │  Subagent   │
            │  (parallel) │     │  (parallel) │     │  (serial)   │
            └─────────────┘     └─────────────┘     └─────────────┘
        ```

        ### 3. Subagent Types Section
        Define 4-6 subagent types relevant to this specific project. For each:
        - **Purpose**: What this subagent does
        - **Subagent type**: Which Task tool subagent_type to use (e.g., "Explore", "csharp-pro", "python-pro", etc.)
        - **When to use**: Specific scenarios
        - **Parallelism**: How many can run concurrently
        - **Key files**: Files this subagent typically works with
        - **Example tasks**: 3-5 example task descriptions

        Common subagent types to consider:
        - Explore Agent (always include - for searching/understanding codebase)
        - Code Implementation Agent (language-specific: csharp-pro, python-pro, typescript-pro, etc.)
        - Build/Test Agent (debugger - always serial, only one at a time)
        - Documentation Agent (if applicable)
        - Domain-specific agents based on project type

        ### 4. Agent Rules Section
        Include these core rules (numbered):
        1. **One Task Per Loop**: Pick ONE task from implementation_plan.md per iteration
        2. **Search Before Implementing**: Always spawn Explore agent before coding
        3. **Parallel Exploration, Serial Building**: Up to 5 parallel explore/implement, but only 1 build/test
        4. **No Placeholders**: Every implementation must be complete and functional
        5. **Tests as Backpressure**: Fix failures before proceeding
        6. **Commit When Green**: Commit after tests pass
        7. **Update the Plan**: Mark tasks complete and add new discoveries

        Add project-specific rules if applicable (e.g., "Do not edit generated code" for code generators).

        ### 5. Task Selection Algorithm
        Include a numbered algorithm:
        ```
        1. Read IMPLEMENTATION_PLAN.md
        2. Find first incomplete task (pending/in_progress, no blockers, highest priority)
        3. If blocked, complete blocking task first
        4. Execute selected task
        5. Update plan
        6. Loop
        ```

        ### 6. Subagent Communication Section
        Show how to spawn subagents using Task tool:
        ```
        Use Task tool with appropriate subagent_type:
        - subagent_type: "Explore" - For searching/understanding
        - subagent_type: "[language]-pro" - For implementation
        - subagent_type: "debugger" - For build/test issues
        ```

        Include a prompt template example.

        ### 7. Error Handling Section
        Describe how to handle:
        - Build errors
        - Test failures
        - Stuck agent (no progress after 3 attempts)

        ### 8. Context Management Section
        Describe what the primary agent and subagents should load.

        ### 9. Build Commands Section
        Include actual build/test commands for this project type.

        ### 10. Example Workflow
        Show a complete example loop iteration.

        ### 11. See Also Section
        Link to related files (PROMPT.md, IMPLEMENTATION_PLAN.md, etc.)

        ## Important Guidelines:
        - Be SPECIFIC to this project type (detect language/framework from context)
        - Use the correct subagent_type values that exist in Claude Code's Task tool
        - Make the ASCII diagram fit the project's specific workflow
        - Include real commands for this project type (not placeholders)
        - Keep practical and actionable

        Write the file to agents.md
        """;

    /// <summary>
    /// Generates the prompt for creating the specs directory
    /// </summary>
    public static string GetSpecsDirectoryPrompt(string projectContext) => $"""
        You are setting up a new project for autonomous AI development.

        PROJECT CONTEXT:
        {projectContext}

        Create a specs/ directory with initial specification files.

        Specs are the source of truth for what needs to be built. Each spec file describes:
        - What the feature/component should do
        - Technical requirements
        - Acceptance criteria

        Based on the project context, create:
        1. specs/README.md - explaining how to write specs for this project
        2. specs/overview.md - a high-level spec describing the project goals and architecture
        3. Any additional spec files for major features you can identify

        The specs should be detailed enough that an agent can implement from them,
        but concise enough to fit in context. Follow the principle: specs + stdlib = generate.
        """;

    /// <summary>
    /// Generates the prompt for creating prompt.md
    /// </summary>
    public static string GetPromptMdPrompt(string projectContext) => $"""
        You are setting up a new project for autonomous AI development.

        PROJECT CONTEXT:
        {projectContext}

        Create a prompt.md file - the main instruction file for the Ralph loop.

        This prompt is read every iteration. It should be CONCISE (under 300 words is ideal).
        Less is more - a simple prompt outperforms a complex one.

        The prompt MUST include these sections:

        ## Context Loading
        - Read agents.md for project context and build commands
        - Read specs/* for requirements
        - Read implementation_plan.md for progress

        ## Task Execution
        - Choose FIRST incomplete High Priority task
        - Implement ONE thing completely
        - Run tests/build to verify
        - Update implementation_plan.md

        ## Git Commits - MANDATORY
        Include this exact instruction:
        "After EVERY successful change: git add -A && git commit -m 'Description'"
        Emphasize: DO NOT skip commits.

        ## Error Handling
        - If file not found: use list_directory or glob
        - If command fails: try different approach
        - If stuck 3 times: mark blocked, move on

        ## Rules
        - Search before implementing
        - Read files before editing
        - One task per iteration
        - No placeholders or TODOs

        CRITICAL - Status reporting requirement:
        Every response MUST end with a RALPH_STATUS block in this exact format:
        ```
        ---RALPH_STATUS---
        STATUS: IN_PROGRESS | COMPLETE | BLOCKED
        TASKS_COMPLETED: <number>
        FILES_MODIFIED: <number>
        TESTS_PASSED: true | false
        EXIT_SIGNAL: true | false
        NEXT_STEP: <brief description of next action>
        ---END_STATUS---
        ```

        EXIT_SIGNAL should be true ONLY when:
        - All items in implementation_plan.md are complete
        - All tests pass
        - No errors exist
        - Specifications are fully implemented

        Customize the prompt for this specific project type based on the context.

        Write the file to prompt.md
        """;

    /// <summary>
    /// Generates the prompt for creating implementation_plan.md
    /// </summary>
    public static string GetImplementationPlanPrompt(string projectContext) => $"""
        You are setting up a new project for autonomous AI development.

        PROJECT CONTEXT:
        {projectContext}

        IMPORTANT: First, read the specs/* directory to understand what needs to be built.
        The implementation plan MUST be derived from the current specs, not from assumptions.

        Create an implementation_plan.md file - the TODO list for the project.

        This file tracks work across THREE priority levels:
        - HIGH PRIORITY: Foundation work, blocking items, critical path
        - MEDIUM PRIORITY: Core features, quality improvements, important functionality
        - LOW PRIORITY: Nice-to-haves, optimizations, polish

        Use this exact structure:

        ```markdown
        # Implementation Plan

        ## Completed
        - [x] Project initialized

        ## High Priority
        - [ ] Critical/blocking task
        - [ ] Foundation work

        ## Medium Priority
        - [ ] Core feature
        - [ ] Important functionality

        ## Low Priority
        - [ ] Nice-to-have
        - [ ] Optimization

        ## Bugs/Issues
        - None

        ## Notes
        - Project learnings go here
        ```

        CRITICAL INSTRUCTIONS:
        1. Read specs/* to understand all requirements
        2. Create tasks that map to spec requirements
        3. Start fresh - do NOT preserve old completed items
        4. The Completed section should only contain "Project initialized"
        5. All spec requirements should appear as incomplete tasks in the appropriate priority

        The agent updates this file every iteration to:
        - Mark completed items with [x]
        - Move items between priority levels as needed
        - Add new items discovered during work
        - Note blockers or issues

        Keep items brief - one line each. This file is the agent's memory across iterations.

        Write the file to implementation_plan.md
        """;
}
