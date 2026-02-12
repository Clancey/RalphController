using System.Diagnostics;
using System.Text;
using RalphController;
using RalphController.Models;
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

static async Task<ModelSpec?> PromptForModelSpec(string label, string? defaultOllamaUrl = null)
{
    // Get installed providers and build choices
    var installedProviders = GetInstalledProviders();
    var providerChoices = new List<string> { "← Go back" };
    providerChoices.AddRange(installedProviders.Select(p => p.ToString()));

    if (installedProviders.Count == 0)
    {
        providerChoices.Add("Ollama");  // Always available via HTTP
    }

    var providerChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title($"[yellow]{label} - Select provider:[/]")
            .AddChoices(providerChoices));

    if (providerChoice == "← Go back")
    {
        return null;
    }

    var selectedProvider = Enum.Parse<AIProvider>(providerChoice);
    string? model = null;
    string? url = null;

    // Get model based on provider
    switch (selectedProvider)
    {
        case AIProvider.Claude:
            var clModels = new List<string> { "← Go back" };
            clModels.AddRange(await GetClaudeModels());
            clModels.Add("Enter custom model...");
            model = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select Claude model:[/]")
                    .AddChoices(clModels));
            if (model == "← Go back") return null;
            if (model == "Enter custom model...")
            {
                model = AnsiConsole.Prompt(
                    new TextPrompt<string>("[yellow]Enter model name:[/]")
                        .DefaultValue("sonnet"));
            }
            break;

        case AIProvider.Codex:
            var cxModels = new List<string> { "← Go back" };
            cxModels.AddRange(await GetCodexModels());
            cxModels.Add("Enter custom model...");
            model = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select Codex model:[/]")
                    .AddChoices(cxModels));
            if (model == "← Go back") return null;
            if (model == "Enter custom model...")
            {
                model = AnsiConsole.Prompt(
                    new TextPrompt<string>("[yellow]Enter model name:[/]")
                        .DefaultValue("o3"));
            }
            break;

        case AIProvider.Copilot:
            var copilotModels = new List<string>
            {
                "← Go back",
                "gpt-5", "gpt-5-mini", "gpt-5.1", "gpt-5.2",
                "claude-sonnet-4", "claude-opus-4.5",
                "Enter custom model..."
            };
            model = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select Copilot model:[/]")
                    .AddChoices(copilotModels));
            if (model == "← Go back") return null;
            if (model == "Enter custom model...")
            {
                model = AnsiConsole.Prompt(
                    new TextPrompt<string>("[yellow]Enter model name:[/]")
                        .DefaultValue("gpt-5"));
            }
            break;

        case AIProvider.Gemini:
            var gmModels = new List<string> { "← Go back" };
            gmModels.AddRange(await GetGeminiModels());
            gmModels.Add("Enter custom model...");
            model = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select Gemini model:[/]")
                    .AddChoices(gmModels));
            if (model == "← Go back") return null;
            if (model == "Enter custom model...")
            {
                model = AnsiConsole.Prompt(
                    new TextPrompt<string>("[yellow]Enter model name:[/]")
                        .DefaultValue("gemini-2.5-pro"));
            }
            break;

        case AIProvider.Cursor:
            var cursorModels = new List<string> { "← Go back" };
            cursorModels.AddRange(await GetCursorModels());
            cursorModels.Add("Enter custom model...");
            model = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select Cursor model:[/]")
                    .AddChoices(cursorModels));
            if (model == "← Go back") return null;
            if (model == "Enter custom model...")
            {
                model = AnsiConsole.Prompt(
                    new TextPrompt<string>("[yellow]Enter model name:[/]")
                        .DefaultValue("claude-sonnet"));
            }
            break;

        case AIProvider.OpenCode:
            var ocModels = new List<string> { "← Go back" };
            ocModels.AddRange(await GetOpenCodeModels());
            ocModels.Add("Enter custom model...");
            model = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select OpenCode model:[/]")
                    .PageSize(15)
                    .AddChoices(ocModels));
            if (model == "← Go back") return null;
            if (model == "Enter custom model...")
            {
                model = AnsiConsole.Prompt(
                    new TextPrompt<string>("[yellow]Enter model (provider/model):[/]")
                        .DefaultValue("ollama/llama3.1:70b"));
            }
            break;

        case AIProvider.Ollama:
            url = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Ollama API URL (or 'back' to go back):[/]")
                    .DefaultValue(defaultOllamaUrl ?? "http://localhost:11434"));
            if (url.Equals("back", StringComparison.OrdinalIgnoreCase)) return null;
            var olModels = await GetOllamaModels(url);
            var olModelChoices = new List<string> { "← Go back" };
            if (olModels.Count > 0)
            {
                olModelChoices.AddRange(olModels);
                olModelChoices.Add("Enter custom model...");
                model = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[yellow]Select Ollama model:[/]")
                        .PageSize(15)
                        .AddChoices(olModelChoices));
                if (model == "← Go back") return null;
                if (model == "Enter custom model...")
                {
                    model = AnsiConsole.Prompt(
                        new TextPrompt<string>("[yellow]Enter model name:[/]")
                            .DefaultValue("llama3.1:8b"));
                }
            }
            else
            {
                model = AnsiConsole.Prompt(
                    new TextPrompt<string>("[yellow]Enter model name (or 'back' to go back):[/]")
                        .DefaultValue("llama3.1:8b"));
                if (model.Equals("back", StringComparison.OrdinalIgnoreCase)) return null;
            }
            break;
    }

    // Create a descriptive label using the actual model name
    var displayLabel = !string.IsNullOrEmpty(model)
        ? model  // Use actual model name (e.g., "sonnet", "opus", "gpt-4o")
        : selectedProvider.ToString();  // Fallback to provider name

    return new ModelSpec
    {
        Provider = selectedProvider,
        Model = model ?? "",
        BaseUrl = url,
        Label = displayLabel
    };
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
string? ralphFolderFromArgs = null;

