# RalphController Specs

Specs are the **source of truth** for what needs to be built. Each spec file describes a feature or component in enough detail that an agent can implement from it, but concise enough to fit in context.

## Principle

```
specs + stdlib = generate
```

An agent reading a spec file plus standard library/framework docs should be able to produce a correct implementation without guessing.

## Spec File Format

Each spec follows this structure:

```markdown
# Feature Name

## Status
Draft | In Progress | Implemented | Needs Rework

## Summary
1-2 sentence description of what this feature does and why.

## Reference
Link to external spec or documentation this is based on (e.g., Claude Code docs).

## Current State
What exists today in the codebase. What works, what doesn't.

## Target State
What the implementation should look like when done.

## Technical Requirements
Concrete, numbered list of what must be built or changed.

## Architecture
Key types, interfaces, data flows. Pseudocode or diagrams where helpful.

## Acceptance Criteria
Checkboxes. If all boxes are checked, the feature is done.

## Files to Modify/Create
List of files this spec touches, so agents know scope.

## Dependencies
Other specs that must be implemented first.
```

## Spec Index

| File | Feature | Status |
|------|---------|--------|
| [overview.md](overview.md) | Project goals, architecture, gap analysis | Draft |
| [task-system.md](task-system.md) | Shared task list with dependencies | Draft |
| [agent-lifecycle.md](agent-lifecycle.md) | Agent spawning, execution, shutdown | Draft |
| [messaging.md](messaging.md) | Inter-agent communication (mailbox) | Draft |
| [orchestration.md](orchestration.md) | Lead agent coordination and delegation | Draft |
| [merge-and-conflicts.md](merge-and-conflicts.md) | Merge strategies and conflict resolution | Draft |
| [tui.md](tui.md) | Terminal UI for agent teams | Draft |

## Guidelines for Writing Specs

1. **Be concrete** - Name files, classes, methods. Don't say "add a component"; say "add `MessageBus.cs` implementing `IMessageBus`".
2. **Show the delta** - Describe what changes from the current state, not a full rewrite spec.
3. **Include edge cases** - If an agent can get confused, spell it out.
4. **Keep it scannable** - Agents have limited context. Use tables, bullet lists, and short paragraphs.
5. **One feature per file** - Specs should be independently implementable where possible.
6. **Reference the source** - Our target is feature parity with Claude Code agent-teams. Always cite the reference doc.
