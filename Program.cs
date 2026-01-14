using System.Diagnostics;
using System.Text;
using RalphController;
using RalphController.Models;
using RalphController.Workflow;
using Spectre.Console;

// Set console encoding to UTF-8 for proper Unicode character support on Windows
Console.OutputEncoding = Encoding.UTF8;

static string? NormalizeOpenCodeModel(string? model)
{
    if (string.IsNullOrWhiteSpace(model))
    {
        return null;
    }

    // If it already has provider, use as is
    if (model.Contains('/'))
    {
        return model;
    }

    // Known OpenCode models (without provider prefix)
    var openCodeModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "big-pickle", "glm-4.7-free", "gpt-5-nano", "grok-code", "minimax-m2.1-free"
    };

    if (openCodeModels.Contains(model))
    {
        return $"opencode/{model}";
    }

    // If it has a tag (like :8b, :70b), it's likely an Ollama model
    if (model.Contains(':'))
    {
        return $"ollama/{model}";
    }

    // Default to opencode provider for unrecognized models
    return $"opencode/{model}";
}

static Task<List<string>> GetClaudeModels()
{
    // Claude CLI uses --model with aliases or full model names
    // Aliases: sonnet, opus, haiku (resolve to latest versions)
    // Full names: claude-sonnet-4-5-20250929, etc.
    var models = new List<string>
    {
        "sonnet",           // Latest Sonnet (recommended for most tasks)
        "opus",             // Latest Opus (most capable)
        "haiku",            // Latest Haiku (fastest)
        "claude-sonnet-4",  // Claude 4 Sonnet
        "claude-opus-4"     // Claude 4 Opus
    };
    return Task.FromResult(models);
}

static async Task<List<string>> GetCodexModels()
{
    // Known Codex models from https://developers.openai.com/codex/models
    var knownCodexModels = new List<string>
    {
        "gpt-5.2-codex",      // Most advanced agentic coding model
        "gpt-5.1-codex-max",
        "gpt-5.1-codex-mini",
        "gpt-5.2",
        "gpt-5.1",
        "gpt-5.1-codex",
        "gpt-5-codex",
        "gpt-5-codex-mini",
        "gpt-5"
    };

    // Try to fetch models from OpenAI API and filter to relevant ones
    try
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            var response = await client.GetStringAsync("https://api.openai.com/v1/models");
            using var doc = System.Text.Json.JsonDocument.Parse(response);

            if (doc.RootElement.TryGetProperty("data", out var dataArray))
            {
                var apiModels = new List<string>();
                foreach (var model in dataArray.EnumerateArray())
                {
                    if (model.TryGetProperty("id", out var idElement))
                    {
                        var id = idElement.GetString();
                        // Only include gpt-5, gpt-4, o3, o4, or codex models
                        if (!string.IsNullOrEmpty(id) &&
                            (id.StartsWith("gpt-5") || id.StartsWith("gpt-4") ||
                             id.StartsWith("o3") || id.StartsWith("o4") ||
                             id.Contains("codex")))
                        {
                            apiModels.Add(id);
                        }
                    }
                }
                if (apiModels.Count > 0)
                {
                    // Merge with known models (in case API is missing some)
                    var allModels = new HashSet<string>(knownCodexModels);
                    allModels.UnionWith(apiModels);
                    var result = allModels.ToList();
                    result.Sort();
                    return result;
                }
            }
        }
    }
    catch { /* Fall through to defaults */ }

    return knownCodexModels;
}

static Task<List<string>> GetGeminiModels()
{
    // Gemini CLI uses -m flag for model selection
    // Common models: gemini-2.5-pro, gemini-2.5-flash, etc.
    var models = new List<string>
    {
        "gemini-2.5-pro",       // Latest Pro (recommended)
        "gemini-2.5-flash",     // Fast model
        "gemini-2.0-flash",     // Previous flash
        "gemini-2.0-pro",       // Previous pro
        "gemini-1.5-pro"        // Stable pro
    };
    return Task.FromResult(models);
}

static Task<List<string>> GetCursorModels()
{
    // Cursor supports various models through its agent mode
    var models = new List<string>
    {
        "claude-sonnet",        // Claude Sonnet (recommended)
        "claude-opus",          // Claude Opus
        "gpt-4",                // GPT-4
        "gpt-4-turbo",          // GPT-4 Turbo
        "gpt-4o",               // GPT-4o
        "cursor-small"          // Cursor's own small model
    };
    return Task.FromResult(models);
}

static Task<List<string>> GetCopilotModels()
{
    // GitHub Copilot supports various models via Copilot Chat
    // See: https://docs.github.com/en/copilot/reference/ai-models/model-comparison
    var models = new List<string>
    {
        // OpenAI models
        "gpt-4.1",              // GPT-4.1
        "gpt-5",                // GPT-5
        "gpt-5-codex",          // GPT-5 Codex
        "gpt-5-mini",           // GPT-5 mini
        "gpt-5.1",              // GPT-5.1
        "gpt-5.1-codex",        // GPT-5.1 Codex
        "gpt-5.1-codex-max",    // GPT-5.1 Codex Max
        "gpt-5.1-codex-mini",   // GPT-5.1 Codex Mini
        "gpt-5.2",              // GPT-5.2
        // Anthropic models
        "claude-haiku-4.5",     // Claude Haiku 4.5
        "claude-opus-4.1",      // Claude Opus 4.1
        "claude-opus-4.5",      // Claude Opus 4.5
        "claude-sonnet-4.0",    // Claude Sonnet 4.0
        "claude-sonnet-4.5",    // Claude Sonnet 4.5
        // Google models
        "gemini-2.5-pro",       // Gemini 2.5 Pro
        "gemini-3-flash",       // Gemini 3 Flash
        "gemini-3-pro",         // Gemini 3 Pro
        // Other models
        "grok-code-fast-1",     // Grok Code Fast 1
        "qwen2.5",              // Qwen 2.5
        "raptor-mini"           // Raptor mini
    };
    return Task.FromResult(models);
}

