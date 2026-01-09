using System.Collections.Concurrent;
using System.Text;
using RalphController.Models;
using Spectre.Console;

namespace RalphController;

/// <summary>
/// Rich console UI using Spectre.Console
/// </summary>
public class ConsoleUI : IDisposable
{
    private readonly LoopController _controller;
    private readonly FileWatcher _fileWatcher;
    private readonly RalphConfig _config;
    private readonly ConcurrentQueue<string> _outputLines = new();
    private readonly StringBuilder _streamBuffer = new();
    private readonly object _bufferLock = new();
    private readonly int _maxOutputLines = 30;
    private CancellationTokenSource? _uiCts;
    private Task? _inputTask;
    private bool _disposed;
    private int _lastConsoleWidth;
    private int _lastConsoleHeight;

    /// <summary>
    /// Whether to automatically start the loop when RunAsync is called
    /// </summary>
    public bool AutoStart { get; set; }

    public ConsoleUI(LoopController controller, FileWatcher fileWatcher, RalphConfig config)
    {
        _controller = controller;
        _fileWatcher = fileWatcher;
        _config = config;

        // Subscribe to controller events - ANSI codes will be converted to Spectre markup
        // Buffer streaming output and emit complete lines
        _controller.OnOutput += text => AddStreamingOutput(text);
        _controller.OnError += line => AddOutputLine(line, isRawOutput: true, isError: true);
        _controller.OnIterationStart += iter => AddOutputLine($"[blue]>>> Starting iteration {iter}[/]");
        _controller.OnIterationComplete += (iter, result) =>
        {
            FlushStreamBuffer(); // Flush any remaining buffered output
            var status = result.Success ? "[green]SUCCESS[/]" : "[red]FAILED[/]";
            AddOutputLine($"[blue]<<< Iteration {iter} complete: {status}[/]");
        };
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

        // Start input handler on background thread
        _inputTask = Task.Run(() => HandleInputAsync(_uiCts.Token), _uiCts.Token);

        // Auto-start the loop if enabled
        if (AutoStart && _controller.State == LoopState.Idle)
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

                        // Clear and redraw on resize
                        AnsiConsole.Clear();
                    }

                    ctx.UpdateTarget(BuildLayout());
                    ctx.Refresh();
                    await Task.Delay(100, _uiCts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            });
    }

    /// <summary>
    /// Stop the UI
    /// </summary>
    public void Stop()
    {
        _uiCts?.Cancel();
    }

    private Layout BuildLayout()
    {
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

    private Panel BuildHeaderPanel()
    {
        var state = _controller.State;
        var stats = _controller.Statistics;
        var provider = _config.Provider;

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
            $"[white]Iteration #{stats.CurrentIteration}[/]",
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
        var controls = _controller.State switch
        {
            LoopState.Running => "[[P]]ause  [[S]]top  [[F]]orce Stop  [[I]]nject  [[Q]]uit",
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
        switch (char.ToLower(key.KeyChar))
        {
            case 'p':
                if (_controller.State == LoopState.Running)
                {
                    _controller.Pause();
                    AddOutputLine("[yellow]>>> Loop paused[/]");
                }
                break;

            case 'r':
                if (_controller.State == LoopState.Paused)
                {
                    _controller.Resume();
                    AddOutputLine("[green]>>> Loop resumed[/]");
                }
                break;

            case 's':
                if (_controller.State == LoopState.Running || _controller.State == LoopState.Paused)
                {
                    _controller.Stop();
                    AddOutputLine("[red]>>> Stopping after current iteration...[/]");
                }
                break;

            case 'f':
                if (_controller.State != LoopState.Idle)
                {
                    await _controller.ForceStopAsync();
                    AddOutputLine("[red]>>> Force stopped![/]");
                }
                break;

            case 'i':
                await HandleInjectAsync();
                break;

            case 'q':
                Stop();
                break;

            case '\r':
            case '\n':
                if (_controller.State == LoopState.Idle)
                {
                    // Start loop in background
                    _ = _controller.StartAsync();
                    AddOutputLine("[green]>>> Starting loop...[/]");
                }
                break;
        }
    }

    private async Task HandleInjectAsync()
    {
        // For now, use a simple text prompt
        // In a more advanced version, this could open an editor
        AnsiConsole.Clear();
        var prompt = AnsiConsole.Prompt(
            new TextPrompt<string>("[yellow]Enter prompt to inject (or empty to cancel):[/]")
                .AllowEmpty());

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            _controller.InjectPrompt(prompt);
            AddOutputLine($"[yellow]>>> Injected prompt: {Markup.Escape(prompt.Length > 50 ? prompt[..50] + "..." : prompt)}[/]");
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
            // Convert ANSI escape codes to Spectre markup, escape all other brackets
            line = ConvertAnsiToMarkup(line);

            // If it's an error and doesn't have any color markup, wrap in red
            if (isError && !line.Contains("[red]") && !line.Contains("[yellow]"))
            {
                line = $"[red]{line}[/]";
            }
        }

        // Remove control characters that can mess up the layout (but preserve markup brackets)
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _uiCts?.Cancel();
        _uiCts?.Dispose();

        GC.SuppressFinalize(this);
    }
}
