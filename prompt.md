## Task Status Legend
- `[ ]` — Incomplete (not started or needs rework)
- `[?]` — Waiting to be verified (work done, needs verification by different agent)
- `[x]` — Complete (verified by a second agent)

## Context Loading
1. Read `AGENTS.md` for project context, build commands, and coding patterns
2. Read `specs/*` for detailed requirements and architecture
3. Read `implementation_plan.md` for current progress and task list

## Task Execution — VERIFICATION FIRST

### If you see a `[?]` task (Waiting Verification):
**Do this FIRST before any new work.**
- Review the implementation — check the code exists, compiles (`dotnet build`), and meets the spec
- Run any relevant tests or smoke checks
- If VERIFIED: Mark as `[x]` and commit
- If INCOMPLETE: Mark as `[ ]` with a note explaining what's missing

### If no `[?]` tasks exist:
- Pick ONE incomplete `[ ]` task — ALL tasks must be completed, not just high priority
- Implement it completely — no placeholders, no TODOs
- When done, mark as `[?]` (NOT `[x]`) — another agent must verify
- Commit your work immediately

## Git Commits — MANDATORY
After EVERY successful change: `git add -A && git commit -m 'Description'`
DO NOT skip commits. Small, atomic commits after each change.

## Error Handling
- File not found → use glob/search to locate it
- Command fails → try a different approach
- Stuck 3 times on the same issue → mark task as blocked, move to next task

## Rules
- Search before implementing — understand existing code first
- Read files before editing — never edit blind
- One task per iteration
- No placeholders or TODOs in code
- **NEVER mark your own work as `[x]` — only mark as `[?]` for verification**
- **Only mark `[x]` when verifying ANOTHER agent's `[?]` work**
- Use `Markup.Escape()` for all dynamic text in Spectre.Console
- Read stdout/stderr via `Task.Run()` BEFORE `WaitForExitAsync()` to avoid deadlock
- Use pattern matching for null safety: `is { Length: >= 8 } sha` not `sha?[..8]`
- Target .NET 8.0 — do not change TFM

---RALPH_STATUS---
STATUS: IN_PROGRESS | COMPLETE | BLOCKED
TASKS_COMPLETED: <number>
FILES_MODIFIED: <number>
TESTS_PASSED: true | false
EXIT_SIGNAL: true | false
NEXT_STEP: <brief description of next action>
---END_STATUS---

EXIT_SIGNAL = true ONLY when ALL `implementation_plan.md` items are `[x]`, all tests pass, no errors, specs fully implemented.
