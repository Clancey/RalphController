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
        HOW the agent should operate, the workflow, and project-specific rules.

        CRITICAL: This is NOT just a notes file. It's the agent's operating manual.
        IMPORTANT: Detect the project type from context and fill in REAL build/test commands — never leave placeholders.

        ## Required Structure:

        ### 1. Core Principle
        Start with a "Core Principle" section:
        - One task at a time - pick ONE incomplete task from the plan, complete it, verify it works
        - ALL tasks must be completed - not just high priority ones
        - Verification workflow - mark work `[?]` after completion, next iteration verifies before `[x]`
        - Commit often - after every successful change

        ### 2. Build Commands
        Include actual build/test commands for this project type:
        ```bash
        # Detect from project files and fill in real commands
        ```

        ### 3. Test Commands
        ```bash
        # Detect from project files and fill in real commands
        ```

        ### 4. Error Handling
        Describe how to handle:
        - Build errors: Try to fix, if stuck 3 times mark as blocked and move on
        - Test failures: Fix before proceeding
        - File not found: Use glob or list_directory to find it

        ### 5. Task Selection
        ```
        1. Read IMPLEMENTATION_PLAN.md
        2. Find an incomplete `[ ]` task (ALL tasks must be completed)
        3. If blocked, complete blocking task first
        4. Execute selected task
        5. Update plan
        6. Loop until ALL tasks are `[x]` complete
        ```

        ### 6. Project-Specific Rules
        Add any rules specific to this project type, e.g.:
        - "Do not edit generated code" for code generators
        - "Run tests in Docker" for containerized projects
        - "Use specific branches" for multi-environment setups

        ## Important Guidelines:
        - Be SPECIFIC to this project type (detect language/framework from context)
        - Include real commands for this project type (not placeholders)
        - Keep practical and actionable
        - Keep under 100 lines if possible

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

        This prompt is read every iteration. It should be CONCISE (under 400 words is ideal).
        Less is more - a simple prompt outperforms a complex one.

        The prompt MUST include these sections:

        ## Task Status Legend
        Include this legend at the top:
        - `[ ]` - Incomplete (not started or needs rework)
        - `[?]` - Waiting to be verified (work done, needs verification by different agent)
        - `[x]` - Complete (verified by a second agent)

        ## Context Loading
        - Read agents.md for project context and build commands
        - Read specs/* for requirements
        - Read implementation_plan.md for progress

        ## Task Execution - VERIFICATION FIRST
        Include TWO subsections:

        ### If you see a `[?]` task (Waiting Verification):
        - PRIORITY: Verify these FIRST before starting new work
        - Review the implementation, check code exists, compiles, meets requirements
        - Run tests
        - If VERIFIED: Mark as `[x]` and commit
        - If INCOMPLETE: Mark as `[ ]` with a note explaining what's missing

        ### If no `[?]` tasks exist:
        - Choose an incomplete `[ ]` task (ALL tasks must be completed, not just high priority)
        - Implement ONE thing completely
        - When done, mark as `[?]` (NOT `[x]`) - another agent must verify
        - Commit your work

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
        - **NEVER mark your own work as `[x]` complete - only mark as `[?]` for verification**
        - **Only mark tasks `[x]` when verifying ANOTHER agent's `[?]` work**

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
        - All items in implementation_plan.md are `[x]` complete (verified)
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

        This file uses a VERIFICATION WORKFLOW where one agent does work and another verifies it.

        Use this exact structure:

        ```markdown
        # Implementation Plan

        ## Status Legend
        - `[ ]` - Incomplete (not started or needs rework)
        - `[?]` - Waiting to be verified (work done, needs verification by different agent)
        - `[x]` - Complete (verified by a second agent)

        ---

        ## Verified Complete
        - [x] Project initialized

        ## Waiting Verification
        (Tasks here have been implemented but need another agent to verify)

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
        4. The Verified Complete section should only contain "Project initialized"
        5. The Waiting Verification section should be empty initially
        6. All spec requirements should appear as incomplete `[ ]` tasks

        VERIFICATION WORKFLOW:
        - When an agent completes work, they mark it `[?]` (waiting verification)
        - The NEXT agent iteration verifies the work
        - If verified good: mark `[x]` and move to Verified Complete
        - If not complete: mark `[ ]` with a note and leave in priority section

        This ensures no single agent can claim their own work is complete.

        Keep items brief - one line each. This file is the agent's memory across iterations.

        Write the file to implementation_plan.md
        """;
}
