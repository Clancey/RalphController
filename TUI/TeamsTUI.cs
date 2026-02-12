using RalphController.Messaging;
using RalphController.Models;
using RalphController.Parallel;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text;
using TaskStatus = RalphController.Models.TaskStatus;

namespace RalphController.TUI;

/// <summary>
/// Spectre.Console-based TUI for teams mode.
/// Subscribes to <see cref="TeamOrchestrator"/> events and renders three views:
/// AgentList (split), AgentDetail (full-screen), and TaskList (table).
///
/// This is a standalone replacement for the teams rendering in ConsoleUI.cs.
/// It does NOT modify ConsoleUI; the two can coexist until migration is complete.
/// </summary>
public sealed class TeamsTUI : IDisposable
{
    // --- Dependencies ---
    private readonly TeamOrchestrator _orchestrator;
    private readonly RalphConfig _config;
    private readonly InputHandler _input;
    private readonly AgentOutputBuffer _outputBuffers;

    // --- View state ---
    private TUIView _currentView = TUIView.AgentList;
    private int _selectedAgentIndex;
    private readonly object _viewLock = new();

    // --- Cached data (updated by events) ---
    private TaskStoreStatistics _taskStats = new();
    private readonly Dictionary<string, AgentStatistics> _agentStats = new();
    private readonly List<string> _sortedAgentIds = new();

    // --- Message input ---
    private readonly StringBuilder _inputBuffer = new();
    private bool _inputMode;

    // --- Timing ---
    private readonly DateTime _startTime = DateTime.UtcNow;

    // --- Render throttle ---
    private DateTime _lastRender = DateTime.MinValue;
    private static readonly TimeSpan MinRenderInterval = TimeSpan.FromMilliseconds(250);
    private volatile bool _renderRequested;
    private bool _disposed;

    /// <summary>
    /// Create the TUI and subscribe to orchestrator events.
    /// </summary>
    public TeamsTUI(TeamOrchestrator orchestrator, RalphConfig config)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _input = new InputHandler();
        _outputBuffers = new AgentOutputBuffer(maxLinesPerAgent: 500);

