# RalphController

A .NET console application that implements the "Ralph Wiggum" autonomous AI coding agent loop pattern. This tool monitors and controls AI CLI tools running in a continuous loop to autonomously implement features, fix bugs, and manage codebases.

Point it at an empty directory with a project description, and watch it build your entire application from scratch. Or use it on an existing codebase to autonomously fix bugs and add features.

## Overview

RalphController automates the Ralph Wiggum technique:

1. **Infinite Loop Execution**: Runs AI CLI in a continuous loop, one task per iteration
2. **Prompt-Driven Development**: Uses `prompt.md` to guide the AI's behavior each iteration
3. **Self-Tracking Progress**: AI updates `implementation_plan.md` to track completed work
4. **Backpressure via Testing**: AI must run tests after changes; failures guide next iteration
5. **Self-Improvement**: AI documents learnings in `agents.md` for future context

## Features

### Core Features
- **Rich TUI**: Spectre.Console-based interface with real-time streaming output, status, and controls
- **Live Streaming**: See AI output as it's generated, not just after completion
- **Project Scaffolding**: Generate all project files from a description or spec file
- **Re-initialization**: Use `--init` to regenerate project files with new requirements

### Multi-Agent Collaboration
- **Task Workflow**: Planner → Implementer → Reviewer pipeline for each iteration
- **Model Tiers**: Automatic Expert/Capable/Fast tier selection based on role requirements
- **Provider Interleaving**: Rotates through providers to spread API usage
- **Git Integration**: Automatically commits approved changes with descriptive messages

### Multi-Provider Support
- **Providers**: Claude, Codex, GitHub Copilot, Gemini, Cursor, OpenCode, and Ollama/LMStudio
- **Provider Detection**: Automatically detects which AI CLIs are installed
- **Multi-Model**: Rotate between multiple models or use verification model
- **Provider Persistence**: Remembers your provider choice per project in `.ralph.json`

### Safety & Control
- **Final Verification**: Before completing, verifies all tasks are truly done
- **Pause/Resume/Stop**: Full control over the loop execution
- **Hot-Reload**: Automatically detects changes to `prompt.md`
- **Manual Injection**: Inject custom prompts mid-loop
- **Circuit Breaker**: Detects stagnation (3+ loops without progress) and stops
- **Response Analyzer**: Detects completion signals and auto-exits when done
- **Rate Limiting**: Configurable API calls/hour (default: 100)

### Progress Tracking
- **RALPH_STATUS**: Structured status reporting for progress tracking
- **Priority Levels**: High/Medium/Low task prioritization
- **Global Tool**: Install as `ralph` command, run from any directory

## Quick Start

### New Project (Empty Directory)

RalphController can bootstrap an entire project from scratch. Just describe what you want to build:

```bash
# Point it at an empty directory
dotnet run -- /path/to/new-project

# When prompted for missing files, choose "Generate files using AI"
# Then provide either:
#   1. A description: "A REST API for task management with SQLite backend"
#   2. A path to a spec file: "./specs/project-spec.md" or "~/Documents/my-idea.txt"
```

RalphController will use AI to generate:
- `prompt.md` - Instructions for each loop iteration
- `implementation_plan.md` - Task list with priorities
- `agents.md` - Project context and learnings
- `specs/` - Specification files based on your description

### Existing Project

```bash
# Point at a project with Ralph files already set up
dotnet run -- /path/to/existing-project
```

## Installation

### As a Global Tool (Recommended)

```bash
# Install from local source
dotnet pack -o ./nupkg
dotnet tool install --global --add-source ./nupkg RalphController

# Now use it from anywhere
ralph                           # Run in current directory
ralph /path/to/project          # Run in specified directory
ralph --copilot                 # Use GitHub Copilot
```

### Updating the Global Tool

```bash
# After making changes, rebuild and update
dotnet pack -o ./nupkg
dotnet tool uninstall --global RalphController
dotnet tool install --global --add-source ./nupkg RalphController
```

### Uninstalling

```bash
dotnet tool uninstall --global RalphController
```

### From Source

```bash
# Clone the repository
git clone https://github.com/clancey/RalphController.git
cd RalphController

# Build and run
dotnet build
dotnet run -- /path/to/your/project
```

## Requirements