// Define valid arguments
var validArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "--provider", "--model", "--api-url", "--url", "--ralph-folder",
    "--codex", "--copilot", "--claude", "--gemini", "--cursor", "--opencode", "--ollama", "--lmstudio",
    "--list-models", "--fresh", "--init", "--spec", "--no-tui", "--console",
    "--test-streaming", "--single-run", "--test-aiprocess", "--test-output",
    "--teams"
};

// Check for flags
var listModels = false;
var freshMode = false;
var noTui = false;
var teamsMode = false;
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
    else if (args[i] == "--ralph-folder" && i + 1 < args.Length)
    {
        ralphFolderFromArgs = args[i + 1];
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
    else if (args[i] == "--teams")
    {
        teamsMode = true;
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
    AnsiConsole.MarkupLine("  [dim]--ralph-folder <path>[/] Store project files in a subfolder (e.g. .ralph)");
    AnsiConsole.MarkupLine("  [dim]--fresh[/]               Ignore saved settings, prompt for all options");
    AnsiConsole.MarkupLine("  [dim]--init [[spec]][/]       Initialize/regenerate project files from spec");
    AnsiConsole.MarkupLine("  [dim]--no-tui[/]              Run without TUI (plain console output)");
    AnsiConsole.MarkupLine("  [dim]--list-models[/]         List available models");
    AnsiConsole.MarkupLine("  [dim]--teams[/]                Run in teams mode (parallel agents)");
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

// Determine provider: command line > saved > prompt
AIProvider provider;
var providerWasSelected = false;

if (providerFromArgs.HasValue)
{
    // Use command line argument
    provider = providerFromArgs.Value;
}
else if (savedProvider.HasValue)
{
    // Use saved provider from project settings
    provider = savedProvider.Value;
    AnsiConsole.MarkupLine($"[dim]Using saved provider from .ralph.json[/]");
}
else
{
    // Detect installed providers
    AnsiConsole.MarkupLine("[dim]Detecting installed providers...[/]");
    var installedProviders = GetInstalledProviders();

    if (installedProviders.Count == 0)
    {
        AnsiConsole.MarkupLine("[red]No AI providers found![/]");
        AnsiConsole.MarkupLine("[dim]Install one of: claude, codex, copilot, gemini, or opencode CLI tools.[/]");
        AnsiConsole.MarkupLine("[dim]Or use Ollama/LMStudio which is always available via HTTP API.[/]");
        installedProviders.Add(AIProvider.Ollama);  // Fallback to Ollama
    }

    // Prompt for provider
    provider = AnsiConsole.Prompt(
        new SelectionPrompt<AIProvider>()
            .Title("[yellow]Select AI provider:[/]")
            .AddChoices(installedProviders));
    providerWasSelected = true;
}

AnsiConsole.MarkupLine($"[green]Provider:[/] {provider}");

// For Claude, handle model selection
string? claudeModel = null;
if (provider == AIProvider.Claude)
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
string? codexModel = null;
if (provider == AIProvider.Codex)
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
string? geminiModel = null;
if (provider == AIProvider.Gemini)
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
string? cursorModel = null;
if (provider == AIProvider.Cursor)
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
string? copilotModel = null;
if (provider == AIProvider.Copilot)
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
string? openCodeModel = null;
if (provider == AIProvider.OpenCode)
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
string? ollamaUrl = null;
string? ollamaModel = null;
if (provider == AIProvider.Ollama)
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

// Ask about multi-model configuration (only if never configured, or --fresh mode)
MultiModelConfig? multiModelConfig = null;
if (!noTui && !teamsMode && (freshMode || (projectSettings.MultiModel == null && projectSettings.Teams?.IsEnabled != true)))
{
    bool multiModelConfigured = false;
    while (!multiModelConfigured)
    {
        var multiModelChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("\n[yellow]Multi-model configuration:[/]")
                .AddChoices(
                    "Single model (default)",
                    "Verification model - use a second model to verify completion",
                    "Round-robin rotation - alternate between models each iteration",
                    "Teams mode - parallel agents working on subtasks"));

        if (multiModelChoice == "Single model (default)")
        {
            // Save explicit "single model" choice so we don't ask again
            projectSettings.MultiModel = new MultiModelConfig { Strategy = ModelSwitchStrategy.None };
            multiModelConfigured = true;
        }
        else if (multiModelChoice.StartsWith("Teams mode"))
        {
            // Teams mode selected from multi-model config - set flag and break out
            teamsMode = true;
            multiModelConfigured = true;
        }
        else
        {
            var strategy = multiModelChoice.StartsWith("Verification")
                ? ModelSwitchStrategy.Verification
                : ModelSwitchStrategy.RoundRobin;

            // Create primary model spec from current selection
            var primaryModelName = provider switch
            {
                AIProvider.Claude => claudeModel ?? "sonnet",
                AIProvider.Codex => codexModel ?? "o3",
                AIProvider.Copilot => copilotModel ?? "gpt-5",
                AIProvider.Gemini => geminiModel ?? "gemini-2.5-pro",
                AIProvider.Cursor => cursorModel ?? "claude-sonnet",
                AIProvider.OpenCode => openCodeModel ?? "",
                AIProvider.Ollama => ollamaModel ?? "llama3.1:8b",
                _ => ""
            };

            var primaryModel = new ModelSpec
            {
                Provider = provider,
                Model = primaryModelName,
                BaseUrl = provider == AIProvider.Ollama ? ollamaUrl : null,
                Label = !string.IsNullOrEmpty(primaryModelName) ? primaryModelName : provider.ToString()
            };

            var modelList = new List<ModelSpec> { primaryModel };

            var cancelled = false;
            if (strategy == ModelSwitchStrategy.Verification)
            {
                // For verification, only need one verifier model
                AnsiConsole.MarkupLine("\n[yellow]Select verifier model:[/]");
                var verifierModel = await PromptForModelSpec("Verifier", ollamaUrl);
                if (verifierModel != null)
                {
                    modelList.Add(verifierModel);
                    multiModelConfigured = true;
                }
                else
                {
                    // User went back - loop back to multi-model choice
                    AnsiConsole.MarkupLine("[dim]Going back to multi-model selection...[/]");
                }
            }
            else
            {
                // For round-robin, allow adding multiple models
                var modelIndex = 2;
                var addMore = true;

                while (addMore)
                {
                    AnsiConsole.MarkupLine($"\n[yellow]Add model #{modelIndex} for rotation:[/]");
                    var nextModel = await PromptForModelSpec($"Model {modelIndex}", ollamaUrl);
                    if (nextModel != null)
                    {
                        modelList.Add(nextModel);
                        modelIndex++;
                    }
                    else if (modelList.Count == 1)
                    {
                        // User went back on first additional model - loop back to multi-model choice
                        cancelled = true;
                        AnsiConsole.MarkupLine("[dim]Going back to multi-model selection...[/]");
                        break;
                    }
                    // else: user went back but we have at least 2 models, just stop adding more

                    if (!cancelled && modelList.Count > 1)
                    {
                        // Ask if they want to add another model
                        addMore = AnsiConsole.Confirm("[yellow]Add another model to the rotation?[/]", false);
                    }
                    else if (cancelled)
                    {
                        break;
                    }
                }

                if (!cancelled)
                {
                    multiModelConfigured = true;
                }
            }

            if (multiModelConfigured)
            {
                multiModelConfig = new MultiModelConfig
                {
                    Strategy = strategy,
                    Models = modelList,
                    Verification = strategy == ModelSwitchStrategy.Verification
                        ? new VerificationConfig { VerifierIndex = modelList.Count - 1, Trigger = VerificationTrigger.CompletionSignal }
                        : null
                };

                projectSettings.MultiModel = multiModelConfig;

                var modelNames = string.Join(" → ", modelList.Select(m => m.DisplayName));
                AnsiConsole.MarkupLine($"[green]Multi-model:[/] {strategy} - {modelNames}");
            }
        }
    }
}
else if (!freshMode && projectSettings.MultiModel?.IsEnabled == true)
{
    // Use saved multi-model config
    multiModelConfig = projectSettings.MultiModel;
    var modelNames = string.Join(" + ", multiModelConfig.Models.Select(m => m.DisplayName));
    AnsiConsole.MarkupLine($"[dim]Using saved multi-model config: {multiModelConfig.Strategy} - {modelNames}[/]");
}

// Teams mode configuration
TeamConfig? teamConfig = null;
if (teamsMode && !freshMode && projectSettings.Teams?.IsEnabled == true)
{
    // --teams passed but saved config exists — use it
    teamConfig = projectSettings.Teams;
    AnsiConsole.MarkupLine($"[dim]Using saved teams config: {teamConfig.AgentCount} agents[/]");
}
else if (teamsMode)
{
    // Teams mode from CLI flag - prompt for configuration
    AnsiConsole.MarkupLine("\n[blue]Teams Mode Configuration[/]");

    // Number of agents
    var agentCount = AnsiConsole.Prompt(
        new TextPrompt<int>("[yellow]Number of sub-agents (2-8):[/]")
            .DefaultValue(3)
            .Validate(n => n >= 2 && n <= 8 ? ValidationResult.Success() : ValidationResult.Error("Must be 2-8")));

    // Lead agent model
    AnsiConsole.MarkupLine("\n[yellow]Select lead agent model:[/]");
    var leadModel = await PromptForModelSpec("Lead Agent", ollamaUrl);
    if (leadModel == null)
    {
        // Use current provider as lead
        var currentModelName = provider switch
        {
            AIProvider.Claude => claudeModel ?? "sonnet",
            AIProvider.Codex => codexModel ?? "o3",
            AIProvider.Copilot => copilotModel ?? "gpt-5",
            AIProvider.Gemini => geminiModel ?? "gemini-2.5-pro",
            AIProvider.Cursor => cursorModel ?? "claude-sonnet",
            AIProvider.OpenCode => openCodeModel ?? "",
            AIProvider.Ollama => ollamaModel ?? "llama3.1:8b",
            _ => ""
        };
        leadModel = new ModelSpec
        {
            Provider = provider,
            Model = currentModelName,
            BaseUrl = provider == AIProvider.Ollama ? ollamaUrl : null,
            Label = currentModelName
        };
    }

    // Sub-agent model assignment
    var assignmentChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[yellow]Sub-agent model assignment:[/]")
            .AddChoices(
                "Same as lead (default)",
                "Pick for each agent",
                "Round-robin from list"));

    var modelAssignment = AgentModelAssignment.SameAsLead;
    var agentModels = new List<ModelSpec>();

    if (assignmentChoice.StartsWith("Pick"))
    {
        modelAssignment = AgentModelAssignment.PerAgent;
        for (int i = 0; i < agentCount; i++)
        {
            AnsiConsole.MarkupLine($"\n[yellow]Select model for Agent {i + 1}:[/]");
            var agentModel = await PromptForModelSpec($"Agent {i + 1}", ollamaUrl);
            agentModels.Add(agentModel ?? leadModel);
        }
    }
    else if (assignmentChoice.StartsWith("Round"))
    {
        modelAssignment = AgentModelAssignment.RoundRobin;
        agentModels.Add(leadModel);
        var addMore = true;
        var modelIndex = 2;
        while (addMore)
        {
            AnsiConsole.MarkupLine($"\n[yellow]Add model #{modelIndex} for rotation:[/]");
            var nextModel = await PromptForModelSpec($"Model {modelIndex}", ollamaUrl);
            if (nextModel != null)
            {
                agentModels.Add(nextModel);
                modelIndex++;
            }
            if (agentModels.Count > 1)
            {
                addMore = AnsiConsole.Confirm("[yellow]Add another model?[/]", false);
            }
            else
            {
                break;
            }
        }
    }

    // Decomposition strategy
    var decompChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[yellow]Task decomposition strategy:[/]")
            .AddChoices(
                "AI decomposed (lead agent breaks down tasks)",
                "From implementation_plan.md"));

    var decomposition = decompChoice.StartsWith("AI")
        ? DecompositionStrategy.AIDecomposed
        : DecompositionStrategy.FromPlan;

    // Merge strategy
    var mergeChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[yellow]Merge strategy:[/]")
            .AddChoices(
                "Sequential (safest)",
                "Rebase then merge",
                "Direct merge"));

    var mergeStrategy = mergeChoice switch
    {
        var s when s.StartsWith("Rebase") => MergeStrategy.RebaseThenMerge,
        var s when s.StartsWith("Direct") => MergeStrategy.MergeDirect,
        _ => MergeStrategy.Sequential
    };

    teamConfig = new TeamConfig
    {
        AgentCount = agentCount,
        LeadModel = leadModel,
        AgentModels = agentModels,
        ModelAssignment = modelAssignment,
        DecompositionStrategy = decomposition,
        MergeStrategy = mergeStrategy,
        UseWorktrees = true
    };

    projectSettings.Teams = teamConfig;

    // Display summary
    var teamModelNames = modelAssignment switch
    {
        AgentModelAssignment.SameAsLead => $"All: {leadModel.DisplayName}",
        AgentModelAssignment.PerAgent => string.Join(", ", agentModels.Select(m => m.DisplayName)),
        AgentModelAssignment.RoundRobin => string.Join(" → ", agentModels.Select(m => m.DisplayName)),
        _ => leadModel.DisplayName
    };
    AnsiConsole.MarkupLine($"[green]Teams:[/] {agentCount} agents, Lead: {Markup.Escape(leadModel.DisplayName)}, Sub-agents: {Markup.Escape(teamModelNames)}");
    AnsiConsole.MarkupLine($"[green]Strategy:[/] {decomposition}, Merge: {mergeStrategy}");
}
else if (!teamsMode && !freshMode && projectSettings.Teams?.IsEnabled == true)
{
    // Auto-enable teams from saved config (no --teams flag needed)
    teamConfig = projectSettings.Teams;
    teamsMode = true;
    AnsiConsole.MarkupLine($"[dim]Using saved teams config: {teamConfig.AgentCount} agents[/]");
}

