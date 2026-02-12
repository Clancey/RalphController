# AGENTS.md — RalphController Operating Guide

## Core Principle

**One task at a time.** Pick ONE incomplete task from `IMPLEMENTATION_PLAN.md`, complete it, verify it works, then move on.

- **ALL tasks must be completed** — not just high priority ones
- **Verification workflow**: After completing work, mark `[?]`. The next iteration verifies before marking `[x]`
- **Commit after every successful change** — small, atomic commits
- **Never skip a broken build** — fix it before moving on

## Goal: Implement Claude Code Agent-Teams Feature

RalphController's teams mode must match the Claude Code agent-teams architecture:

| Component | Claude Code | RalphController Equivalent |
|-----------|------------|---------------------------|
| **Team Lead** | Main session that spawns/coordinates | `TeamController` — orchestrates phases |
| **Teammates** | Independent Claude Code instances | `TeamAgent` — parallel workers in worktrees |
| **Shared Task List** | File-locked task list with claim/complete | `TaskQueue` — `ConcurrentDictionary` + disk persistence |
| **Mailbox / Messaging** | Inter-agent message passing | **NOT YET IMPLEMENTED** — agents currently can't message each other |
| **Task Dependencies** | Tasks block on unresolved deps | `AgentTask.DependsOn` exists but may not block execution |
| **Plan Approval** | Lead reviews teammate plans before impl | **NOT YET IMPLEMENTED** |
| **Delegate Mode** | Lead restricted to coordination only | **NOT YET IMPLEMENTED** |

### Key Gaps to Close

1. **Inter-agent messaging** — teammates need a mailbox system to share findings
2. **Task dependency enforcement** — blocked tasks must not be claimable
3. **Graceful teammate shutdown** — lead sends shutdown request, agent can accept/reject
4. **Plan approval flow** — teammate works read-only until lead approves plan
5. **Self-claiming tasks** — after finishing, agents auto-claim next unblocked task (partially done)
6. **Conflict-free file ownership** — warn or prevent two agents from editing the same file
7. **Real-time status/events** — lead sees teammate progress without polling

## Build Commands

```bash
# Build
dotnet build

# Build and run (dev)
dotnet run -- /path/to/project

# Run teams mode
dotnet run -- --teams 4 /path/to/project

# Pack as global tool
dotnet pack -o ./nupkg

# Install globally
dotnet tool install --global --add-source ./nupkg RalphController

# Test modes
dotnet run -- --test-streaming
dotnet run -- --test-aiprocess
dotnet run -- --test-output
```

## Test Commands

```bash
# No formal test project yet — use built-in test modes:
dotnet run -- --test-streaming      # Test stream-json parsing
dotnet run -- --test-aiprocess      # Test AI process launch
dotnet run -- --test-output         # Test console output rendering

# Single-run smoke test (runs one loop iteration then exits):
dotnet run -- /path/to/project --single-run
```

## Error Handling

- **Build errors**: Fix immediately. If stuck after 3 attempts, mark task as `[B]` (blocked) with a note and move on
- **Test failures / runtime crashes**: Fix before proceeding to the next task
- **File not found**: Use `Glob` or `list_directory` to locate — never guess paths
- **Process deadlocks**: Read stdout/stderr concurrently BEFORE `WaitForExitAsync()` (see critical patterns)
- **Spectre.Console crash**: Always `Markup.Escape()` dynamic text — brackets break the parser

## Task Selection

```
1. Read IMPLEMENTATION_PLAN.md
2. Find first incomplete [ ] task (ALL tasks must be completed, in order)
3. If task has dependencies, complete blocking tasks first
4. Execute selected task
5. Build: `dotnet build` — must succeed
6. Test: run relevant test mode if applicable
7. Commit changes with descriptive message
8. Update IMPLEMENTATION_PLAN.md — mark [?] for verification
9. Loop until ALL tasks are [x] complete
```

## Project-Specific Rules

1. **Target .NET 8.0** — do not change the TFM
2. **Spectre.Console for all TUI** — no raw `Console.Write` for user-facing output
3. **Escape all dynamic markup** — `Markup.Escape(text)` for any string that could contain `[` or `]`
4. **Process I/O pattern** — always read stdout/stderr via `Task.Run()` before `WaitForExitAsync()` to prevent deadlock
5. **Stream-JSON for Claude/Gemini** — use `--output-format text` when you need raw text (regex parsing)
6. **Thread safety** — `TaskQueue` uses `ConcurrentDictionary`; any new shared state must be thread-safe
7. **Disk persistence** — task queue persists to `.ralph-queue.json`; new state should persist similarly
8. **Config in `.ralph.json`** — all user-facing settings belong here, not hardcoded
9. **Null safety** — use pattern matching (`is { Length: >= 8 } sha`) not null-conditional + range (`sha?[..8]`)
10. **No generated code editing** — do not modify files under `obj/` or `bin/`
11. **Git worktrees for isolation** — each parallel agent works in its own worktree; never have two agents edit the same file in the same worktree