- .NET 8.0 SDK
- At least one AI CLI installed and configured:
  - Claude CLI (`claude`) - [Anthropic](https://docs.anthropic.com/claude/docs/claude-cli)
  - Codex CLI (`codex`) - [OpenAI](https://github.com/openai/codex-cli)
  - Copilot CLI (`copilot`) - [GitHub](https://github.com/github/copilot-cli)
  - Gemini CLI (`gemini`) - [Google](https://github.com/google/gemini-cli)
  - Cursor CLI (`cursor`) - [Cursor](https://cursor.sh)
  - OpenCode CLI (`opencode`) - [OpenCode](https://opencode.ai/docs/cli/)
  - Ollama/LMStudio (via HTTP API) - No CLI needed
- Terminal with ANSI color support

## Usage

### Basic Usage

```bash
# Run in current directory (uses saved provider or prompts)
ralph

# Specify target directory
ralph /path/to/project

# Use a specific provider
ralph --claude              # Anthropic Claude
ralph --codex               # OpenAI Codex
ralph --copilot             # GitHub Copilot
ralph --gemini              # Google Gemini
ralph --cursor              # Cursor AI
ralph --opencode            # OpenCode
ralph --ollama              # Ollama/LMStudio

# Or use --provider flag
ralph --provider copilot
ralph --provider gemini
ralph --provider cursor

# Specify a model
ralph --claude --model opus
ralph --gemini --model gemini-2.5-flash
ralph --cursor --model gpt-4o
ralph --copilot --model gpt-5.1

# Specify a model for OpenCode (provider/model)
ralph --opencode --model anthropic/claude-3-5-sonnet
ralph --opencode --model ollama/llama3.1:70b
ralph --opencode --model lmstudio/qwen/qwen3-coder-30b

# Or let it prompt with a list of available models
ralph --opencode

# List available models for OpenCode
ralph --list-models

# Ignore saved settings from .ralph.json
ralph --fresh

```

### Provider Detection

Ralph automatically detects which AI providers are installed on your system. When you run `ralph` without specifying a provider, it will only show providers that are actually available:

```bash
# If you have claude, codex, and gemini installed:
ralph
# Shows: Claude, Codex, Gemini, Ollama (always available)

# Providers are detected using 'which' command
# Ollama is always shown since it uses HTTP API
```

### Provider Persistence

Ralph remembers your provider choice per project in `.ralph.json`:

```bash
# First time - prompts for provider, saves to .ralph.json
ralph

# Second time - automatically uses saved provider
ralph
# Output: Using saved provider from .ralph.json

# Override with command line flag
ralph --copilot             # Uses Copilot, updates .ralph.json

# Ignore saved settings
ralph --fresh               # Prompts for provider even if saved

# For OpenCode, when prompted for model, it shows a selectable list of available models
ralph --opencode            # Shows model selection menu
```

### Re-initialize with New Spec

Use `--init` or `--spec` to regenerate all project files with new instructions:

```bash
# Provide spec inline
ralph --init "Build a REST API for managing todo items with SQLite"

# Provide spec from file
ralph --init ./new-requirements.md

# Interactive - prompts for spec
ralph --init
```

This regenerates:
- `prompt.md` - New iteration instructions
- `implementation_plan.md` - New task breakdown with priorities
- `agents.md` - New project context and build commands
- `specs/` - New specification files

Use this when pivoting direction or starting fresh with new requirements.

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

## Project Scaffolding

When you point RalphController at a directory missing required files, you'll be prompted with options:

1. **Generate files using AI** - Provide a project description or spec file path
2. **Create default template files** - Use generic templates (recommended for code-focused models)
3. **Continue anyway** - Skip scaffolding (requires at least `prompt.md`)
4. **Exit** - Cancel

> **Warning: Code-focused models (like qwen-coder, deepseek-coder, codellama) often fail at scaffolding because they don't follow meta-instructions well. They tend to echo the spec content instead of generating proper Ralph files.**
>
> **Recommended approach:**
> - Use **"Create default template files"** option, then manually customize them
> - Or use a general-purpose model (like llama3, mistral, or claude) for scaffolding only
> - Code-focused models work great for the actual coding loop once files are set up

### Using a Spec File

For complex projects, write your requirements in a document first:

```markdown
# My Project Spec

## Overview
A command-line tool for managing personal finances...

## Features
- Import transactions from CSV
- Categorize expenses automatically
- Generate monthly reports
- Export to PDF

## Technical Requirements
- .NET 8
- SQLite for storage
- Support Windows/Mac/Linux
```

Then provide the path when prompted:

```bash
dotnet run -- /path/to/empty-project
# Choose "Generate files using AI"
# Enter: /path/to/my-spec.md
```

The AI will read your spec and generate tailored project files with appropriate tasks, build commands, and specifications.

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
| Final Verification | Enabled | Verify all tasks before stopping |
| Multi-Model | Disabled | See Multi-Model Support section |

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

### Final Verification

When the AI signals completion, Ralph runs a final verification step:

1. **Verification Prompt**: Ralph injects a special prompt asking the AI to review each task
2. **Task Review**: The AI verifies each item in `implementation_plan.md` is truly complete
3. **Structured Response**: AI reports findings in a structured format:

```
---VERIFICATION_RESULT---
OVERALL_STATUS: COMPLETE or INCOMPLETE

COMPLETED_TASKS:
- Task 1 that is done
- Task 2 that is done

INCOMPLETE_TASKS:
- Task that still needs work: What's missing

SUMMARY: Brief summary of findings
---END_VERIFICATION---
```

4. **Decision**:
   - If all tasks complete → Ralph stops
   - If any tasks incomplete → Ralph continues iterating with the standard prompt

This prevents premature completion by ensuring all work is actually done before stopping.

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

## Multi-Model Support

RalphController supports running multiple AI models in a single session with several strategies:

### Model Rotation (Round Robin)

Cycle through different models each iteration. Useful for:
- Cost optimization (alternate expensive/cheap models)
- Different perspectives on problem-solving
- Avoiding model-specific blind spots

You can add **as many models as you want** to the rotation. During setup, Ralph will keep asking "Add another model to the rotation?" until you say no.

### Verification Mode

When the primary model signals completion, run the same prompt with a verification model. If the verifier makes no changes, the task is truly complete. If it makes changes, continue working.

This prevents premature completion by getting a "second opinion" from a different model.

### Interactive Setup

When starting Ralph, you'll be prompted to configure multi-model after selecting your primary model:

```
Multi-model configuration:
> Single model (default)
  Verification model - use a second model to verify completion
  Round-robin rotation - alternate between models each iteration
```

For round-robin, you can add multiple models:

```
Add model #2 for rotation:
  Model 2 - Select provider: Claude
  Select Claude model: opus

Add another model to the rotation? [y/n]: y

Add model #3 for rotation:
  Model 3 - Select provider: Gemini
  Select Gemini model: gemini-2.5-pro

Add another model to the rotation? [y/n]: n

Multi-model: RoundRobin - sonnet → opus → gemini-2.5-pro
```

### Manual Configuration

You can also configure multi-model directly in your `.ralph.json`:

**Round Robin (Multiple Models):**
```json
{
  "multiModel": {
    "strategy": "RoundRobin",
    "rotateEveryN": 1,
    "models": [
      { "provider": "Claude", "model": "sonnet", "label": "Sonnet" },
      { "provider": "Claude", "model": "opus", "label": "Opus" },
      { "provider": "Gemini", "model": "gemini-2.5-pro", "label": "Gemini Pro" },
      { "provider": "Cursor", "model": "gpt-4o", "label": "Cursor GPT-4o" }
    ]
  }
}
```

**Verification (Sonnet primary, Opus verifier):**
```json
{
  "multiModel": {
    "strategy": "Verification",
    "models": [
      { "provider": "Claude", "model": "sonnet", "label": "Primary" },
      { "provider": "Claude", "model": "opus", "label": "Verifier" }
    ],
    "verification": {
      "verifierIndex": 1,
      "trigger": "CompletionSignal",
      "maxVerificationAttempts": 3
    }
  }
}
```

**Cross-Provider (Claude + Gemini + Ollama):**
```json
{
  "multiModel": {
    "strategy": "RoundRobin",
    "models": [
      { "provider": "Claude", "model": "sonnet" },
      { "provider": "Gemini", "model": "gemini-2.5-flash" },
      { "provider": "Ollama", "model": "qwen2.5-coder:32b", "baseUrl": "http://localhost:11434" }
    ]
  }
}
```

### Strategies

| Strategy | Description |
|----------|-------------|
| `None` | Single model (default behavior) |
| `RoundRobin` | Cycle through models each N iterations |
| `Verification` | Use secondary model to verify completion |
| `Fallback` | Switch to backup model on failure/rate limit |

### Verification Triggers

| Trigger | Description |
|---------|-------------|
| `CompletionSignal` | When ResponseAnalyzer detects task completion |
| `EveryNIterations` | Run verification every N iterations |
| `Manual` | User-triggered (future feature) |

### How Model Verification Works

1. Primary model runs normally
2. When completion is detected, verification model runs the **same prompt**
3. If verifier makes **no file changes** → task verified complete, exit
4. If verifier makes **any changes** → not truly done, continue with primary

This elegant approach requires no special verification prompts - just run another model and see if it agrees nothing needs to change.

## Multi-Agent Collaboration Workflows

RalphController supports sophisticated multi-agent collaboration workflows that go beyond simple model rotation. These workflows enable structured collaboration between multiple AI agents with specialized roles.

### Task Workflow (Primary Mode)

When collaboration is enabled, each iteration runs the **full task workflow**:

```
┌─────────────────────────────────────────────────────────────────┐
│                    TASK WORKFLOW                                │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌──────────┐     ┌──────────────┐     ┌──────────┐            │
│  │ PLANNER  │ ──▶ │ IMPLEMENTER  │ ──▶ │ REVIEWER │            │
│  │ (Expert) │     │  (Capable)   │     │(Capable) │            │
│  └──────────┘     └──────────────┘     └──────────┘            │
│       │                  │                   │                  │
│       │                  │                   ▼                  │
│       │                  │            ┌────────────┐            │
│       │                  │            │  Approved? │            │
│       │                  │            └────────────┘            │
│       │                  │              │       │               │
│       │                  │            Yes       No              │
│       │                  │              │       │               │
│       │                  │              ▼       ▼               │
│       │                  │         ┌────────┐  Loop             │
│       │                  │         │  GIT   │  Back             │
│       │                  │         │ COMMIT │                   │
│       │                  │         └────────┘                   │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

1. **Planner** (Expert tier) - Analyzes the task and creates an implementation plan
2. **Implementer** (Capable tier) - Actually modifies files according to the plan
3. **Reviewer** (Capable tier) - Reviews the changes for correctness
4. **Git Commit** - If approved, commits all changes with descriptive message

Each agent is a **real AI process** that can read and modify files using CLI tools (claude, copilot, codex, etc.).

### Agent Roles

| Role | Tier | Purpose |
|------|------|---------|
| **Planner** | Expert | Breaks down features into implementation steps |
| **Synthesizer** | Expert | Combines multiple perspectives into recommendations |
| **Implementer** | Capable | Primary coding agent that writes features |
| **Reviewer** | Capable | Code review with severity-based findings |
| **Advocate** | Capable | Highlights strengths and opportunities |
| **Challenger** | Fast | Questions assumptions, finds edge cases and issues |

### Model Tier System

Models are automatically classified into tiers based on their capabilities:

| Tier | Models | Used For |
|------|--------|----------|
| **Expert** | opus, pro, 70b+, gpt-5, glm-4 | Planning, synthesis, complex reasoning |
| **Capable** | sonnet, 13b-30b, gpt-4, gemini-pro | Implementation, thorough review |
| **Fast** | haiku, flash, mini, 7b-8b | Quick checks, challenges, validation |

**Runtime Selection**: Models are selected at runtime from ALL available models, filtered by the required tier for each role. No pre-assignment needed.

**Provider Interleaving**: Models are rotated through different providers first to spread API usage:
```
[Claude-Opus, Gemini-Pro, Ollama-70b, Claude-Sonnet, Gemini-Flash, ...]
```

### Custom Tier Overrides

Add custom tier mappings in `.ralph.json`:

```json
{
  "collaboration": {
    "tierOverrides": {
      "my-custom-model": "Expert",
      "cheap-model": "Fast",
      "glm-4.7": "Expert"
    }
  }
}
```

### Additional Workflow Types

Beyond the primary Task Workflow, Ralph supports specialized workflows:

#### 1. Spec Workflow (Feature Specification)

Multi-agent workflow for creating robust feature specifications:

1. **Planner** creates initial spec from feature request
2. **Challenger** critiques the spec, finds issues
3. **Synthesizer** combines feedback into final spec

```bash
# Start spec workflow from TUI (press W for workflow menu)
```

#### 2. Review Workflow (Code Review)

Parallel multi-reviewer code review with severity tracking:

1. **Multiple Reviewers** analyze code in parallel (configurable count)
2. **Findings** consolidated and deduplicated
3. **Expert Validation** for critical issues (optional)

Severity levels:
- **CRITICAL**: Must fix before merge (security, data loss)
- **HIGH**: Should fix (bugs, significant issues)
- **MEDIUM**: Consider fixing (code quality)
- **LOW**: Nice to have (style improvements)
- **INFO**: Observations only

#### 3. Verification Workflow (Replaces Single-Model Verification)

Multi-agent verification when completion is detected:

1. **Multiple Verifiers** run in parallel to check work
2. **Challenger** looks for missed edge cases
3. **Consensus** determines if work is truly complete
4. If passed, **Final Verification** checks all tasks in implementation_plan.md

This replaces the old single-model verification with a more thorough multi-agent review.

#### 4. Consensus Workflow

Blinded multi-model analysis to prevent groupthink:

1. **Multiple Models** with different stances (For, Against, Neutral)
2. Each model only sees the original proposal (blinded)
3. **Synthesizer** combines opinions into recommendations

### Configuration

Enable collaboration in `.ralph.json`:

**Minimal Configuration** (recommended - uses runtime tier selection):

```json
{
  "collaboration": {
    "enabled": true
  },
  "multiModel": {
    "strategy": "RoundRobin",
    "models": [
      { "provider": "Claude", "model": "opus" },
      { "provider": "Claude", "model": "sonnet" },
      { "provider": "Gemini", "model": "gemini-2.5-pro" },
      { "provider": "Ollama", "model": "qwen3-coder:30b", "baseUrl": "http://localhost:11434" }
    ]
  }
}
```

With this config, Ralph automatically:
- Selects **Expert tier** models (opus, gemini-pro) for Planner/Synthesizer
- Selects **Capable tier** models (sonnet, qwen3) for Implementer/Reviewer
- Selects **Fast tier** models for Challenger
- Rotates through providers to spread API usage

**Full Configuration** (for advanced customization):

```json
{
  "collaboration": {
    "enabled": true,
    "promptsDirectory": "prompts",
    "tierOverrides": {
      "my-custom-model": "Expert"
    },
    "verification": {
      "enabled": true,
      "reviewerCount": 2,
      "enableChallenger": true,
      "requireUnanimous": false,
      "parallelExecution": true
    },
    "review": {
      "reviewerCount": 2,
      "expertValidation": true,
      "minSeverity": "Low",
      "focusAreas": ["Full", "Security"]
    },
    "spec": {
      "enableChallenger": true,
      "maxRefinements": 2
    },
    "agents": {
      "Planner": {
        "model": { "provider": "Claude", "model": "opus" }
      },
      "Reviewer": {
        "modelPool": [
          { "provider": "Claude", "model": "sonnet" },
          { "provider": "Gemini", "model": "gemini-2.5-pro" }
        ]
      }
    }
  }
}
```

### Model Selection (Automatic by Default)

By default, Ralph uses **automatic tier-based selection**:
- Models are selected at runtime from your `multiModel.models` list
- Each role gets models filtered by its required tier (Expert/Capable/Fast)
- No manual configuration needed - just add your models and Ralph figures out the rest

The system uses the following priority for selecting models:

1. **Role-specific model** - If configured in `agents.[Role].model`
2. **Role-specific pool** - Round-robin through `agents.[Role].modelPool`
3. **Global pool by tier** - All models from `multiModel.models`, filtered by required tier
4. **Default provider** - Falls back to the configured provider

### Manual Agent Configuration (Optional)

If you want to override the automatic selection for specific roles, configure agents manually in `.ralph.json`:

```json
{
  "collaboration": {
    "enabled": true,
    "agents": {
      "Planner": {
        "model": { "provider": "Claude", "model": "opus" }
      },
      "Implementer": {
        "modelPool": [
          { "provider": "Claude", "model": "sonnet" },
          { "provider": "Gemini", "model": "gemini-2.5-pro" }
        ]
      },
      "Reviewer": {
        "model": { "provider": "Ollama", "model": "qwen3-coder:30b", "baseUrl": "http://localhost:11434" }
      }
    }
  }
}
```

Roles not configured in `agents` will use automatic tier-based selection from your model pool.

### Parallel Execution

Workflows execute steps in parallel where possible:

- **Multiple Reviewers**: All run simultaneously
- **Consensus Opinions**: All participants analyze in parallel
- **Sequential Steps**: Synthesizer waits for all inputs

This dramatically reduces workflow time compared to sequential execution.

### Custom Agent Prompts

Place custom prompts in the `prompts/` directory:

```
your-project/
├── prompts/
│   ├── planner.md      # Planning agent instructions
│   ├── challenger.md   # Challenge/critique instructions
│   ├── reviewer.md     # Code review format
│   ├── advocate.md     # Strength-finding instructions
│   ├── synthesizer.md  # Synthesis instructions
│   └── implementer.md  # Implementation guidelines
```

Each prompt file should define the agent's role, focus areas, and output format.

### How Task Workflow Works

Each iteration with collaboration enabled:

1. **Planner** (Expert model) analyzes the task:
   - Breaks down requirements into steps
   - Identifies files to modify
   - Creates implementation plan

2. **Implementer** (Capable model) executes the plan:
   - Actually modifies code files via CLI tools
   - Handles edge cases from planner
   - Runs tests if appropriate

3. **Reviewer** (Capable model) checks the implementation:
   - Reviews changes for correctness
   - Identifies issues or improvements
   - Approves or requests changes

4. **If Approved** → Git Commit:
   - All changes staged and committed
   - Commit message: `[Ralph] <task summary>`
   - Continues to next iteration

5. **If Not Approved** → Fix Loop:
   - Implementer addresses review feedback
   - Reviewer checks again
   - Repeats up to max review cycles

### Git Commit Integration

When the Reviewer approves changes, Ralph automatically:
1. Stages all modified files (`git add -A`)
2. Creates commit with descriptive message
3. Shows commit status in output

```
▶ [Git] Committing approved changes...
  ✓ Changes committed to git
```

Commit messages follow the format:
```
[Ralph] <first 100 chars of task description>

Modified N file(s) via multi-agent workflow.
```

### Verification After Completion

When the AI signals overall completion:

1. **Workflow Verification** runs (if enabled):
   - Multiple verifiers review the changes in parallel
   - Challenger looks for missed edge cases
   - Requires majority (or unanimous) approval

2. **If Workflow Verification Passes**:
   - **Final Task Verification** runs
   - Checks all tasks in `implementation_plan.md`
   - Ensures nothing marked incomplete

3. **If Any Verification Fails**:
   - Continue iterating with identified issues
   - Required changes are logged

This two-stage verification ensures both code quality (workflow) and task completeness (final).

## Testing & Debug Modes

RalphController includes several test modes for debugging:

```bash
# Test AI streaming output
dotnet run -- --test-streaming

# Run a single iteration without TUI
dotnet run -- /path/to/project --single-run

# Test AIProcess class directly
dotnet run -- --test-aiprocess

# Test process output capture
dotnet run -- --test-output

# Run without TUI (console mode)
ralph --no-tui
ralph --console
```

## Streaming Output

RalphController streams AI output in real-time:

- **Claude**: Uses `--output-format stream-json` to parse streaming events
- **Gemini**: Uses `-o stream-json` for streaming
- **Codex**: Native streaming via stdout

Output is buffered line-by-line to prevent split words while maintaining real-time feedback.

## Configuring Ollama Models for OpenCode

When using Ralph with OpenCode and local Ollama models, you may encounter issues where the AI responds with text but doesn't actually execute tools. This is because Ollama models default to a 4096 token context window, which is too small for OpenCode's system prompt and tool definitions.

### The Problem

Ollama models have a default context window of 4096 tokens. OpenCode requires a larger context to properly include:
- System prompts
- Tool definitions (bash, read, write, edit, etc.)
- Conversation history

When the context is too small, the model receives truncated tool definitions and falls back to outputting tool calls as text rather than using native function calling.

### Solution: Create a Model with Larger Context

**Step 1: Run the model interactively**

```bash
# SSH to your Ollama server or run locally
ollama run qwen3-coder:30b
```

**Step 2: Increase the context window**

In the Ollama interactive prompt:
```
>>> /set parameter num_ctx 32768
```

**Step 3: Save as a new model**

```
>>> /save qwen3-coder:30b-32k
>>> /bye
```

**Step 4: Configure OpenCode**

Add the new model to `~/.config/opencode/opencode.json`:

```json
{
  "provider": {
    "ollama": {
      "npm": "@ai-sdk/openai-compatible",
      "options": {
        "baseURL": "http://localhost:11434/v1"
      },
      "models": {
        "qwen3-coder:30b-32k": {
          "name": "qwen3-coder:30b-32k",
          "tools": true,
          "supportsToolChoice": true
        }
      }
    }
  }
}
```

**Step 5: Use with Ralph**

```bash
ralph --opencode --model ollama/qwen3-coder:30b-32k
```

### Recommended Context Sizes

| Model Size | Recommended `num_ctx` |
|------------|----------------------|
| 7B-8B      | 16384                |
| 13B-30B    | 32768                |
| 70B+       | 32768-65536          |

> **Note**: Larger context windows require more VRAM. Adjust based on your hardware capabilities.

### Troubleshooting

If tool calling still doesn't work:

1. **Verify the model supports tools**: Not all models support native function calling. Check Ollama's model page for a "tools" tag.

2. **Check OpenCode logs**: Run with `--print-logs --log-level DEBUG` to see what's being sent to the API.

3. **Test the API directly**: Verify Ollama returns proper `tool_calls`:
   ```bash
   curl http://localhost:11434/v1/chat/completions -d '{
     "model": "qwen3-coder:30b-32k",
     "messages": [{"role": "user", "content": "hi"}],
     "tools": [{"type": "function", "function": {"name": "test", "parameters": {}}}]
   }'
   ```

For more details, see the [OpenCode Ollama setup guide](https://github.com/p-lemonish/ollama-x-opencode).

## Configuring LM Studio

When using Ralph with the `--ollama` flag pointing to LM Studio, you need to configure sufficient context length for the AI to process prompts and generate responses.

### The Problem

LM Studio defaults to a 4096 token context window, which is often too small for:
- Project scaffolding (reading spec files)
- Long conversations with tool calling
- Processing large codebases

### Solution: Configure Context Settings in LM Studio

LM Studio has two context-related settings you need to configure:

**Step 1: Open Model Settings**

In LM Studio, click the gear icon next to your loaded model to open settings.

**Step 2: Set "Model supports up to"**

> *"This is the maximum number of tokens the model was trained to handle. Click to set the context to this value."*

This setting defines the architectural limit of the model. You must set this first to unlock higher context lengths.

| Model | Model Supports Up To |
|-------|---------------------|
| Qwen3-Coder (any size) | 131072 (128K) |
| Llama 3.x | 8192 (or 131072 for extended) |
| DeepSeek Coder | 16384 (16K) |
| Mistral/Mixtral | 32768 (32K) |

**Recommended**: For Qwen3-Coder, set to **131072**.

**Step 3: Set "Context Length"**

> *"The maximum number of tokens the model can attend to in one prompt. See the Conversation Overflow options under 'Inference params' for more ways to manage this."*

This is the actual working context for your session. It must be ≤ the "Model supports up to" value.

| Model Size | Recommended Context Length | VRAM Required |
|------------|---------------------------|---------------|
| 7B-8B      | 8192 (8K)                 | ~6GB          |
| 13B-14B    | 16384 (16K)               | ~12GB         |
| 30B-32B    | 32768 (32K)               | ~24GB         |
| 70B+       | 32768-65536 (32-64K)      | ~48GB+        |

**Recommended for Ralph**:
- Set "Model supports up to" to the model's max (e.g., 131072 for Qwen3-Coder)
- Set "Context Length" to at least **16384** (16K), or **32768** (32K) for large spec files

### Usage with Ralph

```bash
# Using LM Studio as Ollama provider
ralph --ollama --url http://127.0.0.1:1234 --model qwen/qwen3-coder-30b

# Or point to a remote LM Studio server
ralph --ollama --url http://192.168.1.100:1234 --model your-model-name
```

### Troubleshooting

**Error: "tokens to keep from initial prompt is greater than context length"**
- Increase context length in LM Studio settings
- Try a smaller spec file for scaffolding
- Ralph automatically truncates prompts over 3000 chars for Ollama/LMStudio

**Model generates code instead of markdown files**
- Code-focused models (like qwen-coder) may try to implement rather than scaffold
- Consider using a general-purpose model for initial scaffolding
- Or manually create scaffold files and use code models for implementation

**Slow generation**
- Larger context windows require more computation
- Consider using a smaller context if you don't need it
- GPU acceleration significantly improves speed

## Contributing

Contributions welcome! Please read the contributing guidelines first.

## License

MIT License - see LICENSE file for details.

## Acknowledgments

Based on the "Ralph Wiggum" technique by Geoffrey Huntley.
