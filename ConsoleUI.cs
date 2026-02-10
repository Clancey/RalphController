using System.Collections.Concurrent;
using System.Text;
using RalphController.Models;
using RalphController.Parallel;
using Spectre.Console;

namespace RalphController;

/// <summary>
/// Rich console UI using Spectre.Console
/// </summary>
public class ConsoleUI : IDisposable
{
    private readonly LoopController? _controller;
    private readonly FileWatcher _fileWatcher;
    private readonly RalphConfig _config;
    private readonly ConcurrentQueue<string> _outputLines = new();
    private readonly StringBuilder _streamBuffer = new();
    private readonly object _bufferLock = new();
    private int _maxOutputLines = 30;
    private CancellationTokenSource? _uiCts;
    private Task? _inputTask;
    private bool _disposed;
    private int _lastConsoleWidth;
    private int _lastConsoleHeight;
    private bool _isInInjectMode;
    private string _injectInput = "";
    private int _injectStep; // 0=prompt, 1=confirm, 2=select provider, 3=select model, 4=custom model
    private string _originalPrompt = "";
    private List<string> _injectOptions = new();
    private int _selectedOptionIndex;
    private string _selectedProvider = "";
    private ModelSpec? _selectedModel;

    // Teams mode fields
    private readonly TeamController? _teamController;
    private readonly ConcurrentDictionary<string, AgentStatistics> _agentStats = new();
    private TaskQueueStatistics _queueStats = new() { Total = 0 };

    /// <summary>
    /// Whether to automatically start the loop when RunAsync is called
    /// </summary>
    public bool AutoStart { get; set; }

    /// <summary>
    /// Whether the UI is running in teams mode
    /// </summary>
    public bool IsTeamsMode => _teamController != null;

    public ConsoleUI(LoopController controller, FileWatcher fileWatcher, RalphConfig config)
    {
        _controller = controller;
        _fileWatcher = fileWatcher;
        _config = config;

        // Subscribe to controller events - ANSI codes will be converted to Spectre markup
        // Buffer streaming output and emit complete lines
        _controller.OnOutput += text => AddStreamingOutput(text);
        _controller.OnError += line => AddOutputLine(line, isRawOutput: true, isError: true);
        _controller.OnIterationStart += (iter, modelName) =>
        {
            var modelSuffix = modelName != null ? $" [[{Markup.Escape(modelName)}]]" : "";
            AddOutputLine($"[blue]>>> Starting iteration {iter}{modelSuffix}[/]");
        };
        _controller.OnIterationComplete += (iter, result) =>
        {
            FlushStreamBuffer(); // Flush any remaining buffered output
            var status = result.Success ? "[green]SUCCESS[/]" : "[red]FAILED[/]";
            AddOutputLine($"[blue]<<< Iteration {iter} complete: {status}[/]");
        };
    }

    /// <summary>
    /// Constructor for teams mode
    /// </summary>
    public ConsoleUI(TeamController teamController, FileWatcher fileWatcher, RalphConfig config)
    {
        _teamController = teamController;
        _fileWatcher = fileWatcher;
        _config = config;

        WireTeamControllerEvents();
    }

    /// <summary>
    /// Start the UI and run until stopped
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _uiCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Track initial console size
        _lastConsoleWidth = Console.WindowWidth;
        _lastConsoleHeight = Console.WindowHeight;

        // Calculate initial max output lines based on available space
        UpdateMaxOutputLines();

        // Start input handler on background thread
        _inputTask = Task.Run(() => HandleInputAsync(_uiCts.Token), _uiCts.Token);

