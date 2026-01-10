using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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

        // Use HTTP API for Ollama provider instead of process
        if (_config.ProviderConfig.Provider == AIProvider.Ollama)
        {
            return await ScaffoldFileViaOllamaAsync(fileName, prompt, cancellationToken);
        }

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
    /// Scaffold a file using Ollama HTTP API
    /// </summary>
    private async Task<bool> ScaffoldFileViaOllamaAsync(string fileName, string prompt, CancellationToken cancellationToken)
    {
        var baseUrl = _config.ProviderConfig.ExecutablePath.TrimEnd('/');  // URL stored in ExecutablePath
        var model = _config.ProviderConfig.Arguments;  // Model stored in Arguments

        // Truncate prompt if too long for local models
        // 4096 token context = ~3000 chars max to leave room for response (~1000 tokens)
        const int maxPromptLength = 3000;
        if (prompt.Length > maxPromptLength)
        {
            // Find PROJECT CONTEXT section and truncate it
            var contextMarker = "PROJECT CONTEXT:";
            var contextStart = prompt.IndexOf(contextMarker);
            if (contextStart >= 0)
            {
                var contextEnd = prompt.IndexOf("\n\n", contextStart + contextMarker.Length + 100);
                if (contextEnd > contextStart)
                {
                    var contextContent = prompt.Substring(contextStart, contextEnd - contextStart);
                    var maxContextLen = maxPromptLength - (prompt.Length - contextContent.Length);
                    if (maxContextLen > 500)
                    {
                        var truncatedContext = contextContent.Length > maxContextLen
                            ? contextContent.Substring(0, maxContextLen) + "\n... [truncated for length]"
                            : contextContent;
                        prompt = prompt.Substring(0, contextStart) + truncatedContext + prompt.Substring(contextEnd);
                    }
                }
            }

            // If still too long, hard truncate
            if (prompt.Length > maxPromptLength)
            {
                prompt = prompt.Substring(0, maxPromptLength) + "\n\n... [truncated]";
            }
        }

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            // Build system prompt based on file type - be VERY explicit about format
            string systemPrompt;
            if (fileName == "prompt.md")
            {
                systemPrompt = """
                    You are creating a prompt.md file for an autonomous AI coding agent.

                    CRITICAL: This is NOT a spec or documentation. It's a SHORT instruction file (under 300 words).
                    The prompt tells the AI what to do EACH LOOP ITERATION.

                    YOUR OUTPUT MUST START EXACTLY LIKE THIS:
                    ```
                    Study agents.md for project context.
                    Study specs/* for requirements.
                    Study implementation_plan.md for progress.

                    Choose the most important incomplete task (High Priority first).
                    Implement ONE thing.
                    ```

                    Then add project-specific instructions and the RALPH_STATUS block.

                    DO NOT output JSON. DO NOT output the project spec. Output ONLY the prompt instructions.
                    """;
            }
            else if (fileName == "implementation_plan.md")
            {
                systemPrompt = """
                    You are creating an implementation_plan.md task list for an autonomous AI agent.

                    YOUR OUTPUT MUST START EXACTLY LIKE THIS:
                    ```
                    # Implementation Plan

                    ## Completed
                    - [x] Project initialized

                    ## High Priority
                    - [ ] First critical task
                    ```

                    Create tasks based on the project requirements. Use markdown checkboxes.
                    DO NOT output JSON. Output ONLY the markdown task list.
                    """;
            }
            else if (fileName.EndsWith(".md"))
            {
                systemPrompt = $"""
                    You are generating content for a markdown file named '{fileName}'.

                    CRITICAL FORMAT REQUIREMENTS:
                    - Output valid MARKDOWN content ONLY
                    - Do NOT output JSON
                    - Do NOT wrap output in code blocks
                    - Do NOT add commentary before or after
                    - Start DIRECTLY with markdown (e.g., '# Heading')

                    The output will be saved directly as {fileName}. Begin with the markdown content now.
                    """;
            }
            else
            {
                systemPrompt = $"You are an expert software architect. Generate the content for '{fileName}'. Output ONLY the raw file content with no commentary or code blocks.";
            }

            // Try OpenAI-compatible endpoint first (works with LMStudio and many others)
            var requestBody = new
            {
                model = model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = prompt }
                },
                stream = true,
                temperature = 0.7
            };

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            // Use ResponseHeadersRead to enable streaming (don't buffer the entire response)
            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
            {
                Content = jsonContent
            };
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Fall back to Ollama native endpoint
                var ollamaRequest = new
                {
                    model = model,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = prompt }
                    },
                    stream = true
                };

                jsonContent = new StringContent(
                    JsonSerializer.Serialize(ollamaRequest),
                    Encoding.UTF8,
                    "application/json");

                request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat")
                {
                    Content = jsonContent
                };
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                OnOutput?.Invoke($"[ERROR] API returned {response.StatusCode}: {errorContent}");
                OnScaffoldComplete?.Invoke(fileName, false);
                return false;
            }

            // Parse streaming response
            var content = new StringBuilder();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            var lineCount = 0;
            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                lineCount++;

                if (string.IsNullOrEmpty(line)) continue;

                // Handle SSE format
                if (line.StartsWith("data: "))
                {
                    var data = line.Substring(6);
                    if (data == "[DONE]") break;

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        var root = doc.RootElement;

                        // OpenAI format: choices[0].delta.content
                        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                        {
                            var choice = choices[0];
                            if (choice.TryGetProperty("delta", out var delta) &&
                                delta.TryGetProperty("content", out var textEl))
                            {
                                var text = textEl.GetString();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    content.Append(text);
                                    OnOutput?.Invoke(text);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        OnOutput?.Invoke($"[DEBUG] Parse error: {ex.Message}");
                    }
                }
                else if (line.StartsWith("{"))
                {
                    // Ollama native format: message.content
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("message", out var message) &&
                            message.TryGetProperty("content", out var textEl))
                        {
                            var text = textEl.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                content.Append(text);
                                OnOutput?.Invoke(text);
                            }
                        }
                    }
                    catch { /* Skip unparseable lines */ }
                }
            }

            // Write the generated content to file
            var fileContent = content.ToString().Trim();
            if (string.IsNullOrEmpty(fileContent))
            {
                OnOutput?.Invoke("[ERROR] No content generated");
                OnScaffoldComplete?.Invoke(fileName, false);
                return false;
            }

            // Remove markdown code blocks if present
            fileContent = StripMarkdownCodeBlocks(fileContent);

            // Detect if model output JSON instead of markdown for .md files
            if (fileName.EndsWith(".md") && (fileContent.TrimStart().StartsWith("{") || fileContent.TrimStart().StartsWith("[")))
            {
                OnOutput?.Invoke("\n[WARNING] Model output JSON instead of markdown. This is a known issue with code-focused models.");
                OnOutput?.Invoke("[TIP] Try using a general-purpose model, or create scaffold files manually.");
                OnOutput?.Invoke("[TIP] You can also use --fresh and choose 'Create default template files' option.");
                OnScaffoldComplete?.Invoke(fileName, false);
                return false;
            }

            // Detect if model just echoed back the spec instead of creating the requested file
            // This happens when code-focused models don't follow meta-instructions
            var isScaffoldFile = fileName == "prompt.md" || fileName == "implementation_plan.md" || fileName == "agents.md";
            var looksLikeSpec = (fileContent.Contains("## Overview") && fileContent.Contains("## Architecture")) ||
                               (fileContent.Contains("MCP (Model Context Protocol)") || fileContent.Contains("server that provides AI agents"));
            var notProperFormat = fileName == "prompt.md" && !fileContent.Contains("Study agents.md") && !fileContent.Contains("Study implementation_plan.md");

            if (isScaffoldFile && (looksLikeSpec || notProperFormat))
            {
                OnOutput?.Invoke($"\n[WARNING] Model echoed the spec instead of creating {fileName}.");
                OnOutput?.Invoke("[ISSUE] Code-focused models (like qwen-coder) often struggle with meta-instructions.");
                OnOutput?.Invoke("[TIP] Use a general-purpose model for scaffolding, or choose 'Create default template files'.");
                OnOutput?.Invoke("[TIP] You can then manually edit the generated templates.");
                OnScaffoldComplete?.Invoke(fileName, false);
                return false;
            }

            // Determine the full path
            var fullPath = fileName.Contains('/')
                ? Path.Combine(_config.TargetDirectory, fileName.TrimStart('/'))
                : Path.Combine(_config.TargetDirectory, fileName);

            // Create directory if needed
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(fullPath, fileContent, cancellationToken);
            OnOutput?.Invoke($"\n[SUCCESS] Created {fileName}");
            OnScaffoldComplete?.Invoke(fileName, true);
            return true;
        }
        catch (Exception ex)
        {
            OnOutput?.Invoke($"[EXCEPTION] {ex.Message}");
            OnScaffoldComplete?.Invoke(fileName, false);
            return false;
        }
    }

    /// <summary>
    /// Strip markdown code blocks from generated content
    /// </summary>
    private static string StripMarkdownCodeBlocks(string content)
    {
        var lines = content.Split('\n').ToList();

        // Remove leading code block marker
        if (lines.Count > 0 && lines[0].StartsWith("```"))
        {
            lines.RemoveAt(0);
        }

        // Remove trailing code block marker
        if (lines.Count > 0 && lines[^1].Trim() == "```")
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
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
        ## Context Loading
        1. Read agents.md for project context and build commands
        2. Read specs/* for requirements
        3. Read implementation_plan.md for current progress

        ## Task Execution
        1. Choose the FIRST incomplete High Priority task
        2. Implement ONE thing completely
        3. Run tests/build to verify
        4. Update implementation_plan.md (mark complete, add notes)

        ## Git Commits - MANDATORY
        After EVERY successful change:
        ```bash
        git add -A && git commit -m "Description of change"
        ```
        DO NOT skip commits. Commit before moving to next task.

        ## Error Handling
        - If file not found: use list_directory or glob to find it
        - If command fails: try different approach, don't repeat same error
        - If stuck after 3 attempts: mark task as blocked, move on

        ## Rules
        - Search before implementing (don't assume not implemented)
        - Read files before editing
        - One task per iteration
        - No placeholders or TODOs - complete implementations only

        ## Status Reporting
        End response with:
        ```
        ---RALPH_STATUS---
        STATUS: IN_PROGRESS | COMPLETE | BLOCKED
        TASKS_COMPLETED: <number>
        FILES_MODIFIED: <number>
        TESTS_PASSED: true | false
        EXIT_SIGNAL: true | false
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