static bool IsProviderInstalled(AIProvider provider)
{
    var command = provider switch
    {
        AIProvider.Claude => "claude",
        AIProvider.Codex => "codex",
        AIProvider.Copilot => "copilot",
        AIProvider.Gemini => "gemini",
        AIProvider.Cursor => "cursor",
        AIProvider.OpenCode => "opencode",
        AIProvider.Ollama => null,  // Ollama uses HTTP, not CLI - always available
        _ => null
    };

    if (command == null) return true;  // No CLI needed

    try
    {
        ProcessStartInfo psi;
        
        if (OperatingSystem.IsWindows())
        {
            // Windows: use PowerShell and Get-Command
            psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Get-Command {command} -ErrorAction SilentlyContinue\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        else
        {
            // Unix/Linux/macOS: use bash and which command
            psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"which {command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
    }
    catch
    {
        return false;
    }
}

static List<AIProvider> GetInstalledProviders()
{
    var providers = new List<AIProvider>();
    foreach (AIProvider p in Enum.GetValues<AIProvider>())
    {
        if (IsProviderInstalled(p))
        {
            providers.Add(p);
        }
    }
    return providers;
}

static async Task<List<string>> GetOpenCodeModels()
{
    ProcessStartInfo psi;

    if (OperatingSystem.IsWindows())
    {
        psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c opencode models",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }
    else
    {
        psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = "-c \"opencode models\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    using var process = new Process { StartInfo = psi };
    process.Start();
    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    if (process.ExitCode == 0)
    {
        var models = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                           .Select(m => m.Trim())
                           .Where(m => !string.IsNullOrEmpty(m))
                           .ToList();
        // Sort alphabetically
        models.Sort();
        return models;
    }

    // Fallback defaults
    return new List<string> { "ollama/llama3.1:70b", "lmstudio/qwen/qwen3-coder-30b" };
}

static async Task<List<string>> GetOllamaModels(string baseUrl)
{
    var models = new List<string>();
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    var trimmedUrl = baseUrl.TrimEnd('/');

    // Try Ollama endpoint first: /api/tags
    try
    {
        var response = await client.GetStringAsync($"{trimmedUrl}/api/tags");
        using var doc = System.Text.Json.JsonDocument.Parse(response);

        if (doc.RootElement.TryGetProperty("models", out var modelsArray))
        {
            foreach (var model in modelsArray.EnumerateArray())
            {
                if (model.TryGetProperty("name", out var nameElement))
                {
                    var name = nameElement.GetString();
                    if (!string.IsNullOrEmpty(name))
                        models.Add(name);
                }
            }
        }

        if (models.Count > 0)
        {
            models.Sort();
            return models;
        }
    }
    catch { /* Try OpenAI endpoint */ }

    // Try OpenAI-compatible endpoint (LM Studio): /v1/models
    try
    {
        var response = await client.GetStringAsync($"{trimmedUrl}/v1/models");
        using var doc = System.Text.Json.JsonDocument.Parse(response);

        if (doc.RootElement.TryGetProperty("data", out var dataArray))
        {
            foreach (var model in dataArray.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var idElement))
                {
                    var id = idElement.GetString();
                    if (!string.IsNullOrEmpty(id))
                        models.Add(id);
                }
            }
        }

        models.Sort();
        return models;
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[dim]Could not fetch models from server: {ex.Message}[/]");
        return new List<string>();
    }
}

/// <summary>
/// Interactive manual configuration of agent roles
/// </summary>
static Task ConfigureAgentsManuallyAsync(
    ProjectSettings projectSettings,
    List<ModelSpec> availableModels)
{
    var modelChoices = availableModels.Select(m => m.DisplayName).ToList();
    modelChoices.Insert(0, "(skip - use default)");

    AnsiConsole.MarkupLine("\n[blue]Configure agents for each role.[/]");
    AnsiConsole.MarkupLine("[dim]You can assign multiple models to each role. Same model can be used for multiple roles.[/]\n");

    var allRoles = Enum.GetValues<AgentRole>();

    foreach (var role in allRoles)
    {
        var roleAgents = new List<AgentConfig>();
        var addMore = true;
        var agentNum = 1;

        while (addMore)
        {
            var prompt = agentNum == 1
                ? $"[yellow]{role}[/] - Select model for agent #{agentNum}:"
                : $"[yellow]{role}[/] - Add another agent? (#{agentNum}):";

            var selectedModel = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title(prompt)
                    .PageSize(10)
                    .AddChoices(modelChoices));

            if (selectedModel == "(skip - use default)")
            {
                if (agentNum == 1)
                {
                    // Use default for this role
                    roleAgents.Add(new AgentConfig());
                }
                addMore = false;
            }
            else
            {
                var model = availableModels.FirstOrDefault(m => m.DisplayName == selectedModel);
                if (model != null)
                {
                    roleAgents.Add(new AgentConfig { Model = model });
                    agentNum++;

                    // Ask if they want to add more agents to this role
                    if (agentNum <= 5) // Max 5 agents per role
                    {
                        addMore = AnsiConsole.Confirm($"Add another agent to [cyan]{role}[/]?", false);
                    }
                    else
                    {
                        addMore = false;
                    }
                }
                else
                {
                    addMore = false;
                }
            }
        }

        if (roleAgents.Count > 0)
        {
            projectSettings.Collaboration!.Agents[role] = roleAgents;
            var modelNames = string.Join(", ", roleAgents.Select(a => a.Model?.DisplayName ?? "default"));
            AnsiConsole.MarkupLine($"  [green]✓[/] {role}: {modelNames}");
        }
    }

    AnsiConsole.MarkupLine("\n[green]Agent roles configured![/]");
    return Task.CompletedTask;
}

/// <summary>
/// Assign models to agent roles using simple heuristics
/// Supports multiple agents per role and same model for multiple roles
/// </summary>
static Dictionary<AgentRole, List<AgentConfig>> AssignModelsToRoles(
    List<ModelSpec> availableModels,
    ModelSpec? orchestratorModel)
{
    var assignments = new Dictionary<AgentRole, List<AgentConfig>>();

    if (availableModels.Count == 0) return assignments;

    // Simple heuristics based on model names
    // Larger/pro models for complex tasks, smaller/flash for quick tasks
    var sortedModels = availableModels.OrderByDescending(m =>
    {
        var name = m.Model?.ToLowerInvariant() ?? "";
        // Score models by capability (higher = more capable)
        if (name.Contains("opus") || name.Contains("pro") || name.Contains("70b") || name.Contains("32b"))
            return 100;
        if (name.Contains("sonnet") || name.Contains("13b") || name.Contains("14b") || name.Contains("30b"))
            return 75;
        if (name.Contains("haiku") || name.Contains("flash") || name.Contains("8b") || name.Contains("7b"))
            return 50;
        return 25;
    }).ToList();

    // Assign roles based on model capabilities
    // Same model can be assigned to multiple roles
    var mostCapable = sortedModels.FirstOrDefault();
    var secondMost = sortedModels.Skip(1).FirstOrDefault() ?? mostCapable;
    var thirdMost = sortedModels.Skip(2).FirstOrDefault() ?? secondMost;

    // Planner/Synthesizer: Most capable model (needs to understand full context)
    if (mostCapable != null)
    {
        assignments[AgentRole.Planner] = new List<AgentConfig> { new() { Model = mostCapable } };
        assignments[AgentRole.Synthesizer] = new List<AgentConfig> { new() { Model = mostCapable } };
    }

    // Challenger: Second most capable
    if (secondMost != null)
    {
        assignments[AgentRole.Challenger] = new List<AgentConfig> { new() { Model = secondMost } };
    }

    // Reviewers: Multiple agents if we have multiple models
    var reviewerAgents = new List<AgentConfig>();
    foreach (var model in sortedModels.Take(Math.Min(3, availableModels.Count)))
    {
        reviewerAgents.Add(new AgentConfig { Model = model });
    }
    if (reviewerAgents.Count > 0)
    {
        assignments[AgentRole.Reviewer] = reviewerAgents;
    }

    // Implementer/Advocate: Can use any capable model
    if (thirdMost != null)
    {
        assignments[AgentRole.Implementer] = new List<AgentConfig> { new() { Model = thirdMost } };
        assignments[AgentRole.Advocate] = new List<AgentConfig> { new() { Model = thirdMost } };
    }

    return assignments;
}

// Check for test modes
if (args.Contains("--test-streaming"))
{
    // Test real-time streaming using AIProcess with timestamps
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Starting streaming test with AIProcess...\n");

    var testConfig = new RalphConfig
    {
        TargetDirectory = Directory.GetCurrentDirectory(),
        Provider = AIProvider.Claude,
        ProviderConfig = AIProviderConfig.ForClaude()
    };

    using var aiProcess = new AIProcess(testConfig);

    aiProcess.OnOutput += text =>
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] TEXT: {text}");
    };
    aiProcess.OnError += err =>
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ERR: {err}");
    };

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Sending prompt...\n");

    var result = await aiProcess.RunAsync("Count from 1 to 10, one number per line");

    Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss.fff}] Complete. Exit code: {result.ExitCode}");
    Console.WriteLine($"Full output: {result.Output}");
    return 0;
}

if (args.Contains("--single-run"))
{
    // Run one iteration without TUI to test the full loop
    AnsiConsole.MarkupLine("[yellow]Running single iteration (no TUI)...[/]\n");

    var singleDir = args.FirstOrDefault(a => !a.StartsWith("-")) ?? Directory.GetCurrentDirectory();
    if (!Directory.Exists(singleDir)) singleDir = Directory.GetCurrentDirectory();

    var singleConfig = new RalphConfig
    {
        TargetDirectory = singleDir,
        Provider = AIProvider.Claude,
        ProviderConfig = AIProviderConfig.ForClaude(),
        MaxIterations = 1
    };

    using var singleController = new LoopController(singleConfig);

    singleController.OnOutput += line =>
    {
        var escaped = line.Replace("[", "[[").Replace("]", "]]");
        AnsiConsole.MarkupLine($"[green]OUT:[/] {escaped}");
    };
    singleController.OnError += line =>
    {
        var escaped = line.Replace("[", "[[").Replace("]", "]]");
        AnsiConsole.MarkupLine($"[red]ERR:[/] {escaped}");
    };
    singleController.OnIterationStart += (iter, modelName) =>
    {
        var modelSuffix = modelName != null ? $" [[{Markup.Escape(modelName)}]]" : "";
        AnsiConsole.MarkupLine($"[blue]>>> Starting iteration {iter}{modelSuffix}[/]");
    };
    singleController.OnIterationComplete += (iter, result) =>
    {
        AnsiConsole.MarkupLine($"[blue]<<< Iteration {iter} complete: {(result.Success ? "SUCCESS" : "FAILED")}[/]");
    };

    AnsiConsole.MarkupLine($"[dim]Directory: {singleDir}[/]");
    AnsiConsole.MarkupLine($"[dim]Prompt: {singleConfig.PromptFilePath}[/]\n");

    await singleController.StartAsync();

    AnsiConsole.MarkupLine("\n[green]Done![/]");
    return 0;
}

