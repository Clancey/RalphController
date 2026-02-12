# Overview: Agent Teams for RalphController

## Status
In Progress

## Summary
Bring RalphController's teams mode to feature parity with Claude Code's agent-teams. The current implementation has the skeleton (TeamController, TeamAgent, TaskQueue, worktrees) but key features are missing or broken: dependency enforcement, inter-agent messaging, proper lead-agent coordination, and graceful lifecycle management.

## Reference
- Claude Code agent-teams docs: https://code.claude.com/docs/en/agent-teams

## Architecture Comparison

### Claude Code Agent Teams (Target)

| Component | Description |
|-----------|-------------|
| **Team Lead** | Main session that creates the team, spawns teammates, coordinates work |
| **Teammates** | Separate CLI instances, each with own context window |
| **Task List** | Shared list with dependency tracking; agents self-claim unblocked tasks |
| **Mailbox** | Direct messaging between any agents (not just lead<->teammate) |
| **Display Modes** | In-process (Shift+Up/Down) or split panes (tmux/iTerm2) |

Key behaviors:
- Lead decomposes work into tasks with explicit dependencies
- Tasks have states: pending, in_progress, completed
- Blocked tasks (unresolved dependencies) cannot be claimed
- Teammates can message each other directly, not just report to lead
- Lead can operate in **delegate mode** (coordination only, no code)
- Teammates can be required to **plan before implementing**
- Graceful shutdown: lead requests, teammate approves/rejects
- File-lock-based claim mechanism prevents race conditions

### RalphController Current State

| Component | Status | Issues |
|-----------|--------|--------|
| **TeamController** | Exists | 3-phase model (decompose→execute→merge) is sequential, not dynamic |
| **TeamAgent** | Exists | Runs loop but no messaging, no plan approval |
| **TaskQueue** | Exists | Priority queue works, but dependencies parsed and **never enforced** |
| **GitWorktreeManager** | Exists | Worktree isolation works, merge strategies work |
| **ConflictNegotiator** | Exists | AI-based conflict resolution works |
| **ConsoleUI** | Exists | Teams layout exists but no agent selection/messaging |
| **Mailbox/Messaging** | Missing | No inter-agent communication at all |
| **Delegate Mode** | Missing | Lead always tries to implement |
| **Plan Approval** | Missing | Agents jump straight to implementation |
| **Graceful Shutdown** | Missing | Agents killed via cancellation token only |

## Gap Analysis (Priority Order)

### P0 - Core (Teams Don't Work Without These)

1. **Task dependency enforcement** - `TaskQueue.TryClaim()` must block tasks with unresolved dependencies. Currently `DependsOn` is parsed but ignored.

2. **Agent lifecycle management** - Proper startup → running → idle → shutdown flow. Currently agents just loop until queue empty then stop. Need: idle state, lead-initiated shutdown requests, agent acknowledgement.

3. **Lead orchestration loop** - The lead needs a coordination loop that monitors agent states, reassigns stuck work, and synthesizes results. Currently it's fire-and-forget after decomposition.

### P1 - Communication (Enables Collaboration)

4. **Mailbox / messaging system** - Agents need to send messages to each other. Lead gets automatic delivery of teammate messages. Supports direct (1:1) and broadcast.

5. **Task claiming with file locks** - Prevent race conditions when multiple agents try to claim the same task. Currently uses `Interlocked.CompareExchange` which works in-process but needs file-lock for cross-process.

### P2 - Quality (Makes Teams Useful)

6. **Plan approval flow** - Lead can require teammates to produce a plan before implementation. Lead reviews and approves/rejects.

7. **Delegate mode** - Lead restricted to coordination-only tools (spawn, message, shutdown, task management). No direct code editing.

8. **TUI improvements** - Agent selection (Shift+Up/Down), direct messaging to individual agents, task list toggle (Ctrl+T).

### P3 - Polish

9. **Display modes** - In-process (current default) and split-pane (tmux/iTerm2).
10. **Hooks** - `TeammateIdle` and `TaskCompleted` hooks for quality gates.
11. **Team cleanup** - Proper resource cleanup (worktrees, task files, team config).

## Target Architecture

```
┌─────────────────────────────────────────────────┐
│                    Program.cs                     │
│  CLI entry → Config → Mode selection              │
└───────────────────┬─────────────────────────────┘
                    │
        ┌───────────▼───────────┐
        │   TeamOrchestrator    │  (renamed from TeamController)
        │                       │
        │ - Manages team lead   │
        │ - Spawns/shuts agents │
        │ - Monitors progress   │
        │ - Synthesizes results │
        └───┬───────────────┬───┘
            │               │
   ┌────────▼──┐    ┌──────▼────────┐
   │ TaskStore  │    │  MessageBus   │
   │            │    │               │
   │ - Tasks    │    │ - Mailboxes   │
   │ - Claims   │    │ - Delivery    │
   │ - Deps DAG │    │ - Broadcast   │
   │ - File lock│    │ - File-based  │
   └────────────┘    └───────────────┘
            │               │
   ┌────────▼───────────────▼────────┐
   │         TeamAgent (N)            │
   │                                  │
   │ - Claims tasks from TaskStore    │
   │ - Sends/receives via MessageBus  │
   │ - Runs in isolated worktree     │
   │ - Reports progress to lead      │
   │ - Supports plan-then-implement  │
   └──────────────────────────────────┘
```

## Storage Layout

```
~/.ralph/teams/{team-name}/
  config.json          # Team members, IDs, agent types
  tasks/
    tasks.json         # Shared task list (replaces .ralph-queue.json)
    claims.lock        # File lock for atomic claims
  mailbox/
    {agent-id}.jsonl   # Per-agent message inbox (append-only)
```

## Files to Modify/Create

| Action | File | Description |
|--------|------|-------------|
| Rename | `TeamController.cs` → `TeamOrchestrator.cs` | Better name, expanded role |
| Modify | `TeamAgent.cs` | Add messaging, plan mode, idle state, graceful shutdown |
| Modify | `Parallel/TaskQueue.cs` | Enforce dependencies, file-lock claims |
| Create | `Messaging/MessageBus.cs` | Inter-agent messaging system |
| Create | `Messaging/Message.cs` | Message model |
| Modify | `ConsoleUI.cs` | Agent selection, message display, task list toggle |
| Modify | `Models/AgentTask.cs` | Add dependency resolution state |
| Modify | `Models/TeamConfig.cs` | Add delegate mode, plan approval settings |
| Modify | `Program.cs` | Wire up new components |

## Implementation Order

Implement specs in this order (each spec is independently testable):

1. **task-system.md** - Fix dependency enforcement. Everything depends on this.
2. **agent-lifecycle.md** - Proper agent states and shutdown. Required for orchestration.
3. **messaging.md** - Inter-agent communication. Required for collaboration.
4. **orchestration.md** - Lead coordination loop. Ties 1-3 together.
5. **merge-and-conflicts.md** - Already mostly works; polish and integrate.
6. **tui.md** - UI improvements. Can be done in parallel with 3-4.
