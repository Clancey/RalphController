using System.Diagnostics;
using RalphController;
using RalphController.Models;
using Spectre.Console;

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
    singleController.OnIterationStart += iter =>
    {
        AnsiConsole.MarkupLine($"[blue]>>> Starting iteration {iter}[/]");
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
        Provider = AIProvider.Claude,
        ProviderConfig = AIProviderConfig.ForClaude()
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

// Check for flags
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--provider" && i + 1 < args.Length)
    {
        providerFromArgs = args[i + 1].ToLower() switch
        {
            "codex" => AIProvider.Codex,
            "claude" => AIProvider.Claude,
            "copilot" => AIProvider.Copilot,
            _ => null
        };
    }
    else if (args[i] == "--model" && i + 1 < args.Length)
    {
        modelFromArgs = args[i + 1];
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

// Load project settings
var projectSettings = ProjectSettings.Load(targetDir);
var savedProvider = projectSettings.Provider;

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
    // Prompt for provider
    provider = AnsiConsole.Prompt(
        new SelectionPrompt<AIProvider>()
            .Title("[yellow]Select AI provider:[/]")
            .AddChoices(AIProvider.Claude, AIProvider.Codex, AIProvider.Copilot));
    providerWasSelected = true;
}

AnsiConsole.MarkupLine($"[green]Provider:[/] {provider}");

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
    else if (!string.IsNullOrEmpty(projectSettings.CopilotModel))
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

// Save provider to project settings if it changed or was newly selected
if (providerWasSelected || (providerFromArgs.HasValue && providerFromArgs != savedProvider))
{
    projectSettings.Provider = provider;
}
projectSettings.Save(targetDir);

// Create configuration
var providerConfig = provider switch
{
    AIProvider.Codex => AIProviderConfig.ForCodex(),
    AIProvider.Copilot => AIProviderConfig.ForCopilot(model: copilotModel),
    _ => AIProviderConfig.ForClaude()
};

var config = new RalphConfig
{
    TargetDirectory = targetDir,
    Provider = provider,
    ProviderConfig = providerConfig
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

    var action = AnsiConsole.Prompt(
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