if (args.Contains("--test-aiprocess"))
{
    AnsiConsole.MarkupLine("[yellow]Testing AIProcess class...[/]\n");

    var testConfig = new RalphConfig
    {
        TargetDirectory = Directory.GetCurrentDirectory(),
        Provider = AIProvider.OpenCode,
        ProviderConfig = AIProviderConfig.ForOpenCode(model: "lmstudio/qwen/qwen3-coder-30b")
    };

    using var aiProcess = new AIProcess(testConfig);

    var outputReceived = false;
    aiProcess.OnOutput += line =>
    {
        outputReceived = true;
        var escaped = line.Replace("[", "[[").Replace("]", "]]");
        AnsiConsole.MarkupLine($"[green]OUTPUT:[/] {escaped}");
    };
    aiProcess.OnError += line =>
    {
        var escaped = line.Replace("[", "[[").Replace("]", "]]");
        AnsiConsole.MarkupLine($"[red]ERROR:[/] {escaped}");
    };
    aiProcess.OnExit += code =>
    {
        AnsiConsole.MarkupLine($"[dim]EXIT:[/] {code}");
    };

    AnsiConsole.MarkupLine("[blue]Running AIProcess.RunAsync('Say hello')...[/]\n");
    var result = await aiProcess.RunAsync("Say hello");

    AnsiConsole.MarkupLine($"\n[yellow]Result:[/]");
    AnsiConsole.MarkupLine($"  Success: {result.Success}");
    AnsiConsole.MarkupLine($"  Exit Code: {result.ExitCode}");
    AnsiConsole.MarkupLine($"  Output length: {result.Output.Length}");
    AnsiConsole.MarkupLine($"  Error length: {result.Error.Length}");
    AnsiConsole.MarkupLine($"  Output received via events: {outputReceived}");

    if (result.Output.Length > 0)
    {
        AnsiConsole.MarkupLine($"\n[yellow]Output content:[/]");
        var escaped = result.Output.Replace("[", "[[").Replace("]", "]]");
        AnsiConsole.MarkupLine(escaped);
    }

    return 0;
}

