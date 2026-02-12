# Orchestration: Lead Agent Coordination

## Status
Draft

## Summary
The lead agent (TeamOrchestrator) is the central coordinator. It decomposes work, spawns agents, monitors progress, handles plan approvals, reassigns stuck work, and synthesizes results. Optionally operates in delegate mode (coordination only, no code).

## Reference
From Claude Code docs:
> One session acts as the team lead, coordinating work, assigning tasks, and synthesizing results.
> **Delegate mode** restricts the lead to coordination-only tools: spawning, messaging, shutting down teammates, and managing tasks.
> The lead makes approval decisions autonomously. To influence the lead's judgment, give it criteria in your prompt.
> Having 5-6 tasks per teammate keeps everyone productive and lets the lead reassign work if someone gets stuck.

## Current State

**What exists:**
- `TeamController.cs` with 3 sequential phases: `DecomposeAsync()` → `ExecuteAsync()` → `VerifyAndMergeAsync()`
- AI-based decomposition that produces structured tasks
- Parallel agent execution with worktree isolation
- Sequential merge with conflict resolution

**What's broken:**
1. Phases are strictly sequential — lead can't monitor/intervene during execution
2. No plan approval flow
3. No dynamic task reassignment (stuck agent = stuck task)
4. No delegate mode
5. Lead does decomposition then goes silent until all agents finish
6. No synthesis of results — just merge and mark plan checkboxes

## Target State

### Orchestration Loop

Replace the 3-phase sequential model with a continuous coordination loop:

```
1. Decompose work into tasks
2. Spawn agents
3. Enter coordination loop:
   a. Poll messages from agents
   b. Monitor agent states
   c. Handle plan approvals
   d. Reassign stuck/failed work
   e. Respond to agent questions
   f. Detect completion
4. Synthesize results
5. Merge and cleanup
```

The lead stays active throughout execution, not just at the beginning and end.

### Delegate Mode

When enabled, the lead cannot:
- Edit files directly
- Run build/test commands
- Make code changes

The lead can only:
- Spawn/shutdown agents
- Send/receive messages
- Manage tasks (create, assign, update status)
- Review and approve plans
- Synthesize findings into a report

Enable via `TeamConfig.DelegateMode = true` or toggle at runtime.

### Dynamic Task Management

Lead can:
- **Add tasks** during execution (discovered work)
- **Reassign tasks** from stuck agents to idle agents
- **Split tasks** that are too large (agent reports "this is bigger than expected")
- **Cancel tasks** that are no longer needed

### Agent Monitoring

Lead tracks per-agent:
- Current state and how long in that state
- Task in progress
- Last message received
- Output volume (proxy for activity)

**Stuck detection:** If agent is `Working` for > 2x average task time and hasn't sent messages, lead sends a status check. If no response within 60s, lead can reassign.

### Plan Approval

When an agent submits a plan:
1. Lead receives `PlanSubmission` message
2. Lead evaluates plan against criteria (from user prompt or defaults):
   - Does it address the task description?
   - Does it touch only expected files?
   - Is scope appropriate?
3. Lead sends `PlanApproval` with `approved: true/false` and optional feedback
4. If rejected, agent revises and resubmits (max 3 cycles)