// Determine ralph folder: CLI arg > saved settings > prompt for new projects
string? ralphFolder = null;
if (!string.IsNullOrEmpty(ralphFolderFromArgs))
{
    ralphFolder = ralphFolderFromArgs;
    projectSettings.RalphFolder = ralphFolder;
}
else if (!freshMode && !string.IsNullOrEmpty(projectSettings.RalphFolder))
{
    ralphFolder = projectSettings.RalphFolder;
    AnsiConsole.MarkupLine($"[dim]Using saved ralph folder: {ralphFolder}[/]");
}
else if (!noTui && freshMode)
{
    // Only prompt during --fresh or new project setup (no existing project files in root)
    var hasExistingFiles = File.Exists(Path.Combine(targetDir, "prompt.md"))
        || File.Exists(Path.Combine(targetDir, "implementation_plan.md"))
        || File.Exists(Path.Combine(targetDir, "agents.md"));

    if (!hasExistingFiles)
    {
        var folderChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("\n[yellow]Where should Ralph store project files?[/]")
                .AddChoices(
                    "Project root (default)",
                    ".ralph/ folder",
                    "Custom path..."));

        ralphFolder = folderChoice switch
        {
            ".ralph/ folder" => ".ralph",
            "Custom path..." => AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Enter folder path:[/]")
                    .DefaultValue(".ralph")),
            _ => null
        };

        if (!string.IsNullOrEmpty(ralphFolder))
        {
            projectSettings.RalphFolder = ralphFolder;
        }
    }
}