if (args.Contains("--test-output"))
{
    AnsiConsole.MarkupLine("[yellow]Testing process output capture...[/]\n");

    // Test 1: Simple echo
    AnsiConsole.MarkupLine("[blue]Test 1: Simple echo command[/]");
    ProcessStartInfo psi;

    if (OperatingSystem.IsWindows())
    {
        psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo Hello from Windows",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }
    else
    {
        psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = "-c \"echo 'Hello from bash'\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    using var process = new Process { StartInfo = psi };
    process.OutputDataReceived += (_, e) =>
    {
        if (e.Data != null)
            AnsiConsole.MarkupLine($"  [green]STDOUT:[/] {e.Data}");
    };
    process.ErrorDataReceived += (_, e) =>
    {
        if (e.Data != null)
            AnsiConsole.MarkupLine($"  [red]STDERR:[/] {e.Data}");
    };
    process.Start();
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    await process.WaitForExitAsync();
    AnsiConsole.MarkupLine($"  [dim]Exit code: {process.ExitCode}[/]\n");

    // Test 2: Claude command
    AnsiConsole.MarkupLine("[blue]Test 2: Claude command with temp file[/]");
    var tempFile = Path.GetTempFileName();
    await File.WriteAllTextAsync(tempFile, "Say 'Hello, World!' and nothing else.");

    ProcessStartInfo psi2;

    if (OperatingSystem.IsWindows())
    {
        psi2 = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c claude -p --dangerously-skip-permissions < \"{tempFile}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }
    else
    {
        psi2 = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"claude -p --dangerously-skip-permissions < '{tempFile}'\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    var outputLines = 0;
    var errorLines = 0;

    using var process2 = new Process { StartInfo = psi2 };
    process2.OutputDataReceived += (_, e) =>
    {
        if (e.Data != null)
        {
            outputLines++;
            var escaped = e.Data.Replace("[", "[[").Replace("]", "]]");
            AnsiConsole.MarkupLine($"  [green]STDOUT:[/] {escaped}");
        }
    };
    process2.ErrorDataReceived += (_, e) =>
    {
        if (e.Data != null)
        {
            errorLines++;
            var escaped = e.Data.Replace("[", "[[").Replace("]", "]]");
            AnsiConsole.MarkupLine($"  [red]STDERR:[/] {escaped}");
        }
    };
    process2.Start();
    process2.BeginOutputReadLine();
    process2.BeginErrorReadLine();
    await process2.WaitForExitAsync();
    await Task.Delay(100); // Allow async events to complete
    File.Delete(tempFile);

    AnsiConsole.MarkupLine($"  [dim]Exit code: {process2.ExitCode}, stdout lines: {outputLines}, stderr lines: {errorLines}[/]\n");

    AnsiConsole.MarkupLine("[green]Test complete![/]");
    return 0;
}

// Parse command line arguments
var targetDir = args.Length > 0 && !args[0].StartsWith("-") ? args[0] : null;
AIProvider? providerFromArgs = null;
string? initSpec = null;
var initMode = false;

string? modelFromArgs = null;
string? apiUrlFromArgs = null;

// Define valid arguments
var validArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "--provider", "--model", "--api-url", "--url",
    "--codex", "--copilot", "--claude", "--gemini", "--cursor", "--opencode", "--ollama", "--lmstudio",
    "--list-models", "--fresh", "--init", "--spec", "--no-tui", "--console",
    "--test-streaming", "--single-run", "--test-aiprocess", "--test-output"
};

// Check for flags
var listModels = false;
var freshMode = false;
var noTui = false;
var unknownArgs = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    // Skip positional arguments (directory paths - don't start with -)
    if (!args[i].StartsWith("-"))
    {
        continue;
    }

    // Check if this is a valid argument
    if (!validArgs.Contains(args[i]))
    {
        unknownArgs.Add(args[i]);
        continue;
    }

    if (args[i] == "--provider" && i + 1 < args.Length)
    {
        providerFromArgs = args[i + 1].ToLower() switch
        {
            "codex" => AIProvider.Codex,
            "claude" => AIProvider.Claude,
            "copilot" => AIProvider.Copilot,
            "gemini" => AIProvider.Gemini,
            "cursor" => AIProvider.Cursor,
            "opencode" => AIProvider.OpenCode,
            "ollama" => AIProvider.Ollama,
            _ => null
        };
    }
    else if (args[i] == "--model" && i + 1 < args.Length)
    {
        modelFromArgs = args[i + 1];
        i++;
    }
    else if ((args[i] == "--api-url" || args[i] == "--url") && i + 1 < args.Length)
    {
        apiUrlFromArgs = args[i + 1];
        i++;
    }
    else if (args[i] == "--codex")
    {
        providerFromArgs = AIProvider.Codex;
    }
    else if (args[i] == "--copilot")
    {
        providerFromArgs = AIProvider.Copilot;
    }
    else if (args[i] == "--claude")
    {
        providerFromArgs = AIProvider.Claude;
    }
    else if (args[i] == "--gemini")
    {
        providerFromArgs = AIProvider.Gemini;
    }
    else if (args[i] == "--cursor")
    {
        providerFromArgs = AIProvider.Cursor;
    }
    else if (args[i] == "--opencode")
    {
        providerFromArgs = AIProvider.OpenCode;
    }
    else if (args[i] == "--ollama")
    {
        providerFromArgs = AIProvider.Ollama;
        // URL will be prompted or loaded from saved settings
    }
    else if (args[i] == "--lmstudio")
    {
        providerFromArgs = AIProvider.Ollama; // LMStudio uses same API
        // URL will be prompted or loaded from saved settings
    }
    else if (args[i] == "--list-models")
    {
        listModels = true;
    }
    else if (args[i] == "--fresh")
    {
        freshMode = true;
    }
    else if (args[i] == "--init" || args[i] == "--spec")
    {
        initMode = true;
        // Check if next arg is the spec (not another flag)
        if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
        {
            initSpec = args[i + 1];
            i++; // Skip the spec value in next iteration
        }
    }
    else if (args[i] == "--no-tui" || args[i] == "--console")
    {
        noTui = true;
    }
}

// Check for unknown arguments
if (unknownArgs.Count > 0)
{
    AnsiConsole.MarkupLine($"[red]Error: Unknown argument(s): {string.Join(", ", unknownArgs)}[/]");
    AnsiConsole.MarkupLine("\n[yellow]Valid arguments:[/]");
    AnsiConsole.MarkupLine("  [dim]--provider <name>[/]     Select AI provider (claude, codex, copilot, gemini, cursor, opencode, ollama)");
    AnsiConsole.MarkupLine("  [dim]--model <name>[/]        Select model for the provider");
    AnsiConsole.MarkupLine("  [dim]--api-url <url>[/]       API URL for Ollama/LMStudio");
    AnsiConsole.MarkupLine("  [dim]--fresh[/]               Ignore saved settings, prompt for all options");
    AnsiConsole.MarkupLine("  [dim]--init [[spec]][/]       Initialize/regenerate project files from spec");
    AnsiConsole.MarkupLine("  [dim]--no-tui[/]              Run without TUI (plain console output)");
    AnsiConsole.MarkupLine("  [dim]--list-models[/]         List available models");
    AnsiConsole.MarkupLine("\n[dim]Provider shortcuts: --claude, --codex, --copilot, --gemini, --cursor, --opencode, --ollama, --lmstudio[/]");
    return 1;
}

if (listModels)
{
    AnsiConsole.MarkupLine("[yellow]Listing available models...[/]");

    ProcessStartInfo psi;

    if (OperatingSystem.IsWindows())
    {
        psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c opencode models",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }
    else
    {
        psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = "-c \"opencode models\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    using var process = new Process { StartInfo = psi };
    process.Start();
    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    if (process.ExitCode == 0)
    {
        AnsiConsole.MarkupLine("[green]Available models:[/]");
        AnsiConsole.WriteLine(output);
    }
    else
    {
        AnsiConsole.MarkupLine($"[red]Error listing models: {error}[/]");
    }
    return 0;
}

// Show banner
AnsiConsole.Write(new FigletText("Ralph")
    .LeftJustified()
    .Color(Color.Blue));

// ASCII art mascot
const string mascot = @"
⠀⠀⠀⠀⠀⠀⣀⣤⣶⡶⢛⠟⡿⠻⢻⢿⢶⢦⣄⡀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⢀⣠⡾⡫⢊⠌⡐⢡⠊⢰⠁⡎⠘⡄⢢⠙⡛⡷⢤⡀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⢠⢪⢋⡞⢠⠃⡜⠀⠎⠀⠉⠀⠃⠀⠃⠀⠃⠙⠘⠊⢻⠦⠀⠀⠀⠀⠀⠀
⠀⠀⢇⡇⡜⠀⠜⠀⠁⠀⢀⠔⠉⠉⠑⠄⠀⠀⡰⠊⠉⠑⡄⡇⠀⠀⠀⠀⠀⠀
⠀⠀⡸⠧⠄⠀⠀⠀⠀⠀⠘⡀⠾⠀⠀⣸⠀⠀⢧⠀⠛⠀⠌⡇⠀⠀⠀⠀⠀⠀
⠀⠘⡇⠀⠀⠀⠀⠀⠀⠀⠀⠙⠒⠒⠚⠁⠈⠉⠲⡍⠒⠈⠀⡇⠀⠀⠀⠀⠀⠀
⠀⠀⠈⠲⣆⠀⠀⠀⠀⠀⠀⠀⠀⣠⠖⠉⡹⠤⠶⠁⠀⠀⠀⠈⢦⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠈⣦⡀⠀⠀⠀⠀⠧⣴⠁⠀⠘⠓⢲⣄⣀⣀⣀⡤⠔⠃⠀⠀⠀⠀⠀
⠀⠀⠀⠀⣜⠀⠈⠓⠦⢄⣀⣀⣸⠀⠀⠀⠀⠁⢈⢇⣼⡁⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⢠⠒⠛⠲⣄⠀⠀⠀⣠⠏⠀⠉⠲⣤⠀⢸⠋⢻⣤⡛⣄⠀⠀⠀⠀⠀⠀⠀
⠀⠀⢡⠀⠀⠀⠀⠉⢲⠾⠁⠀⠀⠀⠀⠈⢳⡾⣤⠟⠁⠹⣿⢆⠀⠀⠀⠀⠀⠀
⠀⢀⠼⣆⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣼⠃⠀⠀⠀⠀⠀⠈⣧⠀⠀⠀⠀⠀
⠀⡏⠀⠘⢦⡀⠀⠀⠀⠀⠀⠀⠀⠀⣠⠞⠁⠀⠀⠀⠀⠀⠀⠀⢸⣧⠀⠀⠀⠀
⢰⣄⠀⠀⠀⠉⠳⠦⣤⣤⡤⠴⠖⠋⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢯⣆⠀⠀⠀
⢸⣉⠉⠓⠲⢦⣤⣄⣀⣀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⣀⣀⣀⣠⣼⢹⡄⠀⠀
⠘⡍⠙⠒⠶⢤⣄⣈⣉⡉⠉⠙⠛⠛⠛⠛⠛⠛⢻⠉⠉⠉⢙⣏⣁⣸⠇⡇⠀⠀
⠀⢣⠀⠀⠀⠀⠀⠀⠉⠉⠉⠙⠛⠛⠛⠛⠛⠛⠛⠒⠒⠒⠋⠉⠀⠸⠚⢇⠀⠀
⠀⠀⢧⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢠⠇⢤⣨⠇⠀
⠀⠀⠀⢧⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣤⢻⡀⣸⠀⠀⠀
⠀⠀⠀⢸⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢹⠛⠉⠁⠀⠀⠀
⠀⠀⠀⢸⠀⠀⠀⠀⠀⠀⠀⠀⢠⢄⣀⣤⠤⠴⠒⠀⠀⠀⠀⢸⠀⠀⠀⠀⠀⠀
⠀⠀⠀⢸⠀⠀⠀⠀⠀⠀⠀⠀⡇⠀⠀⢸⠀⠀⠀⠀⠀⠀⠀⠘⡆⠀⠀⠀⠀⠀
⠀⠀⠀⡎⠀⠀⠀⠀⠀⠀⠀⠀⢷⠀⠀⢸⠀⠀⠀⠀⠀⠀⠀⠀⡇⠀⠀⠀⠀⠀
⠀⠀⢀⡷⢤⣤⣀⣀⣀⣀⣠⠤⠾⣤⣀⡘⠛⠶⠶⠶⠶⠖⠒⠋⠙⠓⠲⢤⣀⠀
⠀⠀⠘⠧⣀⡀⠈⠉⠉⠁⠀⠀⠀⠀⠈⠙⠳⣤⣄⣀⣀⣀⠀⠀⠀⠀⠀⢀⣈⡇
⠀⠀⠀⠀⠀⠉⠛⠲⠤⠤⢤⣤⣄⣀⣀⣀⣀⡸⠇⠀⠀⠀⠉⠉⠉⠉⠉⠉⠁⠀
";
AnsiConsole.WriteLine(mascot);
AnsiConsole.MarkupLine("[dim]Autonomous AI Coding Loop Controller[/]\n");

// Default to current directory if not provided
if (string.IsNullOrEmpty(targetDir))
{
    targetDir = Directory.GetCurrentDirectory();
}

// Validate directory
if (!Directory.Exists(targetDir))
{
    AnsiConsole.MarkupLine($"[red]Error: Directory does not exist: {targetDir}[/]");
    return 1;
}

targetDir = Path.GetFullPath(targetDir);
AnsiConsole.MarkupLine($"[green]Target directory:[/] {targetDir}");

// Load project settings and global settings
var projectSettings = ProjectSettings.Load(targetDir);
var globalSettings = GlobalSettings.Load();
var savedProvider = freshMode ? null : projectSettings.Provider;

// First, ask about collaboration mode (before provider/model selection)
bool useMultiModelCollaboration = false;
if (!noTui && (freshMode || projectSettings.Collaboration == null))
{
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Panel(
        "[white]Multi-model collaboration allows you to use different AI models for different roles:\n" +
        "• [cyan]Planner[/] - designs implementation strategy\n" +
        "• [cyan]Challenger[/] - finds edge cases and issues\n" +
        "• [cyan]Reviewer[/] - verifies code quality\n" +
        "• [cyan]Implementer[/] - writes the actual code\n\n" +
        "You can select models from [yellow]any provider[/] and assign them to optimal roles.[/]")
        .Header("[yellow]Multi-Model Collaboration[/]")
        .BorderColor(Color.Blue));

    var modeChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("\n[yellow]How would you like to work?[/]")
            .AddChoices(
                "Multi-model collaboration (recommended) - use multiple models for different roles",
                "Single model only - use just one model for everything"));

    useMultiModelCollaboration = modeChoice.StartsWith("Multi-model");

    if (!useMultiModelCollaboration)
    {
        projectSettings.MultiModel = new MultiModelConfig { Strategy = ModelSwitchStrategy.None };
        projectSettings.Collaboration = new CollaborationConfig { Enabled = false };
        AnsiConsole.MarkupLine("\n[green]✓ Single model mode[/]");
    }
}
else if (projectSettings.Collaboration?.Enabled == true)
{
    // Use saved collaboration setting
    useMultiModelCollaboration = true;
    AnsiConsole.MarkupLine("[dim]Using saved collaboration mode: multi-model[/]");
}

// Variables for model selection
string? claudeModel = null;
string? codexModel = null;
string? geminiModel = null;
string? cursorModel = null;
string? copilotModel = null;
string? openCodeModel = null;
string? ollamaModel = null;
string? ollamaUrl = globalSettings.LastOllamaUrl ?? "http://localhost:11434";

// Determine provider: command line > saved > prompt
AIProvider provider = AIProvider.Claude; // Default
var providerWasSelected = false;

// Handle multi-model collaboration setup OR single model selection
if (useMultiModelCollaboration && !noTui)
{
    // Multi-model collaboration: skip provider selection, gather from ALL providers
    AnsiConsole.MarkupLine("\n[blue]Setting up multi-model collaboration...[/]");

    // Ask about local Ollama/LM Studio
    if (AnsiConsole.Confirm("\n[yellow]Do you have a local Ollama or LM Studio server?[/]",
        !string.IsNullOrEmpty(globalSettings.LastOllamaUrl)))
    {
        ollamaUrl = AnsiConsole.Prompt(
            new TextPrompt<string>("[yellow]Enter Ollama/LM Studio URL:[/]")
                .DefaultValue(ollamaUrl ?? "http://localhost:11434"));
        globalSettings.LastOllamaUrl = ollamaUrl;
    }
    else
    {
        ollamaUrl = null; // Don't check Ollama
    }

    AnsiConsole.MarkupLine("\n[dim]Discovering models from all available providers...[/]");

    var allAvailableModels = new List<ModelSpec>();

    // Gather from all providers
    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Discovering models...", async ctx =>
        {
            // Claude
            ctx.Status("Checking Claude...");
            try
            {
                var claudeModels = await GetClaudeModels();
                foreach (var m in claudeModels)
                    allAvailableModels.Add(new ModelSpec { Provider = AIProvider.Claude, Model = m });
            }
            catch { }

            // Codex
            ctx.Status("Checking Codex...");
            try
            {
                var codexModels = await GetCodexModels();
                foreach (var m in codexModels)
                    allAvailableModels.Add(new ModelSpec { Provider = AIProvider.Codex, Model = m });
            }
            catch { }

            // Copilot
            ctx.Status("Checking Copilot...");
            try
            {
                var copilotModels = await GetCopilotModels();
                foreach (var m in copilotModels)
                    allAvailableModels.Add(new ModelSpec { Provider = AIProvider.Copilot, Model = m });
            }
            catch { }

            // Gemini
            ctx.Status("Checking Gemini...");
            try
            {
                var geminiModels = await GetGeminiModels();
                foreach (var m in geminiModels)
                    allAvailableModels.Add(new ModelSpec { Provider = AIProvider.Gemini, Model = m });
            }
            catch { }

            // Cursor
            ctx.Status("Checking Cursor...");
            try
            {
                var cursorModels = await GetCursorModels();
                foreach (var m in cursorModels)
                    allAvailableModels.Add(new ModelSpec { Provider = AIProvider.Cursor, Model = m });
            }
            catch { }

            // OpenCode
            ctx.Status("Checking OpenCode...");
            try
            {
                var openCodeModels = await GetOpenCodeModels();
                foreach (var m in openCodeModels)
                    allAvailableModels.Add(new ModelSpec { Provider = AIProvider.OpenCode, Model = m });
            }
            catch { }

            // Ollama (if URL is configured)
            if (!string.IsNullOrEmpty(ollamaUrl))
            {
                ctx.Status("Checking Ollama...");
                try
                {
                    var ollamaModels = await GetOllamaModels(ollamaUrl);
                    foreach (var m in ollamaModels)
                        allAvailableModels.Add(new ModelSpec { Provider = AIProvider.Ollama, Model = m, BaseUrl = ollamaUrl });
                }
                catch { }
            }
        });

    if (allAvailableModels.Count < 2)
    {
        AnsiConsole.MarkupLine("[yellow]Not enough models found for multi-model collaboration.[/]");
        AnsiConsole.MarkupLine("[yellow]Falling back to single model mode.[/]");
        useMultiModelCollaboration = false;
        projectSettings.MultiModel = new MultiModelConfig { Strategy = ModelSwitchStrategy.None };
        projectSettings.Collaboration = new CollaborationConfig { Enabled = false };
    }
    else
    {
        AnsiConsole.MarkupLine($"[green]Found {allAvailableModels.Count} models across providers[/]\n");

        // Multi-select models - dynamically add groups for providers that have models
        var multiSelectPrompt = new MultiSelectionPrompt<string>()
            .Title("[yellow]Select models to include in your collaboration pool:[/]")
            .PageSize(25)
            .Required()
            .InstructionsText("[grey](Press <space> to toggle, <enter> to confirm - select at least 2)[/]");

        // Add choice groups for each provider that has models
        var providerGroups = allAvailableModels
            .GroupBy(m => m.Provider)
            .OrderBy(g => g.Key.ToString());

        foreach (var group in providerGroups)
        {
            var models = group.Select(m => m.DisplayName).ToList();
            if (models.Count > 0)
            {
                multiSelectPrompt.AddChoiceGroup(group.Key.ToString(), models);
            }
        }

        var selectedModelNames = AnsiConsole.Prompt(multiSelectPrompt);

        var selectedModels = allAvailableModels
            .Where(m => selectedModelNames.Contains(m.DisplayName))
            .ToList();

        if (selectedModels.Count < 2)
        {
            AnsiConsole.MarkupLine("[yellow]Need at least 2 models. Falling back to single model mode.[/]");
            useMultiModelCollaboration = false;
            projectSettings.MultiModel = new MultiModelConfig { Strategy = ModelSwitchStrategy.None };
            projectSettings.Collaboration = new CollaborationConfig { Enabled = false };
        }
        else
        {
            AnsiConsole.MarkupLine($"\n[green]Selected {selectedModels.Count} models:[/]");
            foreach (var m in selectedModels)
                AnsiConsole.MarkupLine($"  • {m.DisplayName}");

            // Select primary model (used for main iterations)
            var primaryModelName = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[yellow]Which model should be the primary (main worker)?[/]")
                    .AddChoices(selectedModels.Select(m => m.DisplayName)));

            var primaryModel = selectedModels.First(m => m.DisplayName == primaryModelName);

            // Reorder so primary is first
            selectedModels.Remove(primaryModel);
            selectedModels.Insert(0, primaryModel);

            AnsiConsole.MarkupLine($"[green]Primary model:[/] {primaryModel.DisplayName}");

            // Set the provider and model variables based on primary model
            provider = primaryModel.Provider;
            providerWasSelected = true;
            switch (primaryModel.Provider)
            {
                case AIProvider.Claude:
                    claudeModel = primaryModel.Model;
                    projectSettings.ClaudeModel = claudeModel;
                    break;
                case AIProvider.Codex:
                    codexModel = primaryModel.Model;
                    projectSettings.CodexModel = codexModel;
                    break;
                case AIProvider.Copilot:
                    copilotModel = primaryModel.Model;
                    projectSettings.CopilotModel = copilotModel;
                    break;
                case AIProvider.Gemini:
                    geminiModel = primaryModel.Model;
                    projectSettings.GeminiModel = geminiModel;
                    break;
                case AIProvider.Cursor:
                    cursorModel = primaryModel.Model;
                    projectSettings.CursorModel = cursorModel;
                    break;
                case AIProvider.Ollama:
                    ollamaModel = primaryModel.Model;
                    ollamaUrl = primaryModel.BaseUrl;
                    projectSettings.OllamaModel = ollamaModel;
                    projectSettings.OllamaUrl = ollamaUrl;
                    break;
                case AIProvider.OpenCode:
                    openCodeModel = primaryModel.Model;
                    projectSettings.OpenCodeModel = openCodeModel;
                    break;
            }

            // Configure MultiModelConfig
            projectSettings.MultiModel = new MultiModelConfig
            {
                Strategy = ModelSwitchStrategy.RoundRobin,
                Models = selectedModels
            };

            // Configure agent roles
            var roleSetupChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[yellow]Agent role assignment:[/]")
                    .AddChoices(
                        "AI-assisted (recommended) - automatically assign models to optimal roles",
                        "Manual - configure each role yourself"));

            projectSettings.Collaboration = new CollaborationConfig { Enabled = true };

            if (roleSetupChoice.StartsWith("AI-assisted"))
            {
                AnsiConsole.MarkupLine("\n[dim]Analyzing models for optimal role assignment...[/]");
                var roleAssignment = AssignModelsToRoles(selectedModels, primaryModel);
                projectSettings.Collaboration.Agents = roleAssignment;

                AnsiConsole.MarkupLine("[green]Agent roles configured:[/]");
                foreach (var (role, agentConfigs) in roleAssignment)
                {
                    var modelNamesStr = string.Join(", ", agentConfigs.Select(a => a.Model?.DisplayName ?? "default"));
                    var count = agentConfigs.Count > 1 ? $" ({agentConfigs.Count} agents)" : "";
                    AnsiConsole.MarkupLine($"  [cyan]{role}[/]: {modelNamesStr}{count}");
                }
            }
            else
            {
                await ConfigureAgentsManuallyAsync(projectSettings, selectedModels);
            }

            // Configure workflows
            AnsiConsole.MarkupLine("\n[blue]Configure workflows:[/]");

            var workflowChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Which workflows do you want to enable?[/]")
                    .AddChoices(
                        "Verification only - multiple agents verify completion",
                        "Full collaboration - spec, review, and verification workflows",
                        "Custom - choose individual workflows"));

            if (workflowChoice.StartsWith("Verification"))
            {
                projectSettings.Collaboration.Verification = new VerificationWorkflowConfig
                {
                    Enabled = true,
                    ReviewerCount = Math.Min(2, selectedModels.Count),
                    EnableChallenger = true,
                    ParallelExecution = true
                };
                AnsiConsole.MarkupLine("[green]Verification workflow enabled[/]");
            }
            else if (workflowChoice.StartsWith("Full"))
            {
                projectSettings.Collaboration.Verification = new VerificationWorkflowConfig
                {
                    Enabled = true,
                    ReviewerCount = Math.Min(2, selectedModels.Count),
                    EnableChallenger = true,
                    ParallelExecution = true
                };
                projectSettings.Collaboration.Review = new ReviewWorkflowConfig
                {
                    ReviewerCount = Math.Min(2, selectedModels.Count),
                    ExpertValidation = true
                };
                projectSettings.Collaboration.Spec = new SpecWorkflowConfig
                {
                    EnableChallenger = true,
                    MaxRefinements = 2
                };
                AnsiConsole.MarkupLine("[green]Full collaboration enabled[/] - spec, review, and verification");
            }
            else
            {
                if (AnsiConsole.Confirm("Enable [cyan]verification workflow[/]?", true))
                {
                    projectSettings.Collaboration.Verification = new VerificationWorkflowConfig
                    {
                        Enabled = true,
                        ReviewerCount = Math.Min(2, selectedModels.Count),
                        EnableChallenger = AnsiConsole.Confirm("Include challenger?", true),
                        ParallelExecution = AnsiConsole.Confirm("Run in parallel?", true)
                    };
                }

                if (AnsiConsole.Confirm("Enable [cyan]review workflow[/]?", false))
                {
                    projectSettings.Collaboration.Review = new ReviewWorkflowConfig
                    {
                        ReviewerCount = Math.Min(2, selectedModels.Count),
                        ExpertValidation = true
                    };
                }

                if (AnsiConsole.Confirm("Enable [cyan]spec workflow[/]?", false))
                {
                    projectSettings.Collaboration.Spec = new SpecWorkflowConfig
                    {
                        EnableChallenger = true,
                        MaxRefinements = 2
                    };
                }
            }

            // Show summary
            AnsiConsole.WriteLine();
            var enabledWorkflows = new List<string>();
            if (projectSettings.Collaboration.Verification?.Enabled == true) enabledWorkflows.Add("verification");
            if (projectSettings.Collaboration.Review != null) enabledWorkflows.Add("review");
            if (projectSettings.Collaboration.Spec != null) enabledWorkflows.Add("spec");
            var workflowSummary = enabledWorkflows.Count > 0 ? string.Join(", ", enabledWorkflows) : "none";

            AnsiConsole.Write(new Panel(
                $"[green]Primary model:[/] {primaryModel.DisplayName}\n" +
                $"[green]Model pool:[/] {string.Join(", ", selectedModels.Select(m => m.DisplayName))}\n" +
                $"[green]Workflows:[/] {workflowSummary}")
                .Header("[green]✓ Multi-Model Collaboration Configured[/]")
                .BorderColor(Color.Green));
        }
    }
}

