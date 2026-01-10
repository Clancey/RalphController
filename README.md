# RalphController

A .NET console application that implements the "Ralph Wiggum" autonomous AI coding agent loop pattern. This tool monitors and controls Claude CLI (or OpenAI Codex) running in a continuous loop to autonomously implement features, fix bugs, and manage codebases.

Point it at an empty directory with a project description, and watch it build your entire application from scratch. Or use it on an existing codebase to autonomously fix bugs and add features.

## Overview

RalphController automates the Ralph Wiggum technique:

1. **Infinite Loop Execution**: Runs AI CLI in a continuous loop, one task per iteration
2. **Prompt-Driven Development**: Uses `prompt.md` to guide the AI's behavior each iteration
3. **Self-Tracking Progress**: AI updates `implementation_plan.md` to track completed work
4. **Backpressure via Testing**: AI must run tests after changes; failures guide next iteration
5. **Self-Improvement**: AI documents learnings in `agents.md` for future context

## Features

- **Rich TUI**: Spectre.Console-based interface with real-time streaming output, status, and controls
- **Live Streaming**: See AI output as it's generated, not just after completion
- **Project Scaffolding**: Generate all project files from a description or spec file
- **Re-initialization**: Use `--init` to regenerate project files with new requirements
- **Multi-Provider**: Supports Claude, Codex, GitHub Copilot, and OpenCode
- **Provider Persistence**: Remembers your provider choice per project in `.ralph.json`
- **Global Tool**: Install as `ralph` command, run from any directory
- **Pause/Resume/Stop**: Full control over the loop execution
- **Hot-Reload**: Automatically detects changes to `prompt.md`
- **Manual Injection**: Inject custom prompts mid-loop
- **Circuit Breaker**: Detects stagnation (3+ loops without progress) and stops
- **Response Analyzer**: Detects completion signals and auto-exits when done
- **Rate Limiting**: Configurable API calls/hour (default: 100)
- **RALPH_STATUS**: Structured status reporting for progress tracking
- **Priority Levels**: High/Medium/Low task prioritization

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
  - OpenCode CLI (`opencode`) - [OpenCode](https://opencode.ai/docs/cli/)
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
ralph --opencode            # OpenCode

# Or use --provider flag
ralph --provider copilot
ralph --provider opencode

# Specify a model (Copilot or OpenCode)
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

> **⚠️ Important: Code-focused models (like qwen-coder, deepseek-coder, codellama) often fail at scaffolding because they don't follow meta-instructions well. They tend to echo the spec content instead of generating proper Ralph files.**
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
```

## Streaming Output

RalphController streams AI output in real-time:

- **Claude**: Uses `--output-format stream-json` to parse streaming events
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
