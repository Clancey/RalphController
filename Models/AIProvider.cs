namespace RalphController.Models;

/// <summary>
/// Supported AI providers
/// </summary>
public enum AIProvider
{
    /// <summary>Anthropic Claude CLI</summary>
    Claude,

    /// <summary>OpenAI Codex CLI</summary>
    Codex,

    /// <summary>GitHub Copilot CLI</summary>
    Copilot,

    /// <summary>OpenCode CLI</summary>
    OpenCode
}

/// <summary>
/// Configuration for an AI provider
/// </summary>
public record AIProviderConfig
{
    public required AIProvider Provider { get; init; }
    public required string ExecutablePath { get; init; }
    public required string Arguments { get; init; }

    /// <summary>Whether this provider reads prompt from stdin (false = pass as argument)</summary>
    public bool UsesStdin { get; init; } = false;

    /// <summary>Argument prefix for prompt (if not using stdin)</summary>
    public string? PromptArgument { get; init; }

    /// <summary>Whether to write prompt to a temp file and use shell redirection</summary>
    public bool UsesTempFile { get; init; } = false;

    /// <summary>Whether to pass the prompt as a direct command line argument (quoted)</summary>
    public bool UsesPromptArgument { get; init; } = false;

    /// <summary>Whether output is in stream-json format that needs parsing</summary>
    public bool UsesStreamJson { get; init; } = false;

    public static AIProviderConfig ForClaude(string? executablePath = null) => new()
    {
        Provider = AIProvider.Claude,
        ExecutablePath = executablePath ?? "claude",
        // Use stream-json with partial messages for real-time streaming
        Arguments = "-p --dangerously-skip-permissions --output-format stream-json --verbose --include-partial-messages",
        UsesStdin = true,
        UsesStreamJson = true
    };

    public static AIProviderConfig ForCodex(string? executablePath = null) => new()
    {
        Provider = AIProvider.Codex,
        ExecutablePath = executablePath ?? "codex",
        // exec for non-interactive mode, - to read prompt from stdin
        // --dangerously-bypass-approvals-and-sandbox for full auto mode
        Arguments = "exec --dangerously-bypass-approvals-and-sandbox -",
        UsesStdin = true  // Codex exec reads from stdin when using "-"
    };

    public static AIProviderConfig ForCopilot(string? executablePath = null, string? model = null) => new()
    {
        Provider = AIProvider.Copilot,
        ExecutablePath = executablePath ?? "copilot",
        // -p for programmatic mode (non-interactive)
        // --allow-all-tools for autonomous execution without approval prompts
        // --model to specify which model (default: gpt-5)
        Arguments = $"--allow-all-tools --model {model ?? "gpt-5"} -p",
        UsesStdin = false,
        UsesPromptArgument = true  // Prompt is passed as quoted argument after -p
    };

    public static AIProviderConfig ForOpenCode(string? executablePath = null, string? model = null) => new()
    {
        Provider = AIProvider.OpenCode,
        ExecutablePath = executablePath ?? "opencode",
        Arguments = string.IsNullOrWhiteSpace(model)
            ? "run --format json"
            : $"run --format json --model {model}",
        UsesStdin = false,
        UsesPromptArgument = true
    };
}