// Auto-create ralph folder if set
if (!string.IsNullOrEmpty(ralphFolder))
{
    var resolvedFolder = Path.IsPathRooted(ralphFolder)
        ? ralphFolder
        : Path.Combine(targetDir, ralphFolder);

    if (!Directory.Exists(resolvedFolder))
    {
        Directory.CreateDirectory(resolvedFolder);
        AnsiConsole.MarkupLine($"[green]Created ralph folder:[/] {resolvedFolder}");
    }

    // Tip for gitignore with relative paths
    if (!Path.IsPathRooted(ralphFolder))
    {
        var gitignorePath = Path.Combine(targetDir, ".gitignore");
        if (File.Exists(gitignorePath))
        {
            var gitignoreContent = File.ReadAllText(gitignorePath);
            if (!gitignoreContent.Contains(ralphFolder))
            {
                AnsiConsole.MarkupLine($"[dim]Tip: Add '{ralphFolder}/' to .gitignore to keep project files out of version control[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[dim]Tip: Add '{ralphFolder}/' to .gitignore to keep project files out of version control[/]");
        }
    }

    AnsiConsole.MarkupLine($"[green]Ralph folder:[/] {ralphFolder}");
}

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
    Teams = teamConfig,
    RalphFolder = ralphFolder
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

    // Teams mode in no-TUI
    if (teamsMode && teamConfig != null)
    {
        using var orchestrator = new TeamOrchestrator(config);
        var teamStopRequested = false;

        orchestrator.OnOutput += msg => Console.WriteLine($"[Teams] {msg}");
        orchestrator.OnError += msg => Console.Error.WriteLine($"[Teams ERROR] {msg}");
        orchestrator.OnStateChanged += state => Console.WriteLine($"\n--- State: {state} ---");
        orchestrator.OnAgentUpdate += stats =>
            Console.WriteLine($"[{stats.Name}] State: {stats.State}, Tasks: {stats.TasksCompleted}/{stats.TasksCompleted + stats.TasksFailed} done");
        orchestrator.OnQueueUpdate += stats =>
            Console.WriteLine($"[Queue] {stats.Completed}/{stats.Total} done, {stats.Pending} pending, {stats.Failed} failed");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            teamStopRequested = true;
            Console.WriteLine("\n[Stopping teams...]");
            orchestrator.Stop();
        };

        Console.WriteLine("[Press Ctrl+C to stop]\n");

        try
        {
            await orchestrator.RunAsync();
        }
        catch (OperationCanceledException) when (teamStopRequested)
        {
            // Expected on Ctrl+C
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Teams FATAL] {ex.Message}");
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

if (teamsMode && teamConfig != null)
{
    // Teams mode
    using var teamFileWatcher = new FileWatcher(config);
    using var teamController = new TeamController(config);
    using var teamUi = new ConsoleUI(teamController, teamFileWatcher, config);

    teamUi.AutoStart = true;
    teamFileWatcher.Start();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        teamUi.Stop();
    };

    AnsiConsole.Clear();
    await teamUi.RunAsync();

    AnsiConsole.MarkupLine("\n[yellow]Goodbye![/]");
    return 0;
}

// Create components (single agent mode)
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
