# Implementation Plan

## Status Legend
- `[ ]` - Incomplete (not started or needs rework)
- `[?]` - Waiting to be verified (work done, needs verification by different agent)
- `[x]` - Complete (verified by a second agent)

---

## Verified Complete
- [x] Project initialized

## Waiting Verification
(Tasks here have been implemented but need another agent to verify)

## High Priority — Task System (spec: task-system.md)
- [x] Create `Parallel/FileLock.cs` — file-lock helper using `FileStream(FileShare.None)` with timeout
- [x] Rename/rewrite `Parallel/TaskQueue.cs` → `Parallel/TaskStore.cs` with dependency-aware claiming
- [x] Update `Models/Task.cs` — simplify to 3+1 states (Pending, InProgress, Completed, Failed), add `IsClaimable()` method
- [x] Change dependency references from title-based to stable task IDs (`task-1`, `task-2`, etc.)
- [x] Implement `TryClaim()` with file-lock and dependency enforcement
- [x] Add `GetClaimable()`, `GetBlockedBy()`, `GetInProgress()` queries
- [x] Add `TaskCompleted`, `TaskUnblocked`, `TaskFailed` events on TaskStore
- [x] Persist tasks to `~/.ralph/teams/{team}/tasks/tasks.json` instead of `.ralph-queue.json`
- [x] Add stale claim timeout release for crashed agents
- [x] Update decomposition parser to produce ID-based deps and resolve title references

## High Priority — Agent Lifecycle (spec: agent-lifecycle.md)
- [x] Define `AgentState` enum: Spawning, Ready, Claiming, Working, PlanningWork, Idle, ShuttingDown, Stopped, Error
- [x] Create `Models/AgentSpawnConfig.cs` with Name, Model, SpawnPrompt, RequirePlanApproval fields
- [x] Rewrite `TeamAgent.cs` run loop — state machine with proper transitions and `StateChanged` event
- [x] Implement idle polling with exponential backoff (1s→30s), wake on `TaskUnblocked` event
- [x] Implement graceful shutdown protocol (request → finish current task → stop)
- [x] Implement force-stop with 60s timeout fallback
- [x] Add spawn prompt support — prepend task-specific context to agent AI prompt
- [x] Implement plan-before-implement mode (read-only tools, submit plan to lead, wait for approval)
- [x] Emit lifecycle events: `AgentSpawned`, `AgentIdle`, `AgentWorking`, `AgentStopped`, `AgentError`

## High Priority — Messaging (spec: messaging.md)
- [x] Create `Messaging/Message.cs` model with MessageId, From, To, Type, Content, Metadata, Timestamp
- [x] Create `MessageType` enum: Text, StatusUpdate, ShutdownRequest, ShutdownResponse, PlanSubmission, PlanApproval, TaskAssignment, Broadcast
- [x] Create `Messaging/MessageBus.cs` — file-based JSONL per-agent inbox with read cursor tracking
- [x] Implement `Send()` with file-lock for concurrent write safety
- [x] Implement `Broadcast()` — append to all agent inboxes except sender
- [x] Implement `Poll()` — non-blocking read of new messages since cursor
- [x] Implement `WaitForMessages()` / `WaitForMessage(type)` — blocking with timeout
- [x] Integrate message processing into TeamAgent run loop (between tasks and during idle)
- [x] Store mailboxes at `~/.ralph/teams/{team}/mailbox/{agent-id}.jsonl`

## High Priority — Orchestration (spec: orchestration.md)
- [x] Rename/rewrite `TeamController.cs` → `TeamOrchestrator.cs`
- [x] Replace 3-phase sequential model with continuous coordination loop
- [x] Implement `CoordinateAsync()` — poll messages, monitor agents, handle events, detect completion
- [x] Implement plan approval flow — lead evaluates PlanSubmission via AI and responds
- [x] Implement stuck agent detection — alert if Working > 2x avg task time with no messages
- [?] Implement task reassignment — move tasks from stuck/crashed agents to idle agents
- [?] Implement delegate mode — restrict lead to coordination-only (no file edits)
- [x] Implement result synthesis — collect task results and produce summary report
- [x] Add dynamic task management: `AddTask()`, `ReassignTask()`, `CancelTask()` during execution
- [?] Wire orchestrator into `Program.cs` replacing TeamController usage

## Medium Priority — Merge & Conflicts (spec: merge-and-conflicts.md)
- [ ] Create `Merge/MergeManager.cs` with merge queue and file ownership tracking
- [ ] Create `Merge/MergeStatus.cs` enum (Pending, Queued, Merging, Merged, ConflictDetected, Resolved, Failed)
- [ ] Implement file ownership tracking (`Dictionary<string, string>` file→agentId) with overlap warnings
- [ ] Implement incremental merging — merge completed tasks immediately, don't wait for all agents
- [ ] Implement dependency-ordered merging (topological sort of DAG)
- [ ] Improve conflict negotiation prompt to include task descriptions and intent
- [ ] Add MergeStatus field to task model
- [ ] Extract merge logic from GitWorktreeManager into MergeManager

## Medium Priority — TUI (spec: tui.md)
- [ ] Create `TUI/TUIView.cs` enum: AgentList, AgentDetail, TaskList
- [ ] Create `TUI/InputHandler.cs` for keyboard input (Shift+Up/Down, Enter, Escape, Ctrl+T)
- [ ] Implement agent selection state with Shift+Up/Down cycling
- [ ] Implement per-agent output buffers (separate from mixed log)
- [ ] Implement Agent List View — agent list left, selected agent detail right
- [ ] Implement Agent Detail View — full-screen output of selected agent (Enter to enter, Esc to exit)
- [ ] Implement Task List View — table with ID, Title, Status, Agent, Deps (Ctrl+T toggle)
- [ ] Add status bar — team name, agent count, task progress, elapsed time
- [ ] Add user message input — type text to send message to selected agent
- [ ] Wire TUI to events: `StateChanged`, `TaskCompleted`, `TaskUnblocked`, `OutputReceived`
- [ ] Ensure all dynamic text uses `Markup.Escape()`

## Low Priority — Storage & Config
- [?] Create `~/.ralph/teams/{team}/config.json` team config with members array
- [x] Add `DelegateMode` flag to TeamConfig
- [?] Add `RequirePlanApproval` per-agent setting to TeamConfig
- [?] Team cleanup: remove worktrees, task files, mailbox, team config

## Low Priority — Polish
- [?] Display modes: in-process (default) and split-pane (tmux/iTerm2)
- [?] Hooks: `TeammateIdle` and `TaskCompleted` quality gate hooks
- [?] Agent crash recovery: partial task re-queued on restart
- [?] Max 3 plan revision cycles before lead override/reassign

## Bugs/Issues
- None yet

## Notes
- Implementation order per overview.md: task-system → agent-lifecycle → messaging → orchestration → merge → tui
- Task system is the foundation — nothing works without dependency enforcement
- Agent lifecycle and messaging can be partially developed in parallel
- Orchestration ties everything together — implement after the three foundations
- Existing code: TeamController.cs (~850L), TeamAgent.cs (568L), TaskStore.cs (new), FileLock.cs (new), ConsoleUI.cs (~1324L)
- Models/Task.cs is used instead of AgentTask.cs referenced in specs
- TaskQueue.cs has been replaced by TaskStore.cs — do not recreate
- No Messaging/, Merge/, or TUI/ directories exist yet — all need creation
- Critical pattern: always use `Markup.Escape()` for dynamic Spectre.Console text
- Critical pattern: read stdout/stderr concurrently BEFORE `WaitForExitAsync` to avoid deadlock