// Single model path: select provider first
if (!useMultiModelCollaboration)
{
    if (providerFromArgs.HasValue)
    {
        provider = providerFromArgs.Value;
    }
    else if (savedProvider.HasValue)
    {
        provider = savedProvider.Value;
        AnsiConsole.MarkupLine($"[dim]Using saved provider from .ralph.json[/]");
    }
    else if (!noTui)
    {
        AnsiConsole.MarkupLine("[dim]Detecting installed providers...[/]");
        var installedProviders = GetInstalledProviders();

        if (installedProviders.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No AI providers found![/]");
            AnsiConsole.MarkupLine("[dim]Install one of: claude, codex, copilot, gemini, or opencode CLI tools.[/]");
            AnsiConsole.MarkupLine("[dim]Or use Ollama/LMStudio which is always available via HTTP API.[/]");
            installedProviders.Add(AIProvider.Ollama);
        }

        provider = AnsiConsole.Prompt(
            new SelectionPrompt<AIProvider>()
                .Title("[yellow]Select AI provider:[/]")
                .AddChoices(installedProviders));
        providerWasSelected = true;
    }

    AnsiConsole.MarkupLine($"[green]Provider:[/] {provider}");
}

// Single model selection (only if not using multi-model collaboration)
if (!useMultiModelCollaboration && provider == AIProvider.Claude)
{
    if (!string.IsNullOrEmpty(modelFromArgs))
    {
        // Use command line argument
        claudeModel = modelFromArgs;
        projectSettings.ClaudeModel = claudeModel;
    }
    else if (!freshMode && !string.IsNullOrEmpty(projectSettings.ClaudeModel))
    {
        // Use saved model
        claudeModel = projectSettings.ClaudeModel;
        AnsiConsole.MarkupLine($"[dim]Using saved model: {claudeModel}[/]");
    }
    else
    {
        // Get available models dynamically
        var claudeModels = await GetClaudeModels();
        claudeModels.Add("Enter custom model...");

        claudeModel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Select Claude model:[/]")
                .PageSize(10)
                .AddChoices(claudeModels));

        if (claudeModel == "Enter custom model...")
        {
            claudeModel = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Enter model name:[/]")
                    .DefaultValue("claude-sonnet-4"));
        }

        projectSettings.ClaudeModel = claudeModel;
    }

    AnsiConsole.MarkupLine($"[green]Model:[/] {claudeModel}");
}

