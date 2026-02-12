# TUI: Terminal UI for Agent Teams

## Status
Draft

## Summary
Upgrade the Spectre.Console TUI to support interactive agent teams: agent selection, direct messaging, task list display, and real-time status updates. Match Claude Code's in-process display mode where users can cycle through agents and interact directly.

## Reference
From Claude Code docs:
> **In-process mode**: use Shift+Up/Down to select a teammate, then type to send them a message directly. Press Enter to view a teammate's session, then Escape to interrupt their current turn. Press Ctrl+T to toggle the task list.
> The lead's terminal lists all teammates and what they're working on.

## Current State

**What exists:**
- `ConsoleUI.cs` with Spectre.Console `Layout` for teams mode:
  - Top: Phase indicator (Decomposing/Executing/Verifying)
  - Middle-left: Agent status panel (state, current task, stats per agent)
  - Middle-right: Task queue summary (total, pending, completed, failed)
  - Bottom-left: Team output log (scrolling)
  - Bottom-right: Hotkeys
- `Markup.Escape()` used for agent names (bug fix)
- Streaming output buffered and flushed to display

**What's broken/missing:**
1. No agent selection — can't pick an agent to view/message
2. No direct messaging to agents from TUI
3. No task list detail view (only summary counts)
4. No per-agent output view (all output mixed into one log)
5. No hotkey handling for Shift+Up/Down, Ctrl+T
6. Phase indicator is static (Decomposing/Executing/Verifying) — doesn't reflect continuous coordination

## Target State

### Display Layout

```
┌─────────────────────────────────────────────────────────┐
│ Ralph Teams — 3 agents — 12 tasks (8 done, 2 active)   │
├───────────────────────┬─────────────────────────────────┤
│ AGENTS                │ SELECTED: agent-1 (Worker)      │
│                       │                                 │
│ ▸ agent-1 [Working]   │ Task: Implement auth module     │
│   agent-2 [Idle]      │ Files: src/auth.cs, src/jwt.cs  │
│   agent-3 [Working]   │                                 │
│   lead    [Coord]     │ Output:                         │
│                       │ > Analyzing existing auth...    │
│                       │ > Creating JWT helper class...  │
│                       │ > Writing unit tests...         │
├───────────────────────┴─────────────────────────────────┤
│ [Shift+↑↓] Select agent  [Enter] View  [Ctrl+T] Tasks  │
│ [Type] Message agent  [Esc] Back  [Ctrl+C] Stop         │
└─────────────────────────────────────────────────────────┘
```

### Views

1. **Agent List View** (default) - Shows all agents with state, selected agent's detail on right
2. **Agent Detail View** (Enter on selected) - Full-screen output of selected agent
3. **Task List View** (Ctrl+T toggle) - All tasks with status, assignee, dependencies

### Agent Selection

- **Shift+Up/Down**: Move selection cursor through agent list
- **Enter**: Switch to full detail view of selected agent
- **Escape**: Back to agent list view
- **Type + Enter**: Send text message to selected agent

### Task List View

```
┌─────────────────────────────────────────────────────────┐
│ TASKS (12 total)                                        │
├────┬──────────────────────┬──────────┬─────────┬────────┤
│ ID │ Title                │ Status   │ Agent   │ Deps   │
├────┼──────────────────────┼──────────┼─────────┼────────┤
│ 1  │ Setup auth module    │ ✓ Done   │ agent-1 │ —      │
│ 2  │ Create JWT helper    │ Working  │ agent-1 │ 1      │
│ 3  │ Add login endpoint   │ Pending  │ —       │ 1,2    │
│ 4  │ Frontend auth forms  │ Working  │ agent-3 │ —      │
│ 5  │ Write auth tests     │ Blocked  │ —       │ 2,3    │
│ ...│                      │          │         │        │
├────┴──────────────────────┴──────────┴─────────┴────────┤
│ [Ctrl+T] Back to agents                                 │
└─────────────────────────────────────────────────────────┘
```

### Real-Time Updates

TUI refreshes via events from:
- `TeamAgent.StateChanged` → Update agent status in list
- `TaskStore.TaskCompleted` / `TaskUnblocked` → Update task counts
- `TeamAgent.OutputReceived` → Update selected agent's output panel
- `MessageBus` → Show message indicators (new message badge)

## Technical Requirements

1. **Agent selection state** - Track `selectedAgentIndex` in ConsoleUI. Shift+Up/Down changes it. Highlight selected agent in list.

