using System.Diagnostics;
using RalphController;
using RalphController.Models;
using Spectre.Console;

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

    // For local models, assume ollama provider
    return $"ollama/{model}";
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

static Task<List<string>> GetCodexModels()
{
    // Codex CLI uses --model or -m flag
    // Common models include o3, o4-mini, etc.
    var models = new List<string>
    {
        "o3",               // OpenAI o3
        "o4-mini",          // OpenAI o4-mini
        "codex-1",          // Codex-1
        "gpt-4.1"           // GPT-4.1
    };
    return Task.FromResult(models);
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
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"which {command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

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
    var providerChoices = installedProviders.Select(p => p.ToString()).ToList();

    if (providerChoices.Count == 0)
    {
        providerChoices.Add("Ollama");  // Always available via HTTP
    }

    var providerChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title($"[yellow]{label} - Select provider:[/]")
            .AddChoices(providerChoices));

    var selectedProvider = Enum.Parse<AIProvider>(providerChoice);
    string? model = null;
    string? url = null;

    // Get model based on provider
    switch (selectedProvider)
    {
        case AIProvider.Claude:
            var clModels = await GetClaudeModels();
            clModels.Add("Enter custom model...");
            model = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select Claude model:[/]")
                    .AddChoices(clModels));
            if (model == "Enter custom model...")
            {
                model = AnsiConsole.Prompt(
                    new TextPrompt<string>("[yellow]Enter model name:[/]")
                        .DefaultValue("sonnet"));
            }
            break;

        case AIProvider.Codex:
            var cxModels = await GetCodexModels();
            cxModels.Add("Enter custom model...");
            model = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select Codex model:[/]")
                    .AddChoices(cxModels));
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
                "gpt-5", "gpt-5-mini", "gpt-5.1", "gpt-5.2",
                "claude-sonnet-4", "claude-opus-4.5",
                "Enter custom model..."
            };
            model = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select Copilot model:[/]")
                    .AddChoices(copilotModels));
            if (model == "Enter custom model...")
            {
                model = AnsiConsole.Prompt(
                    new TextPrompt<string>("[yellow]Enter model name:[/]")
                        .DefaultValue("gpt-5"));
            }
            break;

        case AIProvider.Gemini:
            var gmModels = await GetGeminiModels();
            gmModels.Add("Enter custom model...");
            model = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select Gemini model:[/]")
                    .AddChoices(gmModels));
            if (model == "Enter custom model...")
            {
                model = AnsiConsole.Prompt(
                    new TextPrompt<string>("[yellow]Enter model name:[/]")
                        .DefaultValue("gemini-2.5-pro"));
            }
            break;

        case AIProvider.Cursor:
            var cursorModels = await GetCursorModels();
            cursorModels.Add("Enter custom model...");
            model = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select Cursor model:[/]")
                    .AddChoices(cursorModels));
            if (model == "Enter custom model...")
            {
                model = AnsiConsole.Prompt(
                    new TextPrompt<string>("[yellow]Enter model name:[/]")
                        .DefaultValue("claude-sonnet"));
            }
            break;

        case AIProvider.OpenCode:
            var ocModels = await GetOpenCodeModels();
            ocModels.Add("Enter custom model...");
            model = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select OpenCode model:[/]")
                    .PageSize(15)
                    .AddChoices(ocModels));
            if (model == "Enter custom model...")
            {
                model = AnsiConsole.Prompt(
                    new TextPrompt<string>("[yellow]Enter model (provider/model):[/]")
                        .DefaultValue("ollama/llama3.1:70b"));
            }
            break;

        case AIProvider.Ollama:
            url = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Ollama API URL:[/]")
                    .DefaultValue(defaultOllamaUrl ?? "http://localhost:11434"));
            var olModels = await GetOllamaModels(url);
            if (olModels.Count > 0)
            {
                olModels.Add("Enter custom model...");
                model = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[yellow]Select Ollama model:[/]")
                        .PageSize(15)
                        .AddChoices(olModels));
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
                    new TextPrompt<string>("[yellow]Enter model name:[/]")
                        .DefaultValue("llama3.1:8b"));
            }
            break;
    }

    return new ModelSpec
    {
        Provider = selectedProvider,
        Model = model ?? "",
        BaseUrl = url,
        Label = label
    };
}

static async Task<List<string>> GetOpenCodeModels()
{
    var psi = new ProcessStartInfo
    {
        FileName = "/bin/bash",
        Arguments = "-c \"opencode models\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

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
    var psi = new ProcessStartInfo
    {
        FileName = "/bin/bash",
        Arguments = "-c \"echo 'Hello from bash'\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

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

    var psi2 = new ProcessStartInfo
    {
        FileName = "/bin/bash",
        Arguments = $"-c \"claude -p --dangerously-skip-permissions < '{tempFile}'\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

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

// Check for flags
var listModels = false;
var freshMode = false;
var noTui = false;
for (int i = 0; i < args.Length; i++)
{
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

if (listModels)
{
    AnsiConsole.MarkupLine("[yellow]Listing available models...[/]");

    var psi = new ProcessStartInfo
    {
        FileName = "/bin/bash",
        Arguments = "-c \"opencode models\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

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

// Ask about multi-model configuration (skip if --fresh and already configured, or in no-tui mode)
MultiModelConfig? multiModelConfig = null;
if (!noTui && (freshMode || projectSettings.MultiModel == null || !projectSettings.MultiModel.IsEnabled))
{
    var multiModelChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("\n[yellow]Multi-model configuration:[/]")
            .AddChoices(
                "Single model (default)",
                "Verification model - use a second model to verify completion",
                "Round-robin rotation - alternate between models each iteration"));

    if (multiModelChoice != "Single model (default)")
    {
        var strategy = multiModelChoice.StartsWith("Verification")
            ? ModelSwitchStrategy.Verification
            : ModelSwitchStrategy.RoundRobin;

        // Create primary model spec from current selection
        var primaryModel = new ModelSpec
        {
            Provider = provider,
            Model = provider switch
            {
                AIProvider.Claude => claudeModel ?? "sonnet",
                AIProvider.Codex => codexModel ?? "o3",
                AIProvider.Copilot => copilotModel ?? "gpt-5",
                AIProvider.Gemini => geminiModel ?? "gemini-2.5-pro",
                AIProvider.Cursor => cursorModel ?? "claude-sonnet",
                AIProvider.OpenCode => openCodeModel ?? "",
                AIProvider.Ollama => ollamaModel ?? "llama3.1:8b",
                _ => ""
            },
            BaseUrl = provider == AIProvider.Ollama ? ollamaUrl : null,
            Label = "Primary"
        };

        var modelList = new List<ModelSpec> { primaryModel };

        if (strategy == ModelSwitchStrategy.Verification)
        {
            // For verification, only need one verifier model
            AnsiConsole.MarkupLine("\n[yellow]Select verifier model:[/]");
            var verifierModel = await PromptForModelSpec("Verifier", ollamaUrl);
            if (verifierModel != null)
            {
                modelList.Add(verifierModel);
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

                // Ask if they want to add another model
                addMore = AnsiConsole.Confirm("[yellow]Add another model to the rotation?[/]", false);
            }
        }

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
else if (!freshMode && projectSettings.MultiModel?.IsEnabled == true)
{
    // Use saved multi-model config
    multiModelConfig = projectSettings.MultiModel;
    var modelNames = string.Join(" + ", multiModelConfig.Models.Select(m => m.DisplayName));
    AnsiConsole.MarkupLine($"[dim]Using saved multi-model config: {multiModelConfig.Strategy} - {modelNames}[/]");
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
    MultiModel = multiModelConfig
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
