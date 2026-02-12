# Agent Lifecycle: Spawning, Execution, and Shutdown

## Status
Draft

## Summary
Define proper agent lifecycle states so the lead can manage teammates — spawning them with context, monitoring their execution, handling idle states, and requesting graceful shutdown.

## Reference
From Claude Code docs:
> One session acts as the team lead, coordinating work, assigning tasks, and synthesizing results. Teammates work independently, each in its own context window.
> The lead sends a shutdown request. The teammate can approve, exiting gracefully, or reject with an explanation.
> When a teammate finishes and stops, they automatically notify the lead.

## Current State

**What exists:**
- `TeamAgent.cs` runs `RunLoopAsync()`: loops claiming/executing tasks until queue empty
- Agent states: `Idle`, `Working`, `WaitingForTask`, `Merging`, `Done`, `Error`
- `AgentStatistics` tracks runtime metrics
- Agents killed via `CancellationToken` (no graceful shutdown)

**What's broken:**
1. No distinction between "idle waiting for work" and "done forever"
2. No graceful shutdown — cancellation token aborts mid-task
3. Agent can't reject shutdown (e.g., "I'm mid-merge, give me 30 seconds")
4. No spawn prompt — agents all get the same generic prompt
5. No plan-before-implement mode
6. No idle notification to lead

## Target State

### Agent States

```
Spawning → Ready → [Claiming → Working → Idle] (loop) → ShuttingDown → Stopped
                                                    ↘ Error
```

| State | Description |
|-------|-------------|
| **Spawning** | Agent process starting, loading context |
| **Ready** | Loaded and waiting for first task |
| **Claiming** | Attempting to claim a task from TaskStore |
| **Working** | Executing a claimed task |
| **PlanningWork** | In plan mode, waiting for lead approval |
| **Idle** | No claimable tasks available; waiting for tasks to unblock |
| **ShuttingDown** | Received shutdown request, finishing current work |
| **Stopped** | Cleanly exited |
| **Error** | Unrecoverable error; requires lead intervention |

### Spawn Flow

Lead spawns agent with:
```csharp
var agent = await orchestrator.SpawnAgent(new AgentSpawnConfig
{
    Name = "security-reviewer",
    Model = ModelSpec.Parse("claude:sonnet"),
    SpawnPrompt = "Review the auth module for security issues...",
    RequirePlanApproval = true,
    WorkingDirectory = worktreePath,
});
```

The spawn prompt provides task-specific context. Without it, the agent only has project context (CLAUDE.md, prompt.md).

### Idle Behavior

When no claimable tasks exist but uncompleted tasks remain:
1. Agent enters `Idle` state
2. Notifies lead: "Agent {name} is idle — no claimable tasks"
3. Waits with exponential backoff (1s, 2s, 4s, ... up to 30s) polling for claimable tasks
4. On new task becoming claimable (dependency resolved), agent wakes and claims

When all tasks are completed or failed:
1. Agent enters `Idle` state
2. Notifies lead: "Agent {name} finished — all tasks resolved"
3. Lead decides whether to assign more work or shut down

### Graceful Shutdown

```
Lead → sends ShutdownRequest message → Agent
Agent → if idle: accepts, enters ShuttingDown → Stopped
Agent → if working: "Finishing current task, will shut down after"
         → completes task → ShuttingDown → Stopped
Agent → if critical: rejects with reason
         → Lead decides: wait or force-kill
```

Force-kill is only used if agent doesn't respond within shutdown timeout (default 60s).

### Plan-Before-Implement Mode

When `RequirePlanApproval = true`:
1. Agent claims task
2. Enters `PlanningWork` state
3. Runs AI with read-only tools (no file writes)
4. Produces plan and sends `PlanApproval` message to lead
5. Lead reviews:
   - **Approve** → Agent exits plan mode, enters `Working`
   - **Reject with feedback** → Agent revises plan, resubmits
6. Max 3 revision cycles before lead can override or reassign

## Technical Requirements

1. **Add `AgentState` enum** with all states above. Replace current ad-hoc state tracking.

2. **Implement `SpawnAgent()` on orchestrator** - Creates agent with spawn prompt, model config, and worktree. Agent enters `Spawning` → `Ready`.

3. **Implement idle polling with backoff** - Agent polls `TaskStore.GetClaimable()` with exponential backoff when idle. Wakes immediately on `TaskStore.TaskUnblocked` event.