// For Codex, handle model selection
if (!useMultiModelCollaboration && provider == AIProvider.Codex)
{
    if (!string.IsNullOrEmpty(modelFromArgs))
    {
        // Use command line argument
        codexModel = modelFromArgs;
        projectSettings.CodexModel = codexModel;
    }
    else if (!freshMode && !string.IsNullOrEmpty(projectSettings.CodexModel))
    {
        // Use saved model
        codexModel = projectSettings.CodexModel;
        AnsiConsole.MarkupLine($"[dim]Using saved model: {codexModel}[/]");
    }
    else
    {
        // Get available models dynamically
        var codexModels = await GetCodexModels();
        codexModels.Add("Enter custom model...");

        codexModel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Select Codex model:[/]")
                .PageSize(10)
                .AddChoices(codexModels));

        if (codexModel == "Enter custom model...")
        {
            codexModel = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Enter model name:[/]")
                    .DefaultValue("o3"));
        }

        projectSettings.CodexModel = codexModel;
    }

    AnsiConsole.MarkupLine($"[green]Model:[/] {codexModel}");
}

// For Gemini, handle model selection
if (!useMultiModelCollaboration && provider == AIProvider.Gemini)
{
    if (!string.IsNullOrEmpty(modelFromArgs))
    {
        // Use command line argument
        geminiModel = modelFromArgs;
        projectSettings.GeminiModel = geminiModel;
    }
    else if (!freshMode && !string.IsNullOrEmpty(projectSettings.GeminiModel))
    {
        // Use saved model
        geminiModel = projectSettings.GeminiModel;
        AnsiConsole.MarkupLine($"[dim]Using saved model: {geminiModel}[/]");
    }
    else
    {
        // Get available models dynamically
        var geminiModels = await GetGeminiModels();
        geminiModels.Add("Enter custom model...");

        geminiModel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Select Gemini model:[/]")
                .PageSize(10)
                .AddChoices(geminiModels));

        if (geminiModel == "Enter custom model...")
        {
            geminiModel = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Enter model name:[/]")
                    .DefaultValue("gemini-2.5-pro"));
        }

        projectSettings.GeminiModel = geminiModel;
    }

    AnsiConsole.MarkupLine($"[green]Model:[/] {geminiModel}");
}

// For Cursor, handle model selection
if (!useMultiModelCollaboration && provider == AIProvider.Cursor)
{
    if (!string.IsNullOrEmpty(modelFromArgs))
    {
        // Use command line argument
        cursorModel = modelFromArgs;
        projectSettings.CursorModel = cursorModel;
    }
    else if (!freshMode && !string.IsNullOrEmpty(projectSettings.CursorModel))
    {
        // Use saved model
        cursorModel = projectSettings.CursorModel;
        AnsiConsole.MarkupLine($"[dim]Using saved model: {cursorModel}[/]");
    }
    else
    {
        // Get available models dynamically
        var cursorModels = await GetCursorModels();
        cursorModels.Add("Enter custom model...");

        cursorModel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Select Cursor model:[/]")
                .PageSize(10)
                .AddChoices(cursorModels));

        if (cursorModel == "Enter custom model...")
        {
            cursorModel = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Enter model name:[/]")
                    .DefaultValue("claude-sonnet"));
        }

        projectSettings.CursorModel = cursorModel;
    }

    AnsiConsole.MarkupLine($"[green]Model:[/] {cursorModel}");
}