        if (IsTeamsMode)
        {
            // Auto-start the team controller
            if (AutoStart && _teamController!.State == TeamControllerState.Idle)
            {
                _ = _teamController.StartAsync();
                AddOutputLine("[green]>>> Auto-starting teams...[/]");
            }

            // Run the live display with teams layout
            await AnsiConsole.Live(BuildTeamsLayout())
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async ctx =>
                {
                    while (!_uiCts.Token.IsCancellationRequested)
                    {
                        if (Console.WindowWidth != _lastConsoleWidth || Console.WindowHeight != _lastConsoleHeight)
                        {
                            _lastConsoleWidth = Console.WindowWidth;
                            _lastConsoleHeight = Console.WindowHeight;
                            UpdateMaxOutputLines();
                            AnsiConsole.Clear();
                        }

                        ctx.UpdateTarget(BuildTeamsLayout());
                        ctx.Refresh();
                        await Task.Delay(100, _uiCts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    }
                });
        }
        else
        {
            // Auto-start the loop if enabled
            if (AutoStart && _controller!.State == LoopState.Idle)
            {
                _ = _controller.StartAsync();
                AddOutputLine("[green]>>> Auto-starting loop...[/]");
            }

            // Run the live display
            await AnsiConsole.Live(BuildLayout())
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async ctx =>
                {
                    while (!_uiCts.Token.IsCancellationRequested)
                    {
                        // Check for console resize
                        if (Console.WindowWidth != _lastConsoleWidth || Console.WindowHeight != _lastConsoleHeight)
                        {
                            _lastConsoleWidth = Console.WindowWidth;
                            _lastConsoleHeight = Console.WindowHeight;

                            // Recalculate max output lines based on available space
                            UpdateMaxOutputLines();

                            // Clear and redraw on resize
                            AnsiConsole.Clear();
                        }

                        ctx.UpdateTarget(BuildLayout());
                        ctx.Refresh();
                        await Task.Delay(100, _uiCts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    }
                });
        }
    }

    /// <summary>
    /// Stop the UI
    /// </summary>
    public void Stop()
    {
        _uiCts?.Cancel();
    }

    /// <summary>
    /// Calculate the maximum number of output lines based on available console height
    /// </summary>
    private void UpdateMaxOutputLines()
    {
        // Layout structure:
        // - Header: Size(3) - includes border, ~1 line content
        // - Main section: 
        //   - Output: Ratio(4) - gets remaining space after Plan
        //   - Plan: Size(6) - fixed at 6 lines including border
        // - Footer: Size(3) - includes border, ~1 line content
        //
        // Calculation:
        // Main section height = TotalHeight - Header(3) - Footer(3) = TotalHeight - 6
        // Output panel height = MainHeight - Plan(6) = TotalHeight - 12
        // Output content area = OutputHeight - border(2) = TotalHeight - 14
        
        var totalHeight = Console.WindowHeight;
        var reservedSpace = 14; // Header(3) + Plan(6) + Footer(3) + Output border(2)
        var availableForOutput = totalHeight - reservedSpace;
        
        // Ensure minimum of 10 lines and maximum reasonable limit
        _maxOutputLines = Math.Max(10, Math.Min(availableForOutput, 200));
    }

    private Layout BuildLayout()
    {
        // If in inject mode, show a special overlay layout
        if (_isInInjectMode)
        {
            return BuildInjectLayout();
        }

        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(3),
                new Layout("Main").SplitRows(
                    new Layout("Output").Ratio(4),
                    new Layout("Plan").Size(6)
                ),
                new Layout("Footer").Size(3)
            );

        layout["Header"].Update(BuildHeaderPanel());
        layout["Output"].Update(BuildOutputPanel());
        layout["Plan"].Update(BuildPlanPanel());
        layout["Footer"].Update(BuildFooterPanel());

        return layout;
    }

    private Layout BuildInjectLayout()
    {
        var layout = new Layout("Root").SplitRows(
            new Layout("Title").Size(8),
            new Layout("Content"),
            new Layout("Footer").Size(3)
        );

        // Title - centered INJECT text with larger display
        var titleStyle = new Style(Color.Yellow, Color.Black).Decoration(Decoration.Bold);
        var titlePanel = new Panel(
            Align.Center(
                new Text("INJECT", titleStyle).Centered(),
                VerticalAlignment.Middle
            )
        )
        .Border(BoxBorder.Double)
        .BorderColor(Color.Yellow)
        .Expand();
        layout["Title"].Update(titlePanel);

        // Content based on step
        string content = _injectStep switch
        {
            0 => $"[bold yellow]Enter prompt to inject:[/]\n\n[yellow]> {Markup.Escape(_injectInput)}_[/]",
            1 => BuildModelConfirmationContent(),
            2 => BuildProviderSelectionContent(),
            3 => BuildModelSelectionContent(),
            4 => $"[bold yellow]Enter custom model name:[/]\n\n[yellow]Provider:[/] {Markup.Escape(_selectedProvider)}\n[yellow]> {Markup.Escape(_injectInput)}_[/]",
            _ => ""
        };
        layout["Content"].Update(new Panel(new Markup(content)).Border(BoxBorder.Rounded).BorderColor(Color.Yellow).Expand());

        // Footer with instructions
        string footer = _injectStep switch
        {
            0 => "[dim]Type | Enter=Submit | Esc=Cancel[/]",
            1 => "[dim]Y=Different model | N=Use current | Esc=Cancel[/]",
            2 => "[dim]↑↓ Navigate | Enter=Select | Esc=Cancel[/]",
            3 => "[dim]↑↓ Navigate | Enter=Select | Esc=Cancel[/]",
            4 => "[dim]Type model name | Enter=Submit | Esc=Cancel[/]",
            _ => ""
        };
        layout["Footer"].Update(new Panel(new Markup(footer).Centered()).Border(BoxBorder.Rounded).BorderColor(Color.Grey).Expand());

        return layout;
    }

    private string BuildProviderSelectionContent()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[bold yellow]Select provider:[/]");
        sb.AppendLine();
        for (int i = 0; i < _injectOptions.Count; i++)
        {
            var isSelected = i == _selectedOptionIndex;
            var prefix = isSelected ? "[yellow]>[/] " : "  ";
            var optionText = Markup.Escape(_injectOptions[i]);
            var formattedOption = isSelected ? $"[bold yellow]{optionText}[/]" : optionText;
            sb.AppendLine($"{prefix}{formattedOption}");
        }
        return sb.ToString();
    }

    private string BuildModelSelectionContent()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[bold yellow]Select {Markup.Escape(_selectedProvider)} model:[/]");
        sb.AppendLine();
        int visibleStart = Math.Max(0, _selectedOptionIndex - 4);
        int visibleEnd = Math.Min(_injectOptions.Count, _selectedOptionIndex + 5);
        for (int i = visibleStart; i < visibleEnd; i++)
        {
            var isSelected = i == _selectedOptionIndex;
            var prefix = isSelected ? "[yellow]>[/] " : "  ";
            var optionText = Markup.Escape(_injectOptions[i]);
            var formattedOption = isSelected ? $"[bold yellow]{optionText}[/]" : optionText;
            sb.AppendLine($"{prefix}{formattedOption}");
        }
        if (_injectOptions.Count > 8)
        {
            sb.AppendLine($"[dim]... ({_injectOptions.Count - 8} more)[/]");
        }
        return sb.ToString();
    }

    private string BuildModelConfirmationContent()
    {
        var currentModel = _controller!.ModelSelector.GetCurrentModel();
        var modelName = currentModel?.DisplayName ?? _config.Provider.ToString();
        var safePrompt = string.IsNullOrEmpty(_originalPrompt) ? "(empty)" : _originalPrompt;
        var promptPreview = safePrompt.Length > 60
            ? safePrompt[..60] + "..."
            : safePrompt;
        return $"[bold yellow]Current model:[/] [green]{Markup.Escape(modelName)}[/]\n\n[yellow]Your prompt:[/] {Markup.Escape(promptPreview)}\n\n[bold]Use different model?[/] [dim](Y/N)[/]";
    }

    private Panel BuildHeaderPanel()
    {
        var state = _controller!.State;
        var stats = _controller.Statistics;
        var provider = _config.Provider;

        // Get current model name for multi-model mode
        var currentModel = _controller.ModelSelector.GetCurrentModel();
        var modelDisplay = (_config.MultiModel?.IsEnabled == true && currentModel != null)
            ? $" [[{Markup.Escape(currentModel.DisplayName)}]]"
            : "";

        var stateColor = state switch
        {
            LoopState.Running => "green",
            LoopState.Paused => "yellow",
            LoopState.Stopping => "red",
            _ => "grey"
        };

        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("")
            .AddColumn("")
            .AddColumn("")
            .AddColumn("");

        table.AddRow(
            $"[bold blue]RALPH CONTROLLER[/]",
            $"[{stateColor}][[{state.ToString().ToUpper()}]][/]",
            $"[white]Iteration #{stats.CurrentIteration}{modelDisplay}[/]",
            $"[dim]{provider}[/]"
        );

        table.AddRow(
            $"[dim]{Markup.Escape(_config.TargetDirectory)}[/]",
            $"[dim]Duration: {stats.FormatDuration(stats.TotalDuration)}[/]",
            $"[dim]Est: {stats.FormatCost(stats.EstimatedCost)}[/]",
            _fileWatcher.PromptChanged ? "[yellow]PROMPT CHANGED[/]" : ""
        );

        return new Panel(table)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue);
    }

    private Panel BuildOutputPanel()
    {
        var lines = _outputLines.ToArray();
        if (lines.Length == 0)
        {
            return new Panel(new Markup("[dim]Waiting for output...[/]"))
                .Header("[bold]Output[/]")
                .Border(BoxBorder.Rounded)
                .Expand();
        }

        // Validate each line individually - escape lines that fail markup parsing
        var validatedLines = new List<string>();
        foreach (var line in lines)
        {
            try
            {
                // Test if the line can be parsed as markup
                _ = new Markup(line);
                validatedLines.Add(line);
            }
            catch
            {
                // Line has invalid markup - escape it entirely
                validatedLines.Add(Markup.Escape(line));
            }
        }

        var content = string.Join("\n", validatedLines);
        return new Panel(new Markup(content))
            .Header("[bold]Output[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private Panel BuildPlanPanel()
    {
        var planLines = _fileWatcher.ReadPlanLinesAsync(4).GetAwaiter().GetResult();
        var content = planLines.Length > 0
            ? string.Join("\n", planLines.Select(Markup.Escape))
            : "[dim]No implementation plan found[/]";

        return new Panel(new Markup(content))
            .Header($"[bold]{Markup.Escape(_config.PlanFile)}[/]")
            .Border(BoxBorder.Rounded);
    }

    private Panel BuildFooterPanel()
    {
        // Double brackets to escape them in Spectre.Console markup
        var controls = _controller!.State switch
        {
            LoopState.Running => "[[P]]ause  [[N]]ext  [[S]]top  [[F]]orce Stop  [[I]]nject  [[Q]]uit",
            LoopState.Paused => "[[R]]esume  [[S]]top  [[I]]nject  [[Q]]uit",
            LoopState.Idle => "[[Enter]] Start  [[Q]]uit",
            LoopState.Stopping => "[[F]]orce Stop  [[Q]]uit",
            _ => "[[Q]]uit"
        };

        return new Panel(new Markup($"[bold]{controls}[/]"))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
    }

    private async Task HandleInputAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                await HandleKeyAsync(key);
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task HandleKeyAsync(ConsoleKeyInfo key)
    {
        // Handle inject mode input (single-agent only)
        if (_isInInjectMode)
        {
            await HandleInjectInput(key);
            return;
        }

        // Dispatch to teams key handler if in teams mode
        if (IsTeamsMode)
        {
            await HandleTeamsKeyAsync(key);
            return;
        }

        // Normal mode handling (single-agent only)
        switch (char.ToLower(key.KeyChar))
        {
            case 'p':
                if (_controller!.State == LoopState.Running)
                {
                    _controller.Pause();
                    AddOutputLine("[yellow]>>> Loop paused[/]");
                }
                break;

            case 'r':
                if (_controller!.State == LoopState.Paused)
                {
                    _controller.Resume();
                    AddOutputLine("[green]>>> Loop resumed[/]");
                }
                break;

            case 'n':
                if (_controller!.State == LoopState.Running)
                {
                    _controller.SkipIteration();
                    AddOutputLine("[yellow]>>> Skipping to next iteration...[/]");
                }
                break;

            case 's':
                if (_controller!.State == LoopState.Running || _controller.State == LoopState.Paused)
                {
                    _controller.Stop();
                    AddOutputLine("[red]>>> Stopping after current iteration...[/]");
                }
                break;

            case 'f':
                if (_controller!.State != LoopState.Idle)
                {
                    await _controller.ForceStopAsync();
                    AddOutputLine("[red]>>> Force stopped![/]");
                }
                break;

            case 'i':
                StartInjectMode();
                break;

            case 'q':
                Stop();
                break;

            case '\r':
            case '\n':
                if (_controller!.State == LoopState.Idle)
                {
                    // Start loop in background
                    _ = _controller.StartAsync();
                    AddOutputLine("[green]>>> Starting loop...[/]");
                }
                break;
        }
    }

    private void StartInjectMode()
    {
        _isInInjectMode = true;
        _injectStep = 0;
        _injectInput = "";
        _originalPrompt = "";
        _selectedOptionIndex = 0;
        _selectedProvider = "";
        _selectedModel = null;
        _injectOptions = new List<string>();
    }

    private void EndInjectMode()
    {
        _isInInjectMode = false;
        _injectStep = 0;
        _injectInput = "";
        _originalPrompt = "";
        _selectedOptionIndex = 0;
        _selectedProvider = "";
        _selectedModel = null;
        _injectOptions = new List<string>();
    }

    private async Task HandleInjectInput(ConsoleKeyInfo key)
    {
        // Handle Escape to cancel
        if (key.Key == ConsoleKey.Escape)
        {
            EndInjectMode();
            return;
        }

        // Handle Enter based on step
        if (key.Key == ConsoleKey.Enter)
        {
            await SubmitInjectStep();
            return;
        }

        // Step 0: Text input for prompt
        if (_injectStep == 0)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (_injectInput.Length > 0)
                    _injectInput = _injectInput[..^1];
            }
            else if (!char.IsControl(key.KeyChar))
            {
                _injectInput += key.KeyChar;
            }
            return;
        }

        // Step 1: Y/N confirmation
        if (_injectStep == 1)
        {
            char c = char.ToLower(key.KeyChar);
            if (c == 'y')
            {
                // Move to provider selection
                _injectStep = 2;
                _selectedOptionIndex = 0;
                PopulateProviderOptions();
            }
            else if (c == 'n')
            {
                // Use current model
                _controller!.InjectPrompt(_originalPrompt);
                AddOutputLine($"[yellow]>>> Prompt: {Markup.Escape(_originalPrompt.Length > 50 ? _originalPrompt[..50] + "..." : _originalPrompt)}[/]");
                EndInjectMode();
            }
            return;
        }

        // Step 4: Custom model name input
        if (_injectStep == 4)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (_injectInput.Length > 0)
                    _injectInput = _injectInput[..^1];
            }
            else if (!char.IsControl(key.KeyChar))
            {
                _injectInput += key.KeyChar;
            }
            return;
        }

        // Steps 2 & 3: Arrow key navigation
        if (_injectStep == 2 || _injectStep == 3)
        {
            if (key.Key == ConsoleKey.UpArrow || key.Key == ConsoleKey.LeftArrow)
            {
                _selectedOptionIndex = Math.Max(0, _selectedOptionIndex - 1);
            }
            else if (key.Key == ConsoleKey.DownArrow || key.Key == ConsoleKey.RightArrow)
            {
                _selectedOptionIndex = Math.Min(_injectOptions.Count - 1, _selectedOptionIndex + 1);
            }
            return;
        }
    }

    private void PopulateProviderOptions()
    {
        _injectOptions = new List<string>
        {
            "Claude",
            "Codex",
            "Copilot",
            "Gemini",
            "Cursor",
            "OpenCode",
            "Ollama"
        };
    }

    private async Task PopulateModelOptionsAsync()
    {
        _injectOptions = new List<string>();
        _selectedOptionIndex = 0;

        switch (_selectedProvider)
        {
            case "Claude":
                _injectOptions.AddRange(new[] { "sonnet", "opus", "haiku", "Enter custom..." });
                break;
            case "Codex":
                _injectOptions.AddRange(new[] { "o3", "o1", "gpt-5.2-codex", "gpt-5.1-codex", "Enter custom..." });
                break;
            case "Copilot":
                _injectOptions.AddRange(new[] { "gpt-5", "gpt-5-mini", "gpt-5.1", "claude-sonnet-4", "Enter custom..." });
                break;
            case "Gemini":
                _injectOptions.AddRange(new[] { "gemini-2.5-pro", "gemini-2.0-flash", "Enter custom..." });
                break;
            case "Cursor":
                _injectOptions.AddRange(new[] { "claude-sonnet", "claude-opus", "gpt-4o", "Enter custom..." });
                break;
            case "OpenCode":
                _injectOptions.AddRange(new[] { "ollama/llama3.1:70b", "ollama/deepseek-r1:32b", "ollama/qwen2.5:72b", "Enter custom..." });
                break;
            case "Ollama":
                _injectOptions.AddRange(new[] { "llama3.1:8b", "llama3.1:70b", "deepseek-r1:32b", "qwen2.5:72b", "Enter custom..." });
                break;
        }
    }

    private async Task SubmitInjectStep()
    {
        switch (_injectStep)
        {
            case 0: // Prompt entry complete
                if (!string.IsNullOrWhiteSpace(_injectInput))
                {
                    _originalPrompt = _injectInput;
                    _injectInput = "";
                    _injectStep = 1;
                }
                else
                {
                    EndInjectMode();
                }
                break;

            case 2: // Provider selected
                _selectedProvider = _injectOptions[_selectedOptionIndex];
                _injectStep = 3;
                _selectedOptionIndex = 0;
                await PopulateModelOptionsAsync();
                break;

            case 3: // Model selected
                var modelName = _injectOptions[_selectedOptionIndex];
                if (modelName == "Enter custom...")
                {
                    // Move to custom model input
                    _injectStep = 4;
                    _injectInput = "";
                }
                else
                {
                    var provider = Enum.Parse<AIProvider>(_selectedProvider, true);
                    _selectedModel = new ModelSpec
                    {
                        Provider = provider,
                        Model = modelName,
                        Label = modelName
                    };
                    _controller!.InjectPrompt(_originalPrompt, _selectedModel);
                    AddOutputLine($"[yellow]>>> Injected with model: {Markup.Escape($"{_selectedProvider}/{modelName}")}[/]");
                    AddOutputLine($"[yellow]>>> Prompt: {Markup.Escape(_originalPrompt.Length > 50 ? _originalPrompt[..50] + "..." : _originalPrompt)}[/]");
                    EndInjectMode();
                }
                break;

            case 4: // Custom model name entered
                if (!string.IsNullOrWhiteSpace(_injectInput))
                {
                    var provider = Enum.Parse<AIProvider>(_selectedProvider, true);
                    _selectedModel = new ModelSpec
                    {
                        Provider = provider,
                        Model = _injectInput,
                        Label = _injectInput
                    };
                    _controller!.InjectPrompt(_originalPrompt, _selectedModel);
                    AddOutputLine($"[yellow]>>> Injected with model: {Markup.Escape($"{_selectedProvider}/{_injectInput}")}[/]");
                    AddOutputLine($"[yellow]>>> Prompt: {Markup.Escape(_originalPrompt.Length > 50 ? _originalPrompt[..50] + "..." : _originalPrompt)}[/]");
                    EndInjectMode();
                }
                break;
        }
    }

    private void AddOutputLine(string line, bool isRawOutput = false, bool isError = false)
    {
        // Skip empty lines to save space
        if (string.IsNullOrWhiteSpace(line))
            return;

        // Only process raw AI output - internal messages already have Spectre markup
        if (isRawOutput)
        {
            // First, strip ALL ANSI escape sequences completely (including cursor movement, clear screen, etc.)
            line = StripAllAnsiSequences(line);

            // Escape any Spectre markup characters in the raw output
            line = Markup.Escape(line);

            // Prefix with "OUT:" to show it's AI output
            line = $"[green]OUT:[/] {line}";

            // If it's an error and doesn't have any color markup, wrap in red
            if (isError)
            {
                line = $"[red]ERR: {Markup.Escape(line)}[/]";
            }
        }

        // Remove control characters that can mess up the layout
        line = new string(line.Where(c => !char.IsControl(c) || c == ' ').ToArray());

        // Truncate long lines to prevent layout issues
        var maxLineLength = Math.Max(40, Console.WindowWidth - 15);
        if (line.Length > maxLineLength)
        {
            // Find a safe truncation point (not inside markup)
            var truncated = TruncatePreservingMarkup(line, maxLineLength);
            line = truncated + "...";
        }

        _outputLines.Enqueue(line);

        // Keep only last N lines
        while (_outputLines.Count > _maxOutputLines)
        {
            _outputLines.TryDequeue(out _);
        }
    }

    private void AddStreamingOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        lock (_bufferLock)
        {
            _streamBuffer.Append(text);

            // Process complete lines
            var content = _streamBuffer.ToString();
            var lastNewline = content.LastIndexOf('\n');

            if (lastNewline >= 0)
            {
                // Extract complete lines
                var completeLines = content.Substring(0, lastNewline);
                var remainder = content.Substring(lastNewline + 1);

                // Clear buffer and keep remainder
                _streamBuffer.Clear();
                _streamBuffer.Append(remainder);

                // Add each complete line
                foreach (var line in completeLines.Split('\n'))
                {
                    AddOutputLine(line, isRawOutput: true);
                }
            }
            else if (_streamBuffer.Length > 200)
            {
                // Buffer getting large without newlines - flush as partial line
                var partial = _streamBuffer.ToString();
                _streamBuffer.Clear();
                AddOutputLine(partial, isRawOutput: true);
            }
        }
    }

    /// <summary>
    /// Flush any remaining buffered streaming content
    /// </summary>
    public void FlushStreamBuffer()
    {
        lock (_bufferLock)
        {
            if (_streamBuffer.Length > 0)
            {
                AddOutputLine(_streamBuffer.ToString(), isRawOutput: true);
                _streamBuffer.Clear();
            }
        }
    }

    /// <summary>
    /// Strip ALL ANSI escape sequences from a string (cursor movement, colors, clear screen, etc.)
    /// This is more aggressive than ConvertAnsiToMarkup and removes everything
    /// </summary>
    private static string StripAllAnsiSequences(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var result = new System.Text.StringBuilder();
        var i = 0;

        while (i < input.Length)
        {
            // Check for ANSI escape sequence (ESC followed by [ or other control chars)
            if (input[i] == '\x1B')
            {
                // Skip the escape character and find the end of the sequence
                i++;
                if (i < input.Length && input[i] == '[')
                {
                    // CSI sequence - skip until we hit a letter (the command)
                    i++;
                    while (i < input.Length && !char.IsLetter(input[i]))
                        i++;
                    if (i < input.Length)
                        i++; // Skip the command letter too
                }
                else if (i < input.Length)
                {
                    // Other escape sequence - skip next character
                    i++;
                }
                continue;
            }

            // Skip other problematic control characters
            if (input[i] == '\r')
            {
                i++;
                continue;
            }

            result.Append(input[i]);
            i++;
        }

        return result.ToString();
    }

    private static string ConvertAnsiToMarkup(string input)
    {
        // Only convert ANSI escape codes to Spectre markup
        // Leave everything else unchanged - BuildOutputPanel validates per-line
        var result = new System.Text.StringBuilder();
        var i = 0;

        while (i < input.Length)
        {
            // Check for ANSI escape sequence (ESC[...m)
            if (i < input.Length - 1 && input[i] == '\x1B' && input[i + 1] == '[')
            {
                var start = i + 2;
                var end = start;
                while (end < input.Length && !char.IsLetter(input[end]))
                    end++;

                if (end < input.Length && input[end] == 'm')
                {
                    var code = input[start..end];
                    var markup = AnsiCodeToSpectreMarkup(code);
                    if (markup != null)
                        result.Append(markup);
                    i = end + 1;
                    continue;
                }
            }

            result.Append(input[i]);
            i++;
        }

        return result.ToString();
    }

    private static string? AnsiCodeToSpectreMarkup(string code)
    {
        // Handle multiple codes separated by semicolon
        var codes = code.Split(';');
        var result = new System.Text.StringBuilder();

        foreach (var c in codes)
        {
            if (!int.TryParse(c, out var num))
                continue;

            var markup = num switch
            {
                0 => "[/]", // Reset
                1 => "[bold]",
                2 => "[dim]",
                3 => "[italic]",
                4 => "[underline]",
                30 => "[black]",
                31 => "[red]",
                32 => "[green]",
                33 => "[yellow]",
                34 => "[blue]",
                35 => "[magenta]",
                36 => "[cyan]",
                37 => "[white]",
                39 => "[/]", // Default foreground
                90 => "[grey]",
                91 => "[red]",
                92 => "[green]",
                93 => "[yellow]",
                94 => "[blue]",
                95 => "[magenta]",
                96 => "[cyan]",
                97 => "[white]",
                _ => null
            };

            if (markup != null)
                result.Append(markup);
        }

        return result.Length > 0 ? result.ToString() : null;
    }

    private static string TruncatePreservingMarkup(string line, int maxLength)
    {
        // Simple truncation that tries to close any open markup tags
        var visibleLength = 0;
        var inMarkup = false;
        var truncateAt = 0;

        for (var i = 0; i < line.Length && visibleLength < maxLength; i++)
        {
            if (line[i] == '[' && i + 1 < line.Length && line[i + 1] != '[')
            {
                inMarkup = true;
            }
            else if (line[i] == ']' && inMarkup)
            {
                inMarkup = false;
            }
            else if (!inMarkup)
            {
                visibleLength++;
                truncateAt = i + 1;
            }
        }

        return line[..truncateAt];
    }

    // ───────────────────────────────────────────────────────────
    // Teams mode methods
    // ───────────────────────────────────────────────────────────

    /// <summary>
    /// Wire TeamController events to the output lines and internal state
    /// </summary>
    private void WireTeamControllerEvents()
    {
        if (_teamController == null) return;

        _teamController.OnOutput += msg =>
            AddOutputLine($"[green]>>>[/] {Markup.Escape(msg)}");

        _teamController.OnError += msg =>
            AddOutputLine($"[red]ERR:[/] {Markup.Escape(msg)}");

        _teamController.OnPhaseChanged += phase =>
            AddOutputLine($"[blue]--- Phase: {phase} ---[/]");

        _teamController.OnAgentUpdate += stats =>
            _agentStats[stats.AgentId] = stats;

        _teamController.OnQueueUpdate += stats =>
            _queueStats = stats;

        _teamController.OnStateChanged += state =>
            AddOutputLine($"[yellow]State: {state}[/]");

        _teamController.OnVerificationComplete += result =>
        {
            var status = result.AllTasksComplete ? "[green]PASSED[/]" : "[red]ISSUES FOUND[/]";
            AddOutputLine($"[blue]Verification: {status}[/]");
            if (!string.IsNullOrEmpty(result.Summary))
                AddOutputLine($"[dim]{Markup.Escape(result.Summary)}[/]");
        };
    }

    /// <summary>
    /// Build the teams mode layout
    /// </summary>
    private Layout BuildTeamsLayout()
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(3),
                new Layout("Main").SplitColumns(
                    new Layout("Output").Ratio(3),
                    new Layout("Agents").Ratio(2)
                ),
                new Layout("Queue").Size(5),
                new Layout("Footer").Size(3)
            );

        layout["Header"].Update(BuildTeamsHeaderPanel());
        layout["Output"].Update(BuildOutputPanel()); // Reuse existing output panel
        layout["Agents"].Update(BuildAgentsPanel());
        layout["Queue"].Update(BuildQueuePanel());
        layout["Footer"].Update(BuildTeamsFooterPanel());

        return layout;
    }

    /// <summary>
    /// Build the teams header panel showing title, phase, and state
    /// </summary>
    private Panel BuildTeamsHeaderPanel()
    {
        var state = _teamController!.State;
        var phase = _teamController.CurrentPhase;
        var agentCount = _agentStats.Count;

        var stateColor = state switch
        {
            TeamControllerState.Running => "green",
            TeamControllerState.Initializing => "yellow",
            TeamControllerState.Stopping => "red",
            TeamControllerState.Failed => "red",
            TeamControllerState.Stopped => "grey",
            _ => "grey"
        };

        var phaseDisplay = phase switch
        {
            TeamPhase.Decomposing => "[yellow]DECOMPOSING[/]",
            TeamPhase.Executing => "[green]EXECUTING[/]",
            TeamPhase.Verifying => "[cyan]VERIFYING[/]",
            TeamPhase.Complete => "[green]COMPLETE[/]",
            _ => "[dim]IDLE[/]"
        };

        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("")
            .AddColumn("")
            .AddColumn("")
            .AddColumn("");

        table.AddRow(
            $"[bold blue]RALPH TEAMS[/]",
            $"[{stateColor}][[{state.ToString().ToUpper()}]][/]",
            phaseDisplay,
            $"[dim]{agentCount} agent{(agentCount != 1 ? "s" : "")}[/]"
        );

        table.AddRow(
            $"[dim]{Markup.Escape(_config.TargetDirectory)}[/]",
            "",
            "",
            ""
        );

        return new Panel(table)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue);
    }

    /// <summary>
    /// Build the agents panel showing each agent's status
    /// </summary>
    private Panel BuildAgentsPanel()
    {
        var agents = _agentStats.Values.ToArray();

        if (agents.Length == 0)
        {
            return new Panel(new Markup("[dim]No agents running...[/]"))
                .Header("[bold]Agents[/]")
                .Border(BoxBorder.Rounded)
                .Expand();
        }

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("Agent")
            .AddColumn("State")
            .AddColumn("Task")
            .AddColumn("Done/Fail");

        foreach (var agent in agents.OrderBy(a => a.Name))
        {
            var stateColor = agent.State switch
            {
                ParallelAgentState.Running => "green",
                ParallelAgentState.Initializing => "yellow",
                ParallelAgentState.Waiting => "cyan",
                ParallelAgentState.Merging => "magenta",
                ParallelAgentState.Failed => "red",
                ParallelAgentState.Stopped => "grey",
                _ => "dim"
            };

            var nameDisplay = agent.Name;
            if (agent.AssignedModel != null)
                nameDisplay += $"\n[dim]{Markup.Escape(agent.AssignedModel.DisplayName)}[/]";

            var taskDisplay = agent.CurrentTask != null
                ? Markup.Escape(TruncateString(agent.CurrentTask.Title ?? agent.CurrentTask.Description, 30))
                : "[dim]---[/]";

            var countsDisplay = agent.TasksFailed > 0
                ? $"[green]{agent.TasksCompleted}[/]/[red]{agent.TasksFailed}[/]"
                : $"[green]{agent.TasksCompleted}[/]/0";

            table.AddRow(
                new Markup(nameDisplay),
                new Markup($"[{stateColor}]{agent.State}[/]"),
                new Markup(taskDisplay),
                new Markup(countsDisplay)
            );
        }

        return new Panel(table)
            .Header("[bold]Agents[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    /// <summary>
    /// Build the task queue panel with progress bar and counts
    /// </summary>
    private Panel BuildQueuePanel()
    {
        var stats = _queueStats;

        if (stats.Total == 0)
        {
            return new Panel(new Markup("[dim]No tasks queued[/]"))
                .Header("[bold]Task Queue[/]")
                .Border(BoxBorder.Rounded)
                .Expand();
        }

        var percent = stats.CompletionPercent;
        var barWidth = Math.Max(10, Console.WindowWidth - 20);
        var filledWidth = (int)(barWidth * percent / 100.0);
        var emptyWidth = barWidth - filledWidth;

        var barColor = percent >= 100 ? "green" : percent >= 50 ? "yellow" : "blue";
        var progressBar = $"[{barColor}]{new string('#', filledWidth)}[/][dim]{new string('-', emptyWidth)}[/]";

        var countsLine = $"[dim]Pending:[/] {stats.Pending}  " +
                         $"[cyan]Claimed:[/] {stats.Claimed}  " +
                         $"[yellow]InProgress:[/] {stats.InProgress}  " +
                         $"[green]Completed:[/] {stats.Completed}  " +
                         $"[red]Failed:[/] {stats.Failed}  " +
                         $"[bold]{percent:F0}%[/]";

        var content = $"{progressBar}\n{countsLine}";

        return new Panel(new Markup(content))
            .Header($"[bold]Task Queue ({stats.Completed}/{stats.Total})[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    /// <summary>
    /// Build the teams footer panel with key controls
    /// </summary>
    private Panel BuildTeamsFooterPanel()
    {
        var state = _teamController!.State;

        var controls = state switch
        {
            TeamControllerState.Running or TeamControllerState.Initializing =>
                "[[S]]top  [[Q]]uit",
            TeamControllerState.Stopped or TeamControllerState.Failed =>
                "[[Q]]uit",
            _ => "[[Q]]uit"
        };

        // Show phase-specific hint if running
        if (state == TeamControllerState.Running)
        {
            var phaseHint = _teamController.CurrentPhase switch
            {
                TeamPhase.Decomposing => "[dim]Decomposing tasks...[/]  ",
                TeamPhase.Executing => "[dim]Agents working...[/]  ",
                TeamPhase.Verifying => "[dim]Verifying results...[/]  ",
                TeamPhase.Complete => "[dim]Complete[/]  ",
                _ => ""
            };
            controls = phaseHint + controls;
        }

        return new Panel(new Markup($"[bold]{controls}[/]"))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
    }

    /// <summary>
    /// Handle key presses in teams mode
    /// </summary>
    private async Task HandleTeamsKeyAsync(ConsoleKeyInfo key)
    {
        switch (char.ToLower(key.KeyChar))
        {
            case 's':
                if (_teamController!.State == TeamControllerState.Running ||
                    _teamController.State == TeamControllerState.Initializing)
                {
                    _teamController.Stop();
                    AddOutputLine("[red]>>> Stopping teams...[/]");
                }
                break;

            case 'q':
                if (_teamController!.State == TeamControllerState.Running ||
                    _teamController.State == TeamControllerState.Initializing)
                {
                    _teamController.Stop();
                }
                Stop();
                break;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Truncate a string to a maximum length
    /// </summary>
    private static string TruncateString(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _uiCts?.Cancel();
        _uiCts?.Dispose();

        GC.SuppressFinalize(this);
    }
}