For Ralph, plan approval is done by AI (lead's model reviews the plan). The user can influence by setting criteria in the project prompt.

### Result Synthesis

After all tasks complete:
1. Lead collects all task results (commit messages, files changed, output summaries)
2. Produces a synthesis report
3. Reports: what was done, what failed, what needs manual attention
4. Updates implementation_plan.md checkboxes (existing behavior)

## Technical Requirements

1. **Rename `TeamController` to `TeamOrchestrator`** with continuous coordination loop.

2. **Implement coordination loop** - Runs after decomposition+spawn. Polls messages, monitors agents, handles events. Exits when all tasks resolved and all agents idle/stopped.

3. **Implement delegate mode** - Config flag. When true, lead's AI process is spawned without `--dangerously-skip-permissions` and with "You are a coordinator. Do not edit files." instruction.

4. **Implement stuck detection** - Track time-in-state per agent. Alert after threshold. Auto-reassign after timeout.

5. **Implement plan approval** - Lead processes `PlanSubmission` messages. Uses AI to evaluate. Sends `PlanApproval` response.

6. **Implement result synthesis** - After completion, lead produces summary from all task results.

7. **Add dynamic task management** - `AddTask()`, `ReassignTask()`, `CancelTask()` on TaskStore. Lead can call these during coordination loop.

## Architecture

### TeamOrchestrator

```csharp
public class TeamOrchestrator : IDisposable
{
    private readonly TaskStore _taskStore;
    private readonly MessageBus _leadBus;
    private readonly List<TeamAgent> _agents;
    private readonly TeamConfig _config;
    private readonly RalphConfig _ralphConfig;

    // Phase 1: Setup
    public async Task<IReadOnlyList<AgentTask>> DecomposeAsync(CancellationToken ct);
    public async Task SpawnAgents(CancellationToken ct);

    // Phase 2: Coordination (main loop)
    public async Task CoordinateAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Process lead's inbox
            var messages = _leadBus.Poll();
            await HandleMessages(messages);

            // Monitor agent states
            await CheckForStuckAgents();
            await CheckForIdleAgents();

            // Check completion
            if (AllTasksResolved() && AllAgentsIdleOrStopped())
                break;

            await Task.Delay(1000, ct);
        }
    }

    // Phase 3: Finalize
    public async Task SynthesizeResults(CancellationToken ct);
    public async Task MergeAndCleanup(CancellationToken ct);

    // Run all phases
    public async Task RunAsync(CancellationToken ct)
    {
        var tasks = await DecomposeAsync(ct);
        _taskStore.AddTasks(tasks);

        await SpawnAgents(ct);
        await CoordinateAsync(ct);
        await SynthesizeResults(ct);
        await MergeAndCleanup(ct);
    }

    // Dynamic management
    public void AddTask(AgentTask task);
    public void ReassignTask(string taskId, string newAgentId);
    public void CancelTask(string taskId);

    // Agent management
    public async Task<TeamAgent> SpawnAgent(AgentSpawnConfig config);
    public async Task RequestShutdown(string agentId);
    public async Task ShutdownAll();
}
```

### Coordination Loop Detail

```csharp
private async Task HandleMessages(IReadOnlyList<Message> messages)
{
    foreach (var msg in messages)
    {
        switch (msg.Type)
        {
            case MessageType.StatusUpdate:
                // Update internal tracking
                _agentStatus[msg.FromAgentId] = msg.Content;
                _ui?.UpdateAgentStatus(msg.FromAgentId, msg.Content);
                break;

            case MessageType.PlanSubmission:
                // AI-evaluate the plan
                var decision = await EvaluatePlan(msg);
                _leadBus.Send(msg.FromAgentId, MessageType.PlanApproval,
                    decision.Approved ? "approved" : decision.Feedback,
                    new() { ["approved"] = decision.Approved.ToString() });
                break;

            case MessageType.ShutdownResponse when msg.Metadata?["accepted"] == "false":
                // Agent rejected shutdown, note reason
                _ui?.Log($"Agent {msg.FromAgentId} deferred shutdown: {msg.Content}");
                break;

            case MessageType.Text:
                // Log for synthesis
                _agentFindings[msg.FromAgentId].Add(msg.Content);
                break;
        }
    }
}
```

## Acceptance Criteria

- [ ] Lead runs continuous coordination loop (not 3 sequential phases)
- [ ] Lead processes agent messages in real-time
- [ ] Plan approval: lead evaluates submitted plans and responds
- [ ] Stuck detection: lead identifies agents stuck on tasks
- [ ] Task reassignment: lead can move tasks from stuck to idle agents
- [ ] Delegate mode: lead cannot edit files when enabled
- [ ] Result synthesis: lead produces summary after all tasks complete
- [ ] Dynamic task addition during execution
- [ ] Lead updates TUI with agent progress in real-time
- [ ] Coordination loop exits cleanly when all work done

## Files to Modify/Create

| Action | File |
|--------|------|
| Rename+Rewrite | `TeamController.cs` → `TeamOrchestrator.cs` |
| Modify | `Program.cs` (wire up orchestrator) |
| Modify | `ConsoleUI.cs` (real-time status updates from orchestrator) |
| Modify | `Models/TeamConfig.cs` (add DelegateMode flag) |

## Dependencies
- [task-system.md](task-system.md) - TaskStore
- [agent-lifecycle.md](agent-lifecycle.md) - Agent state management
- [messaging.md](messaging.md) - MessageBus for lead-agent communication