        SubscribeToEvents();
    }

    /// <summary>
    /// Run the TUI render loop. Blocks until the cancellation token fires.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _input.OnKeyPressed += HandleKey;
        _input.Start(ct);

        Console.CursorVisible = false;

        try
        {
            // Initial render
            Render();

            while (!ct.IsCancellationRequested)
            {
                if (_renderRequested)
                {
                    _renderRequested = false;
                    Render();
                }

                await Task.Delay(100, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            Console.CursorVisible = true;
            Console.Clear();
        }
    }

    // -------------------------------------------------------
    // Event subscriptions
    // -------------------------------------------------------

    private void SubscribeToEvents()
    {
        _orchestrator.OnStateChanged += _ => RequestRender();

        _orchestrator.OnOutput += output =>
        {
            // Route per-agent output to the correct buffer
            var agentId = ExtractAgentId(output);
            if (agentId != null)
            {
                var message = StripAgentPrefix(output, agentId);
                _outputBuffers.Append(agentId, message);
            }
            else
            {
                // Lead / orchestrator output goes to a "lead" buffer
                _outputBuffers.Append("lead", output);
            }
            RequestRender();
        };

        _orchestrator.OnError += error =>
        {
            var agentId = ExtractAgentId(error);
            if (agentId != null)
            {
                var message = StripAgentPrefix(error, agentId);
                _outputBuffers.Append(agentId, $"[ERROR] {message}");
            }
            else
            {
                _outputBuffers.Append("lead", $"[ERROR] {error}");
            }
            RequestRender();
        };

        _orchestrator.OnAgentUpdate += stats =>
        {
            lock (_viewLock)
            {
                _agentStats[stats.AgentId] = stats;
                if (!_sortedAgentIds.Contains(stats.AgentId))
                {
                    // Ensure "lead" is always first
                    if (stats.AgentId == "lead")
                    {
                        _sortedAgentIds.Insert(0, stats.AgentId);
                    }
                    else
                    {
                        _sortedAgentIds.Add(stats.AgentId);
                        // Sort non-lead entries only
                        if (_sortedAgentIds.Count > 1 && _sortedAgentIds[0] == "lead")
                        {
                            var rest = _sortedAgentIds.Skip(1).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
                            _sortedAgentIds.RemoveRange(1, _sortedAgentIds.Count - 1);
                            _sortedAgentIds.AddRange(rest);
                        }
                        else
                        {
                            _sortedAgentIds.Sort(StringComparer.OrdinalIgnoreCase);
                        }
                    }
                }
            }
            RequestRender();
        };

        _orchestrator.OnQueueUpdate += stats =>
        {
            lock (_viewLock)
                _taskStats = stats;
            RequestRender();
        };

        // Lead-driven mode: ephemeral TaskAgent lifecycle
        _orchestrator.OnTaskAgentCreated += taskAgent =>
        {
            lock (_viewLock)
            {
                if (!_sortedAgentIds.Contains(taskAgent.AgentId))
                {
                    _sortedAgentIds.Add(taskAgent.AgentId);
                }
            }
            _outputBuffers.Append(taskAgent.AgentId, $"TaskAgent created for: {taskAgent.Task.Title ?? taskAgent.Task.Description}");
            RequestRender();
        };

        _orchestrator.OnTaskAgentDestroyed += taskAgent =>
        {
            // Keep in list briefly with stopped state, then schedule removal
            _outputBuffers.Append(taskAgent.AgentId, "TaskAgent completed and destroyed");
            _ = RemoveTaskAgentAfterDelay(taskAgent.AgentId, TimeSpan.FromSeconds(5));
            RequestRender();
        };

        _outputBuffers.OnOutputReceived += _ => RequestRender();
    }

    private async Task RemoveTaskAgentAfterDelay(string agentId, TimeSpan delay)
    {
        await System.Threading.Tasks.Task.Delay(delay);
        lock (_viewLock)
        {
            _sortedAgentIds.Remove(agentId);
            _agentStats.Remove(agentId);
        }
        RequestRender();
    }

    // -------------------------------------------------------
    // Input handling
    // -------------------------------------------------------

    private void HandleKey(ConsoleKeyInfo key)
    {
        // Text input mode: typing a message to send to the selected agent
        if (_inputMode)
        {
            HandleInputModeKey(key);
            return;
        }

        // Ctrl+T: toggle task list view
        if (key.Key == ConsoleKey.T && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            lock (_viewLock)
            {
                _currentView = _currentView == TUIView.TaskList
                    ? TUIView.AgentList
                    : TUIView.TaskList;
            }
            RequestRender();
            return;
        }

        switch (_currentView)
        {
            case TUIView.AgentList:
                HandleAgentListKey(key);
                break;

            case TUIView.AgentDetail:
                HandleAgentDetailKey(key);
                break;

            case TUIView.TaskList:
                HandleTaskListKey(key);
                break;
        }
    }

    private void HandleAgentListKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            // Up/k: select previous agent
            case ConsoleKey.UpArrow:
            case ConsoleKey.K:
                lock (_viewLock)
                {
                    if (_sortedAgentIds.Count > 0)
                    {
                        _selectedAgentIndex = (_selectedAgentIndex - 1 + _sortedAgentIds.Count)
                            % _sortedAgentIds.Count;
                    }
                }
                RequestRender();
                break;

            // Down/j: select next agent
            case ConsoleKey.DownArrow:
            case ConsoleKey.J:
                lock (_viewLock)
                {
                    if (_sortedAgentIds.Count > 0)
                    {
                        _selectedAgentIndex = (_selectedAgentIndex + 1) % _sortedAgentIds.Count;
                    }
                }
                RequestRender();
                break;

            // Enter: drill into agent detail
            case ConsoleKey.Enter:
                lock (_viewLock)
                    _currentView = TUIView.AgentDetail;
                RequestRender();
                break;

            // '/' or 'm': start message input
            case ConsoleKey.Oem2: // '/' key
            case ConsoleKey.M:
                StartInputMode();
                break;
        }
    }

    private void HandleAgentDetailKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            // Escape: back to agent list
            case ConsoleKey.Escape:
                lock (_viewLock)
                    _currentView = TUIView.AgentList;
                RequestRender();
                break;

            // '/' or 'm': start message input
            case ConsoleKey.Oem2:
            case ConsoleKey.M:
                StartInputMode();
                break;
        }
    }

    private void HandleTaskListKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                lock (_viewLock)
                    _currentView = TUIView.AgentList;
                RequestRender();
                break;
        }
    }

    private void StartInputMode()
    {
        _inputMode = true;
        _inputBuffer.Clear();
        RequestRender();
    }

    private void HandleInputModeKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _inputMode = false;
                _inputBuffer.Clear();
                RequestRender();
                break;

            case ConsoleKey.Enter:
                var message = _inputBuffer.ToString().Trim();
                if (!string.IsNullOrEmpty(message))
                {
                    SendMessageToSelectedAgent(message);
                }
                _inputMode = false;
                _inputBuffer.Clear();
                RequestRender();
                break;

            case ConsoleKey.Backspace:
                if (_inputBuffer.Length > 0)
                    _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                RequestRender();
                break;

            default:
                if (key.KeyChar >= 32 && key.KeyChar < 127)
                {
                    _inputBuffer.Append(key.KeyChar);
                    RequestRender();
                }
                break;
        }
    }

    private void SendMessageToSelectedAgent(string message)
    {
        string? targetAgentId;
        lock (_viewLock)
        {
            targetAgentId = _selectedAgentIndex < _sortedAgentIds.Count
                ? _sortedAgentIds[_selectedAgentIndex]
                : null;
        }

        if (targetAgentId == null) return;

        // Use the orchestrator's agents to find the message bus
        if (_orchestrator.Agents.TryGetValue(targetAgentId, out var agent) && agent.MessageBus != null)
        {
            agent.MessageBus.Send(Message.TextMessage("user", targetAgentId, message));
            _outputBuffers.Append(targetAgentId, $"[You] {message}");
        }
    }

    // -------------------------------------------------------
    // Render
    // -------------------------------------------------------

    private void RequestRender()
    {
        _renderRequested = true;
    }

    private void Render()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastRender) < MinRenderInterval)
        {
            // We already have _renderRequested = true, so the loop will pick it up
            return;
        }
        _lastRender = now;

        try
        {
            Console.SetCursorPosition(0, 0);

            TUIView view;
            lock (_viewLock)
                view = _currentView;

            IRenderable content = view switch
            {
                TUIView.AgentList => RenderAgentListView(),
                TUIView.AgentDetail => RenderAgentDetailView(),
                TUIView.TaskList => RenderTaskListView(),
                _ => new Markup("[dim]Unknown view[/]")
            };

            var statusBar = RenderStatusBar();
            var inputBar = RenderInputBar();

            // Build a layout: status bar on top, content in middle, input on bottom
            var layout = new Layout("root")
                .SplitRows(
                    new Layout("status").Size(1),
                    new Layout("content"),
                    new Layout("input").Size(_inputMode ? 1 : 1));

            layout["status"].Update(statusBar);
            layout["content"].Update(content);
            layout["input"].Update(inputBar);

            AnsiConsole.Write(layout);
        }
        catch
        {
            // Swallow render errors (terminal resize, etc.)
        }
    }

    // -------------------------------------------------------
    // View: Agent List (split layout)
    // -------------------------------------------------------

    private IRenderable RenderAgentListView()
    {
        List<string> agentIds;
        int selectedIdx;
        lock (_viewLock)
        {
            agentIds = new List<string>(_sortedAgentIds);
            selectedIdx = _selectedAgentIndex;
        }

        // Left panel: agent list
        var agentTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Agents[/]")
            .AddColumn(new TableColumn("").Width(2))
            .AddColumn("Agent")
            .AddColumn("State")
            .AddColumn("Task")
            .AddColumn("Done");

        for (int i = 0; i < agentIds.Count; i++)
        {
            var id = agentIds[i];
            var isSelected = i == selectedIdx;
            var prefix = isSelected ? "[bold cyan]>[/]" : " ";

            AgentStatistics? stats;
            lock (_viewLock)
                _agentStats.TryGetValue(id, out stats);

            var state = stats?.State ?? AgentState.Idle;
            var stateMarkup = FormatAgentState(state, stats?.CurrentSubPhase);
            var taskTitle = stats?.CurrentTask?.Title;
            var escapedTask = taskTitle != null
                ? Markup.Escape(Truncate(taskTitle, 30))
                : "[dim]none[/]";
            var completed = stats?.TasksCompleted.ToString() ?? "0";
            var escapedName = Markup.Escape(stats?.Name ?? id);

            if (isSelected)
            {
                agentTable.AddRow(
                    new Markup(prefix),
                    new Markup($"[bold]{escapedName}[/]"),
                    new Markup(stateMarkup),
                    new Markup(escapedTask),
                    new Markup(completed));
            }
            else
            {
                agentTable.AddRow(
                    new Markup(prefix),
                    new Markup(escapedName),
                    new Markup(stateMarkup),
                    new Markup(escapedTask),
                    new Markup(completed));
            }
        }

        if (agentIds.Count == 0)
        {
            agentTable.AddRow("", "[dim]No agents spawned yet[/]", "", "", "");
        }

        // Right panel: selected agent output
        string? selectedAgentId;
        lock (_viewLock)
        {
            selectedAgentId = selectedIdx < agentIds.Count ? agentIds[selectedIdx] : null;
        }

        var outputPanel = RenderAgentOutputPanel(selectedAgentId, maxLines: 30);

        // Combine into a split layout
        var split = new Layout("split")
            .SplitColumns(
                new Layout("agents").Size(55).Update(agentTable),
                new Layout("output").Update(outputPanel));

        return split;
    }

    // -------------------------------------------------------
    // View: Agent Detail (full-screen output)
    // -------------------------------------------------------

    private IRenderable RenderAgentDetailView()
    {
        string? agentId;
        lock (_viewLock)
        {
            agentId = _selectedAgentIndex < _sortedAgentIds.Count
                ? _sortedAgentIds[_selectedAgentIndex]
                : null;
        }

        if (agentId == null)
        {
            return new Panel(new Markup("[dim]No agent selected[/]"))
                .Header("[bold]Agent Detail[/]")
                .Expand();
        }

        AgentStatistics? stats;
        lock (_viewLock)
            _agentStats.TryGetValue(agentId, out stats);

        // Header line with agent info
        var state = stats?.State ?? AgentState.Idle;
        var stateStr = FormatAgentState(state, stats?.CurrentSubPhase);
        var taskTitle = stats?.CurrentTask?.Title;
        var escapedTask = taskTitle != null
            ? Markup.Escape(taskTitle)
            : "none";
        var model = stats?.AssignedModel?.Model ?? "default";
        var escapedModel = Markup.Escape(model);
        var headerText = $"[bold]{Markup.Escape(agentId)}[/]  {stateStr}  Task: {escapedTask}  Model: {escapedModel}";

        var lines = _outputBuffers.GetLines(agentId, maxLines: 50);
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.AppendLine(Markup.Escape(line));
        }

        if (sb.Length == 0)
            sb.AppendLine("[dim]No output yet[/]");

        var panel = new Panel(new Markup(sb.ToString().TrimEnd()))
            .Header(headerText)
            .Border(BoxBorder.Rounded)
            .Expand();

        return new Rows(
            new Markup("[dim]Esc: back  /: message agent[/]"),
            panel);
    }

    // -------------------------------------------------------
    // View: Task List
    // -------------------------------------------------------

    private IRenderable RenderTaskListView()
    {
        var tasks = _orchestrator.TaskStore.GetAll();

        // Build a set of claimable task IDs so we can distinguish
        // Pending (claimable) from Blocked (pending with unsatisfied deps).
        var claimableIds = new HashSet<string>(
            _orchestrator.TaskStore.GetClaimable().Select(t => t.TaskId));

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Tasks[/]")
            .AddColumn("ID")
            .AddColumn("Title")
            .AddColumn("Status")
            .AddColumn("Agent")
            .AddColumn("Deps");

        foreach (var task in tasks)
        {
            var isBlocked = task.Status == TaskStatus.Pending
                && task.DependsOn.Count > 0
                && !claimableIds.Contains(task.TaskId);
            var statusMarkup = isBlocked
                ? "[grey]\u25cc Blocked[/]"       // ◌
                : FormatTaskStatus(task.Status);
            var escapedId = Markup.Escape(Truncate(task.TaskId, 12));
            var escapedTitle = Markup.Escape(Truncate(task.Title ?? task.Description, 45));
            var escapedAgent = task.ClaimedByAgentId != null
                ? Markup.Escape(task.ClaimedByAgentId)
                : "[dim]-[/]";
            var deps = task.DependsOn.Count > 0
                ? Markup.Escape(string.Join(", ", task.DependsOn.Select(d => Truncate(d, 10))))
                : "[dim]-[/]";

            table.AddRow(
                new Markup(escapedId),
                new Markup(escapedTitle),
                new Markup(statusMarkup),
                new Markup(escapedAgent),
                new Markup(deps));
        }

        if (tasks.Count == 0)
        {
            table.AddRow("", "[dim]No tasks[/]", "", "", "");
        }

        TaskStoreStatistics stats;
        lock (_viewLock)
            stats = _taskStats;

        var summary = new Markup(
            $"[dim]Total:[/] {stats.Total}  " +
            $"[green]Done:[/] {stats.Completed}  " +
            $"[yellow]In Progress:[/] {stats.InProgress}  " +
            $"[dim]Pending:[/] {stats.Pending}  " +
            $"[red]Failed:[/] {stats.Failed}  " +
            $"[dim]({stats.CompletionPercent:F0}% complete)[/]");

        return new Rows(
            new Markup("[dim]Esc: back  Ctrl+T: toggle[/]"),
            summary,
            table);
    }

    // -------------------------------------------------------
    // Status bar
    // -------------------------------------------------------

    private IRenderable RenderStatusBar()
    {
        var teamName = _config.Teams?.TeamName ?? "default";
        var elapsed = DateTime.UtcNow - _startTime;
        var elapsedStr = $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

        int agentCount;
        TaskStoreStatistics stats;
        lock (_viewLock)
        {
            agentCount = _sortedAgentIds.Count;
            stats = _taskStats;
        }

        var stateStr = FormatOrchestratorState(_orchestrator.State);
        var viewHint = _currentView switch
        {
            TUIView.AgentList => "Up/Down: select  Enter: detail  Ctrl+T: tasks  /: msg",
            TUIView.AgentDetail => "Esc: back  /: msg",
            TUIView.TaskList => "Esc: back  Ctrl+T: toggle",
            _ => ""
        };

        var barText =
            $"[bold]{Markup.Escape(teamName)}[/]  " +
            $"{stateStr}  " +
            $"Agents: [cyan]{agentCount}[/]  " +
            $"Tasks: [green]{stats.Completed}[/]/{stats.Total}  " +
            $"Elapsed: [dim]{elapsedStr}[/]  " +
            $"[dim]{viewHint}[/]";

        return new Markup(barText);
    }

    // -------------------------------------------------------
    // Input bar
    // -------------------------------------------------------

    private IRenderable RenderInputBar()
    {
        if (!_inputMode)
        {
            return new Markup("[dim]Press / or m to type a message to the selected agent[/]");
        }

        var text = _inputBuffer.ToString();
        var escapedText = Markup.Escape(text);
        return new Markup($"[yellow]Message:[/] {escapedText}[blink]|[/]");
    }

    // -------------------------------------------------------
    // Shared rendering helpers
    // -------------------------------------------------------

    private IRenderable RenderAgentOutputPanel(string? agentId, int maxLines)
    {
        if (agentId == null)
        {
            return new Panel(new Markup("[dim]Select an agent to view output[/]"))
                .Header("[bold]Output[/]")
                .Border(BoxBorder.Rounded)
                .Expand();
        }

        var lines = _outputBuffers.GetLines(agentId, maxLines);
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.AppendLine(Markup.Escape(line));
        }

        if (sb.Length == 0)
            sb.AppendLine("[dim]No output yet[/]");

        var escapedHeader = Markup.Escape(agentId);
        return new Panel(new Markup(sb.ToString().TrimEnd()))
            .Header($"[bold]{escapedHeader}[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    // -------------------------------------------------------
    // Formatting helpers
    // -------------------------------------------------------

    /// <summary>
    /// Format agent state with the correct color per the color scheme.
    /// </summary>
    private static string FormatAgentState(AgentState state, SubAgentPhase? subPhase = null)
    {
        // In lead-driven mode, show the sub-phase if available
        if (subPhase is SubAgentPhase.Plan)
            return "[yellow]Planning[/]";
        if (subPhase is SubAgentPhase.Code)
            return "[green]Coding[/]";
        if (subPhase is SubAgentPhase.Verify)
            return "[cyan]Verifying[/]";

        return state switch
        {
            AgentState.Ready => "[blue]Ready[/]",
            AgentState.Working => "[green]Working[/]",
            AgentState.PlanningWork => "[yellow]Planning[/]",
            AgentState.Deciding => "[magenta]Deciding[/]",
            AgentState.Coding => "[green]Coding[/]",
            AgentState.Verifying => "[cyan]Verifying[/]",
            AgentState.Reviewing => "[yellow]Reviewing[/]",
            AgentState.MergingWork => "[yellow]Merging[/]",
            AgentState.Idle => "[dim]Idle[/]",
            AgentState.Error => "[red]Error[/]",
            AgentState.Stopped => "[grey]Stopped[/]",
            AgentState.Spawning => "[blue]Spawning[/]",
            AgentState.Claiming => "[blue]Claiming[/]",
            AgentState.ShuttingDown => "[grey]Shutting Down[/]",
            _ => $"[dim]{Markup.Escape(state.ToString())}[/]"
        };
    }

    /// <summary>
    /// Format task status with symbol and color per the color scheme.
    /// </summary>
    private static string FormatTaskStatus(TaskStatus status) => status switch
    {
        TaskStatus.Pending => "[dim]\u25cb Pending[/]",       // ○
        TaskStatus.InProgress => "[green]\u25c9 InProgress[/]", // ◉
        TaskStatus.Completed => "[green]\u2713 Completed[/]",   // ✓
        TaskStatus.Failed => "[red]\u2717 Failed[/]",           // ✗
        _ => $"[dim]{Markup.Escape(status.ToString())}[/]"
    };

    /// <summary>
    /// Format orchestrator state for status bar.
    /// </summary>
    private static string FormatOrchestratorState(TeamOrchestratorState state) => state switch
    {
        TeamOrchestratorState.Idle => "[dim]Idle[/]",
        TeamOrchestratorState.Initializing => "[blue]Initializing[/]",
        TeamOrchestratorState.Decomposing => "[yellow]Decomposing[/]",
        TeamOrchestratorState.Spawning => "[blue]Spawning[/]",
        TeamOrchestratorState.Coordinating => "[green]Coordinating[/]",
        TeamOrchestratorState.Synthesizing => "[yellow]Synthesizing[/]",
        TeamOrchestratorState.Merging => "[yellow]Merging[/]",
        TeamOrchestratorState.Complete => "[green]Complete[/]",
        TeamOrchestratorState.Stopped => "[grey]Stopped[/]",
        TeamOrchestratorState.Failed => "[red]Failed[/]",
        _ => $"[dim]{Markup.Escape(state.ToString())}[/]"
    };

    /// <summary>
    /// Extract an agent ID from output like "[agent-1] some text".
    /// Returns null if no agent prefix found.
    /// </summary>
    private static string? ExtractAgentId(string output)
    {
        if (output.Length < 3 || output[0] != '[')
            return null;

        var closeBracket = output.IndexOf(']', 1);
        if (closeBracket < 2)
            return null;

        var id = output[1..closeBracket];

        // Match agent-N patterns (parallel mode) and task-agent-* patterns (lead-driven mode)
        if (id.StartsWith("agent-", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("task-agent-", StringComparison.OrdinalIgnoreCase))
            return id;

        return null;
    }

    /// <summary>
    /// Strip the "[agent-1] " prefix from output.
    /// </summary>
    private static string StripAgentPrefix(string output, string agentId)
    {
        var prefix = $"[{agentId}] ";
        return output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? output[prefix.Length..]
            : output;
    }

    /// <summary>
    /// Truncate a string to a maximum length, appending "..." if truncated.
    /// </summary>
    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length <= maxLength
            ? value
            : value[..(maxLength - 3)] + "...";
    }

    // -------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _input.Dispose();
    }
}
