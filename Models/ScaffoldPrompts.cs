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

        Create an agents.md file for this project.

        This file is the agent's self-improvement notes. When the agent learns something new about:
        - How to build/run/test the project
        - Common errors and their solutions
        - Project-specific commands or patterns

        The agent should update this file to help future iterations.

        Requirements:
        1. Start with a brief project description based on the context above
        2. Include sections for: Build Commands, Test Commands, Common Issues, Learnings
        3. Pre-fill any build/test commands you can infer from the project type
        4. Keep it brief - this file is read every loop iteration
        5. Use markdown format

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

        The prompt should instruct the agent to:
        1. Study agents.md to learn project context
        2. Study specs/* for requirements
        3. Study implementation_plan.md for current progress
        4. Choose the most important incomplete task
        5. Implement ONE thing per iteration
        6. Run tests after changes
        7. Update implementation_plan.md with progress
        8. Commit on success

        Key Ralph principles to include:
        - "Choose the most important thing"
        - "Don't assume not implemented - search first"
        - "After implementing, run tests for that unit"
        - "Update the plan with learnings"

        IMPORTANT - Agent usage instructions to include:
        - "Use the Task tool to spawn agents for parallel work (research, code generation, file exploration)"
        - "Spawn multiple agents in parallel when tasks are independent"
        - "NEVER run builds or tests in parallel - only one build/test at a time to avoid conflicts"
        - "Wait for build/test results before spawning new build/test tasks"
        - "Use agents liberally for: reading docs, exploring code, generating boilerplate, writing tests"

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

        Create an implementation_plan.md file - the TODO list for the project.

        This file tracks:
        - What has been completed [x]
        - What is in progress [ ] <- CURRENT
        - What is pending [ ]
        - Bugs discovered during implementation
        - Items that need investigation

        The agent updates this file every iteration to:
        - Mark completed items
        - Add new items discovered during work
        - Note blockers or issues

        Based on the project context, create an initial plan with:
        1. A "Completed" section (empty or with any existing work)
        2. A "In Progress" section (empty)
        3. A "Pending" section with actual tasks derived from the project description
        4. A "Bugs/Issues" section (empty)
        5. A "Notes" section for learnings

        Keep items brief - one line each. This file is the agent's memory across iterations.
        Make the pending tasks specific and actionable based on what you know about the project.

        Write the file to implementation_plan.md
        """;
}