// For Copilot, handle model selection
if (!useMultiModelCollaboration && provider == AIProvider.Copilot)
{
    if (!string.IsNullOrEmpty(modelFromArgs))
    {
        // Use command line argument
        copilotModel = modelFromArgs;
        projectSettings.CopilotModel = copilotModel;
    }
    else if (!freshMode && !string.IsNullOrEmpty(projectSettings.CopilotModel))
    {
        // Use saved model
        copilotModel = projectSettings.CopilotModel;
        AnsiConsole.MarkupLine($"[dim]Using saved model: {copilotModel}[/]");
    }
    else
    {
        // Prompt for model
        copilotModel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Select Copilot model:[/]")
                .AddChoices(
                    "gpt-5",
                    "gpt-5-mini",
                    "gpt-5.1",
                    "gpt-5.1-codex",
                    "gpt-5.1-codex-mini",
                    "gpt-5.2",
                    "claude-sonnet-4",
                    "claude-sonnet-4.5",
                    "claude-opus-4.5"));

        projectSettings.CopilotModel = copilotModel;
    }

    AnsiConsole.MarkupLine($"[green]Model:[/] {copilotModel}");
}

// For OpenCode, handle model selection
if (!useMultiModelCollaboration && provider == AIProvider.OpenCode)
{
    if (!string.IsNullOrEmpty(modelFromArgs))
    {
        // Use command line argument
        openCodeModel = NormalizeOpenCodeModel(modelFromArgs);
        projectSettings.OpenCodeModel = openCodeModel;
    }
    else if (!freshMode && !string.IsNullOrEmpty(projectSettings.OpenCodeModel))
    {
        // Use saved model
        openCodeModel = NormalizeOpenCodeModel(projectSettings.OpenCodeModel);
        if (openCodeModel != projectSettings.OpenCodeModel)
        {
            projectSettings.OpenCodeModel = openCodeModel;
        }
        AnsiConsole.MarkupLine($"[dim]Using saved model: {openCodeModel}[/]");
    }
    else
    {
        // Get available models
        var models = await GetOpenCodeModels();

        // Prompt for model selection
        var allChoices = models.Concat(new[] { "Enter custom model..." }).ToList();
        var modelInput = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Select OpenCode model:[/]")
                .AddChoices(allChoices));

        if (modelInput == "Enter custom model...")
        {
            modelInput = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Enter custom model (provider/model):[/]")
                    .AllowEmpty());
        }

        openCodeModel = NormalizeOpenCodeModel(modelInput);
        if (!string.IsNullOrEmpty(openCodeModel))
        {
            projectSettings.OpenCodeModel = openCodeModel;
        }
    }

    var modelLabel = string.IsNullOrEmpty(openCodeModel) ? "(default)" : openCodeModel;
    AnsiConsole.MarkupLine($"[green]Model:[/] {modelLabel}");
}

// For Ollama/LMStudio, handle URL and model selection
if (!useMultiModelCollaboration && provider == AIProvider.Ollama)
{
    // Handle URL: command line > project settings > global cache > prompt
    if (!string.IsNullOrEmpty(apiUrlFromArgs))
    {
        ollamaUrl = apiUrlFromArgs;
        projectSettings.OllamaUrl = ollamaUrl;
        globalSettings.LastOllamaUrl = ollamaUrl;
    }
    else if (!freshMode && !string.IsNullOrEmpty(projectSettings.OllamaUrl))
    {
        ollamaUrl = projectSettings.OllamaUrl;
        AnsiConsole.MarkupLine($"[dim]Using saved API URL: {ollamaUrl}[/]");
    }
    else
    {
        // Build choices - include last used URL if available
        var urlChoices = new List<string>();

        // Add last used URL from global cache if available
        if (!string.IsNullOrEmpty(globalSettings.LastOllamaUrl))
        {
            urlChoices.Add($"{globalSettings.LastOllamaUrl} (last used)");
        }

        // Add standard options (only if not already the last used)
        if (globalSettings.LastOllamaUrl != "http://localhost:11434")
            urlChoices.Add("http://localhost:11434 (Ollama local)");
        if (globalSettings.LastOllamaUrl != "http://127.0.0.1:1234")
            urlChoices.Add("http://127.0.0.1:1234 (LMStudio)");
        urlChoices.Add("Enter custom URL...");

        ollamaUrl = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Select API endpoint:[/]")
                .AddChoices(urlChoices));

        if (ollamaUrl == "Enter custom URL...")
        {
            var defaultUrl = globalSettings.LastOllamaUrl ?? "http://localhost:11434";
            ollamaUrl = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Enter API URL:[/]")
                    .DefaultValue(defaultUrl));
        }
        else
        {
            // Extract just the URL part (remove description in parentheses)
            ollamaUrl = ollamaUrl.Split(' ')[0];
        }

        projectSettings.OllamaUrl = ollamaUrl;
        globalSettings.LastOllamaUrl = ollamaUrl;
    }

    AnsiConsole.MarkupLine($"[green]API URL:[/] {ollamaUrl}");

    // Handle model: command line > project settings > global cache > prompt
    if (!string.IsNullOrEmpty(modelFromArgs))
    {
        ollamaModel = modelFromArgs;
        projectSettings.OllamaModel = ollamaModel;
        globalSettings.LastOllamaModel = ollamaModel;
    }
    else if (!freshMode && !string.IsNullOrEmpty(projectSettings.OllamaModel))
    {
        ollamaModel = projectSettings.OllamaModel;
        AnsiConsole.MarkupLine($"[dim]Using saved model: {ollamaModel}[/]");
    }
    else
    {
        // Query server for available models
        var availableModels = await GetOllamaModels(ollamaUrl!);

        if (availableModels.Count > 0)
        {
            // If we have a last used model from global cache, put it first
            if (!string.IsNullOrEmpty(globalSettings.LastOllamaModel) &&
                availableModels.Contains(globalSettings.LastOllamaModel))
            {
                availableModels.Remove(globalSettings.LastOllamaModel);
                availableModels.Insert(0, $"{globalSettings.LastOllamaModel} (last used)");
            }

            // Add custom option at the end
            availableModels.Add("Enter custom model...");

            ollamaModel = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[yellow]Select model ({availableModels.Count - 1} available):[/]")
                    .PageSize(15)
                    .AddChoices(availableModels));

            if (ollamaModel == "Enter custom model...")
            {
                var defaultModel = globalSettings.LastOllamaModel ?? "llama3.1:8b";
                ollamaModel = AnsiConsole.Prompt(
                    new TextPrompt<string>("[yellow]Enter model name:[/]")
                        .DefaultValue(defaultModel));
            }
            else if (ollamaModel.EndsWith(" (last used)"))
            {
                // Strip the suffix
                ollamaModel = ollamaModel.Replace(" (last used)", "");
            }
        }
        else
        {
            // Fallback to manual entry if server query failed
            var defaultModel = globalSettings.LastOllamaModel ?? "llama3.1:8b";
            ollamaModel = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Enter model name:[/]")
                    .DefaultValue(defaultModel));
        }

        projectSettings.OllamaModel = ollamaModel;
        globalSettings.LastOllamaModel = ollamaModel;
    }

    AnsiConsole.MarkupLine($"[green]Model:[/] {ollamaModel}");
}

// Get multiModelConfig from projectSettings (set earlier in collaboration setup)
MultiModelConfig? multiModelConfig = projectSettings.MultiModel;

// Save provider to project settings if it changed or was newly selected
if (providerWasSelected || (providerFromArgs.HasValue && providerFromArgs != savedProvider))
{
    projectSettings.Provider = provider;
}
projectSettings.Save(targetDir);

// Save global settings (caches last used URLs/models)
globalSettings.Save();

// Create configuration
var providerConfig = provider switch
{
    AIProvider.Claude => AIProviderConfig.ForClaude(model: claudeModel),
    AIProvider.Codex => AIProviderConfig.ForCodex(model: codexModel),
    AIProvider.Copilot => AIProviderConfig.ForCopilot(model: copilotModel),
    AIProvider.Gemini => AIProviderConfig.ForGemini(model: geminiModel),
    AIProvider.Cursor => AIProviderConfig.ForCursor(model: cursorModel),
    AIProvider.OpenCode => AIProviderConfig.ForOpenCode(model: openCodeModel),
    AIProvider.Ollama => AIProviderConfig.ForOllama(baseUrl: ollamaUrl, model: ollamaModel),
    _ => AIProviderConfig.ForClaude()
};

var config = new RalphConfig
{
    TargetDirectory = targetDir,
    Provider = provider,
    ProviderConfig = providerConfig,
    MultiModel = multiModelConfig,
    Collaboration = projectSettings.Collaboration
};