4. **Implement graceful shutdown protocol** - Via messaging system. Agent checks for shutdown request between tasks and during idle.

5. **Implement plan-before-implement** - When `RequirePlanApproval` is true, agent builds prompt with "analyze and plan only, do not modify files" instruction. Sends plan text to lead via message. Waits for approval message.

6. **Emit lifecycle events** - `AgentSpawned`, `AgentIdle`, `AgentWorking`, `AgentStopped`, `AgentError`. Consumed by orchestrator and TUI.

7. **Add spawn prompt to TeamAgent constructor** - Currently agents only receive the main project prompt. Add optional spawn-specific prompt that's prepended.

## Architecture

### Updated TeamAgent

```csharp
public class TeamAgent : IDisposable
{
    // Identity
    public string AgentId { get; }
    public string Name { get; }
    public AgentState State { get; private set; }

    // Configuration
    public ModelSpec Model { get; }
    public string? SpawnPrompt { get; }
    public bool RequirePlanApproval { get; }
    public string WorktreePath { get; }

    // Events
    public event Action<AgentState>? StateChanged;
    public event Action<string>? OutputReceived;

    // Lifecycle
    public async Task RunAsync(CancellationToken ct);
    public void RequestShutdown();
    public void ForceStop();

    // Messaging (delegated to MessageBus)
    public async Task SendMessage(string recipientId, string content);
    public IAsyncEnumerable<Message> GetMessages();

    // Internal loop
    private async Task RunLoopAsync(CancellationToken ct)
    {
        State = AgentState.Ready;

        while (!ct.IsCancellationRequested && !_shutdownRequested)
        {
            // Check for messages (shutdown requests, plan feedback)
            await ProcessPendingMessages();

            // Try to claim work
            State = AgentState.Claiming;
            var task = _taskStore.TryClaim(AgentId);

            if (task == null)
            {
                if (_taskStore.HasPendingWork())
                {
                    State = AgentState.Idle;
                    await WaitForClaimableTask(ct);  // backoff + event wait
                    continue;
                }
                else
                {
                    // All done
                    State = AgentState.Idle;
                    await NotifyLead("All tasks resolved");
                    await WaitForShutdownOrNewWork(ct);
                    continue;
                }
            }

            // Plan if required
            if (RequirePlanApproval)
            {
                State = AgentState.PlanningWork;
                var plan = await ProducePlan(task);
                await SendPlanForApproval(plan);
                var approval = await WaitForApproval(ct);
                if (!approval.Approved) { /* replan or skip */ continue; }
            }

            // Execute
            State = AgentState.Working;
            await ExecuteTask(task, ct);
        }

        State = AgentState.Stopped;
    }
}
```

### AgentSpawnConfig

```csharp
public record AgentSpawnConfig
{
    public required string Name { get; init; }
    public ModelSpec? Model { get; init; }        // null = use lead's model
    public string? SpawnPrompt { get; init; }     // Task-specific context
    public bool RequirePlanApproval { get; init; }
    public Dictionary<string, string>? Environment { get; init; }
}
```

## Acceptance Criteria

- [ ] Agent transitions through states: Spawning → Ready → Claiming → Working → Idle → Stopped
- [ ] StateChanged event fires on every transition (TUI consumes this)
- [ ] Agent enters Idle when no claimable tasks; resumes when tasks unblock
- [ ] Graceful shutdown: agent finishes current task before stopping
- [ ] Force shutdown: agent stops within 5 seconds regardless
- [ ] Spawn prompt is included in agent's AI prompt alongside project context
- [ ] Plan-before-implement: agent produces plan, sends to lead, waits for approval
- [ ] Agent notifies lead when entering Idle state
- [ ] Agent handles crash recovery (partial task → re-queue on restart)

## Files to Modify/Create

| Action | File |
|--------|------|
| Rewrite | `TeamAgent.cs` |
| Modify | `Models/AgentState.cs` (new enum values) |
| Create | `Models/AgentSpawnConfig.cs` |
| Modify | `ConsoleUI.cs` (consume StateChanged events) |
| Modify | `TeamController.cs` / `TeamOrchestrator.cs` (spawn logic) |

## Dependencies
- [task-system.md](task-system.md) - TaskStore with dependency-aware claiming
- [messaging.md](messaging.md) - For shutdown requests and plan approval
