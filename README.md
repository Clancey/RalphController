# RalphController

A .NET console application that implements the "Ralph Wiggum" autonomous AI coding agent loop pattern. This tool monitors and controls Claude CLI (or OpenAI Codex) running in a continuous loop to autonomously implement features, fix bugs, and manage codebases.

## Overview

RalphController automates the Ralph Wiggum technique:

1. **Infinite Loop Execution**: Runs AI CLI in a continuous loop, one task per iteration
2. **Prompt-Driven Development**: Uses `prompt.md` to guide the AI's behavior each iteration
3. **Self-Tracking Progress**: AI updates `implementation_plan.md` to track completed work
4. **Backpressure via Testing**: AI must run tests after changes; failures guide next iteration
5. **Self-Improvement**: AI documents learnings in `agents.md` for future context

## Features

- **Rich TUI**: Spectre.Console-based interface with live output, status, and controls
- **Pause/Resume/Stop**: Full control over the loop execution
- **Hot-Reload**: Automatically detects changes to `prompt.md`
- **Manual Injection**: Inject custom prompts mid-loop
- **Multi-Provider**: Supports Claude CLI and OpenAI Codex
- **Project Scaffolding**: Generates missing project files using AI
- **Circuit Breaker**: Detects stagnation (3+ loops without progress) and stops
- **Response Analyzer**: Detects completion signals and auto-exits when done
- **Rate Limiting**: Configurable API calls/hour (default: 100)
- **RALPH_STATUS**: Structured status reporting for progress tracking
- **Priority Levels**: High/Medium/Low task prioritization

## Installation

```bash
# Clone the repository
git clone https://github.com/yourusername/RalphController.git
cd RalphController

# Build the project
dotnet build

# Run
dotnet run -- /path/to/your/project
```

## Requirements

- .NET 8.0 SDK
- Claude CLI (`claude`) or OpenAI Codex CLI (`codex`) installed and configured
- Terminal with ANSI color support

## Usage

### Basic Usage

```bash
# Run with interactive prompts
dotnet run

# Specify target directory
dotnet run -- /path/to/project

# Use Claude (default)
dotnet run -- /path/to/project

# Use Codex
dotnet run -- /path/to/project --codex
```

### Keyboard Controls

| Key | State | Action |
|-----|-------|--------|
| `Enter` | Idle | Start the loop |
| `P` | Running | Pause after current iteration |
| `R` | Paused | Resume execution |
| `S` | Running/Paused | Stop after current iteration |
| `F` | Any | Force stop immediately |
| `I` | Any | Inject a custom prompt |
| `Q` | Any | Quit the application |

## Project Structure

RalphController expects the following files in the target project:

```
your-project/
├── agents.md              # AI learnings and project context
├── prompt.md              # Instructions for each iteration
├── implementation_plan.md # Progress tracking
└── specs/                 # Specification files
    └── *.md
```

### agents.md

Contains learnings and context for the AI agent:

- Build/test commands
- Common errors and solutions
- Project-specific patterns
- Architecture notes

### prompt.md

Instructions executed each iteration. Example:

```markdown
Study agents.md for project context.
Study specs/* for requirements.
Study implementation_plan.md for progress.

Choose the most important incomplete task.
Implement ONE thing.
Run tests after changes.
Update implementation_plan.md with progress.
Commit on success.

Don't assume not implemented - search first.
```

### implementation_plan.md

Tracks what's done, in progress, and pending:

```markdown
# Implementation Plan

## Completed
- [x] Set up project structure
- [x] Implement user authentication

## In Progress
- [ ] Add payment processing

## Pending
- [ ] Email notifications
- [ ] Admin dashboard

## Bugs/Issues
- None

## Notes
- Using Stripe for payments
```

### specs/

Directory containing specification markdown files that describe features to implement.

## How It Works

1. **Startup**: Validates project structure, offers to scaffold missing files
2. **Loop Start**: Reads `prompt.md` and sends to AI CLI
3. **Execution**: AI processes prompt, makes changes, runs tests
4. **Completion**: Iteration ends, controller waits for delay
5. **Repeat**: Next iteration begins with fresh prompt read

The AI is expected to:
- Update `implementation_plan.md` with progress
- Update `agents.md` with new learnings
- Commit successful changes
- Run tests to validate work

## Configuration

RalphController uses sensible defaults but can be customized:

| Setting | Default | Description |
|---------|---------|-------------|
| Prompt File | `prompt.md` | Main prompt file |
| Plan File | `implementation_plan.md` | Progress tracking file |
| Agents File | `agents.md` | AI learnings file |
| Specs Directory | `specs/` | Specifications folder |
| Iteration Delay | 1000ms | Delay between iterations |
| Cost Per Hour | $10.50 | Estimated API cost/hour |
| Max Calls/Hour | 100 | Rate limit for API calls |
| Circuit Breaker | Enabled | Detect and stop on stagnation |
| Response Analyzer | Enabled | Detect completion signals |
| Auto Exit | Enabled | Exit when completion detected |

## Safety Features

### Circuit Breaker
Prevents runaway loops by detecting stagnation:
- **No Progress**: Opens after 3+ loops without file changes
- **Repeated Errors**: Opens after 5+ loops with same error
- **States**: CLOSED (normal) → HALF_OPEN (monitoring) → OPEN (halted)

### Rate Limiting
Prevents API overuse:
- Default: 100 calls per hour
- Auto-waits when limit reached
- Hourly reset window

### Response Analyzer
Detects when work is complete:
- Parses `---RALPH_STATUS---` blocks from AI output
- Tracks completion signals ("all tasks complete", "project done")
- Detects test-only loops (stuck running tests without implementation)
- Auto-exits when confidence is high

### RALPH_STATUS Block
The AI should end each response with:
```
---RALPH_STATUS---
STATUS: IN_PROGRESS | COMPLETE | BLOCKED
TASKS_COMPLETED: <number>
FILES_MODIFIED: <number>
TESTS_PASSED: true | false
EXIT_SIGNAL: true | false
NEXT_STEP: <what to do next>
---END_STATUS---
```

## Contributing

Contributions welcome! Please read the contributing guidelines first.

## License

MIT License - see LICENSE file for details.

## Acknowledgments

Based on the "Ralph Wiggum" technique by Geoffrey Huntley.