2. **Keyboard input handling** - Spectre.Console doesn't have built-in hotkey handling for Shift+Arrow. Use `Console.ReadKey()` in a background thread or Spectre's `Prompt` system. May need raw terminal input.

3. **Per-agent output buffer** - Each agent's output stored separately. When agent selected, show their buffer. Currently all output mixed.

4. **Task list panel** - Render task list as a Spectre `Table` with columns: ID, Title, Status (with color), Agent, Dependencies.

5. **Message input** - When user types text in agent list view, it's sent as a `Text` message to the selected agent. Need input field at bottom.

6. **Status bar** - Top bar shows: team name, agent count, task progress (X/Y done), elapsed time.

7. **Markup safety** - ALL dynamic text (agent names, task titles, output, error messages) must go through `Markup.Escape()`.

8. **View state machine** - `TUIView` enum: `AgentList`, `AgentDetail`, `TaskList`. Keyboard shortcuts transition between views.

## Architecture

### Updated ConsoleUI (teams mode)

```csharp
public partial class ConsoleUI
{
    // View state
    private TUIView _currentView = TUIView.AgentList;
    private int _selectedAgentIndex = 0;
    private Dictionary<string, List<string>> _agentOutputBuffers;

    // Event handlers (called by orchestrator/agents)
    public void OnAgentStateChanged(string agentId, AgentState state);
    public void OnAgentOutput(string agentId, string line);
    public void OnTaskUpdated(AgentTask task);
    public void OnMessageReceived(string agentId, Message message);

    // Input handling
    private async Task HandleInputAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: true);

            switch (_currentView)
            {
                case TUIView.AgentList:
                    if (key is { Modifiers: Shift, Key: UpArrow })
                        _selectedAgentIndex = Math.Max(0, _selectedAgentIndex - 1);
                    else if (key is { Modifiers: Shift, Key: DownArrow })
                        _selectedAgentIndex = Math.Min(_agents.Count - 1, _selectedAgentIndex + 1);
                    else if (key.Key == ConsoleKey.Enter)
                        _currentView = TUIView.AgentDetail;
                    else if (key is { Modifiers: Control, Key: T })
                        _currentView = TUIView.TaskList;
                    break;

                case TUIView.AgentDetail:
                    if (key.Key == ConsoleKey.Escape)
                        _currentView = TUIView.AgentList;
                    break;

                case TUIView.TaskList:
                    if (key is { Modifiers: Control, Key: T })
                        _currentView = TUIView.AgentList;
                    break;
            }

            Refresh();
        }
    }

    // Rendering
    private void RenderAgentListView(Layout layout);
    private void RenderAgentDetailView(Layout layout);
    private void RenderTaskListView(Layout layout);
}
```

### Color Scheme

| Agent State | Color |
|-------------|-------|
| Ready | `[blue]` |
| Working | `[green]` |
| PlanningWork | `[yellow]` |
| Idle | `[dim]` |
| Error | `[red]` |
| Stopped | `[grey]` |

| Task Status | Symbol | Color |
|-------------|--------|-------|
| Pending | `○` | `[dim]` |
| Blocked | `◌` | `[grey]` |
| InProgress | `◉` | `[green]` |
| Completed | `✓` | `[green]` |
| Failed | `✗` | `[red]` |

## Acceptance Criteria

- [ ] Agent list shows all agents with current state and color coding
- [ ] Shift+Up/Down cycles agent selection
- [ ] Selected agent's output shown in detail panel
- [ ] Enter shows full-screen agent detail view; Escape returns
- [ ] Ctrl+T toggles task list view
- [ ] Task list shows all tasks with status, assignee, and dependencies
- [ ] Blocked tasks visually distinct from pending tasks
- [ ] Agent output buffers are per-agent (not mixed)
- [ ] Status bar shows team progress summary
- [ ] All dynamic text escaped with `Markup.Escape()`
- [ ] TUI refreshes on state change events (not polling)
- [ ] User can type a message to the selected agent

## Files to Modify/Create

| Action | File |
|--------|------|
| Major rewrite | `ConsoleUI.cs` (teams mode section) |
| Create | `TUI/TUIView.cs` (view state enum) |
| Create | `TUI/InputHandler.cs` (keyboard input processing) |

## Dependencies
- [agent-lifecycle.md](agent-lifecycle.md) - StateChanged events
- [messaging.md](messaging.md) - MessageBus for user-to-agent messaging
- [task-system.md](task-system.md) - TaskStore events for task list display