// Handle init mode - regenerate all project files from new spec
if (initMode)
{
    AnsiConsole.MarkupLine("\n[blue]Initializing project with new specification...[/]");

    string projectContext;

    if (!string.IsNullOrEmpty(initSpec))
    {
        // Check if initSpec is a file path
        var specPath = Path.IsPathRooted(initSpec)
            ? initSpec
            : Path.Combine(targetDir, initSpec);

        if (File.Exists(specPath))
        {
            AnsiConsole.MarkupLine($"[dim]Reading spec from {specPath}...[/]");
            projectContext = await File.ReadAllTextAsync(specPath);
        }
        else
        {
            // Treat as inline description
            projectContext = initSpec;
        }
    }
    else
    {
        // Prompt for spec
        AnsiConsole.MarkupLine("[yellow]Enter your project specification.[/]");
        AnsiConsole.MarkupLine("[dim]Describe what you want to build, or provide a path to a spec file.[/]\n");

        projectContext = AnsiConsole.Prompt(
            new TextPrompt<string>("[yellow]Project spec:[/]")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(projectContext))
        {
            AnsiConsole.MarkupLine("[red]No specification provided. Exiting init mode.[/]");
            return 1;
        }

        // Check if it's a file path
        var specPath = Path.IsPathRooted(projectContext)
            ? projectContext
            : Path.Combine(targetDir, projectContext);

        if (File.Exists(specPath))
        {
            AnsiConsole.MarkupLine($"[dim]Reading spec from {specPath}...[/]");
            projectContext = await File.ReadAllTextAsync(specPath);
        }
    }

    var initScaffolder = new ProjectScaffolder(config)
    {
        ProjectContext = projectContext,
        ForceOverwrite = true
    };

    initScaffolder.OnScaffoldStart += file =>
        AnsiConsole.MarkupLine($"[dim]Generating {file}...[/]");
    initScaffolder.OnScaffoldComplete += (file, success) =>
    {
        if (success)
            AnsiConsole.MarkupLine($"[green]Created {file}[/]");
        else
            AnsiConsole.MarkupLine($"[red]Failed to create {file}[/]");
    };
    initScaffolder.OnOutput += line =>
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(line)}[/]");

    AnsiConsole.MarkupLine("\n[blue]Regenerating project files...[/]");
    await initScaffolder.ScaffoldAllAsync();

    AnsiConsole.MarkupLine("\n[green]Project initialized![/]");
}

// Check project structure
var scaffolder = new ProjectScaffolder(config);
var structure = scaffolder.ValidateProject();

if (!structure.IsComplete && !initMode)
{
    AnsiConsole.MarkupLine("\n[yellow]Missing project files:[/]");
    foreach (var item in structure.MissingItems)
    {
        AnsiConsole.MarkupLine($"  [red]- {item}[/]");
    }

    // In no-TUI mode, skip interactive prompts and continue anyway
    var action = noTui
        ? "Continue anyway"
        : AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("\n[yellow]What would you like to do?[/]")
                .AddChoices(
                    "Generate files using AI",
                    "Create default template files",
                    "Continue anyway",
                    "Exit"));

    switch (action)
    {
        case "Generate files using AI":
            // Warn about code-focused models having issues with scaffolding
            if (config.ProviderConfig.Provider == AIProvider.Ollama)
            {
                AnsiConsole.Write(new Panel(
                    "[yellow]⚠️ Warning: Code-focused models (qwen-coder, deepseek-coder, codellama, etc.)\n" +
                    "often struggle with generating scaffold files. They may output JSON or echo\n" +
                    "the spec instead of creating proper Ralph files.\n\n" +
                    "If scaffolding fails, try 'Create default template files' instead.[/]")
                    .Header("[yellow]Local Model Notice[/]")
                    .BorderColor(Color.Yellow));
                AnsiConsole.WriteLine();
            }

            // Ask for project description - we can't generate without context
            AnsiConsole.MarkupLine("\n[yellow]To generate project files, I need to understand your project.[/]");
            AnsiConsole.MarkupLine("[dim]You can provide a description, or path to a document (README, spec, etc.)[/]\n");

            var projectContext = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Describe your project (or enter path to a doc):[/]")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(projectContext))
            {
                AnsiConsole.MarkupLine("[red]No project description provided. Using default templates instead.[/]");
                await scaffolder.CreateDefaultFilesAsync();
                AnsiConsole.MarkupLine("[green]Created default template files[/]");
                break;
            }

            // Check if it's a file path
            var contextPath = Path.IsPathRooted(projectContext)
                ? projectContext
                : Path.Combine(targetDir, projectContext);

            if (File.Exists(contextPath))
            {
                AnsiConsole.MarkupLine($"[dim]Reading context from {contextPath}...[/]");
                projectContext = await File.ReadAllTextAsync(contextPath);
            }

            scaffolder.ProjectContext = projectContext;

            AnsiConsole.MarkupLine("\n[blue]Generating project files using AI...[/]");

            scaffolder.OnScaffoldStart += file =>
                AnsiConsole.MarkupLine($"[dim]Generating {file}...[/]");
            scaffolder.OnScaffoldComplete += (file, success) =>
            {
                if (success)
                    AnsiConsole.MarkupLine($"[green]Created {file}[/]");
                else
                    AnsiConsole.MarkupLine($"[red]Failed to create {file}[/]");
            };
            scaffolder.OnOutput += line =>
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(line)}[/]");

            await scaffolder.ScaffoldMissingAsync();
            break;

        case "Create default template files":
            await scaffolder.CreateDefaultFilesAsync();
            AnsiConsole.MarkupLine("[green]Created default template files[/]");
            break;

        case "Exit":
            return 0;
    }
}

// Re-validate
structure = scaffolder.ValidateProject();
if (!structure.HasPromptMd)
{
    AnsiConsole.MarkupLine("[red]Error: prompt.md is required to run the loop[/]");
    return 1;
}

// No-TUI mode - run loop with plain console output
if (noTui)
{
    AnsiConsole.MarkupLine("[yellow]Running in console mode (no TUI)...[/]\n");

    // For Ollama provider, use OllamaClient directly for proper streaming
    if (provider == AIProvider.Ollama)
    {
        var promptPath = config.PromptFilePath;
        if (!File.Exists(promptPath))
        {
            Console.Error.WriteLine($"[ERROR] prompt.md not found at {promptPath}");
            return 1;
        }

        var prompt = await File.ReadAllTextAsync(promptPath);
        var client = new OllamaClient(
            ollamaUrl ?? "http://localhost:11434",
            ollamaModel ?? "llama3.1:8b",
            targetDir);

        client.OnOutput += text => Console.Write(text);
        client.OnToolCall += (name, args) => Console.WriteLine($"\n[Tool: {name}]");
        client.OnToolResult += (name, result) =>
        {
            var preview = result.Length > 200 ? result.Substring(0, 200) + "..." : result;
            Console.WriteLine($"[Result: {preview}]\n");
        };
        client.OnError += err => Console.Error.WriteLine($"\n[ERROR] {err}");
        client.OnIterationComplete += iter => Console.WriteLine($"\n=== Iteration {iter} complete ===\n");

        // Handle Ctrl+C
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n[Stopping...]");
            client.Stop();
        };

        Console.WriteLine("[Press Ctrl+C to stop]\n");
        Console.WriteLine("=== Starting Ollama session ===\n");

        try
        {
            var result = await client.RunAsync(prompt, CancellationToken.None);
            Console.WriteLine($"\n=== Session complete: {(result.Success ? "SUCCESS" : "FAILED")} ===");
            if (!result.Success && !string.IsNullOrEmpty(result.Error))
            {
                Console.Error.WriteLine($"Error: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n[ERROR] {ex.Message}");
        }

        Console.WriteLine("\n[Goodbye!]");
        return 0;
    }

    // For other providers, use LoopController
    using var consoleController = new LoopController(config);
    var loopStopRequested = false;

    consoleController.OnOutput += line =>
    {
        Console.WriteLine(line);
    };
    consoleController.OnError += line =>
    {
        Console.Error.WriteLine($"[ERROR] {line}");
    };
    consoleController.OnIterationStart += (iter, modelName) =>
    {
        var modelSuffix = modelName != null ? $" [{modelName}]" : "";
        Console.WriteLine($"\n=== Starting iteration {iter}{modelSuffix} ===");
    };
    consoleController.OnIterationComplete += (iter, result) =>
    {
        var status = result.Success ? "SUCCESS" : "FAILED";
        Console.WriteLine($"=== Iteration {iter} complete: {status} ===\n");
    };
    consoleController.OnStateChanged += newState =>
    {
        Console.WriteLine($"[State: {newState}]");
    };

    // Handle Ctrl+C
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        loopStopRequested = true;
        Console.WriteLine("\n[Stopping... press Ctrl+C again to force quit]");
        consoleController.Stop();
    };

    Console.WriteLine("[Press Ctrl+C to stop]\n");
    await consoleController.StartAsync();

    // Wait for completion or stop
    while (consoleController.State == LoopState.Running && !loopStopRequested)
    {
        await Task.Delay(100);
    }

    Console.WriteLine("\n[Goodbye!]");
    return 0;
}

// Create components
using var fileWatcher = new FileWatcher(config);
using var controller = new LoopController(config);
using var ui = new ConsoleUI(controller, fileWatcher, config);

// Auto-start if project structure is complete
ui.AutoStart = structure.IsComplete;

// Start file watcher
fileWatcher.Start();

// Handle Ctrl+C
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    ui.Stop();
};

// Run UI
AnsiConsole.Clear();
await ui.RunAsync();

// Cleanup
AnsiConsole.MarkupLine("\n[yellow]Goodbye![/]");
return 0;
