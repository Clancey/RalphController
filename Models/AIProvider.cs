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

    /// <summary>Google Gemini CLI</summary>
    Gemini,

    /// <summary>Cursor AI Editor CLI</summary>
    Cursor,

    /// <summary>OpenCode CLI</summary>
    OpenCode,

    /// <summary>Direct Ollama API integration</summary>
    Ollama
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

    /// <summary>Whether this provider has been configured to support sub-agents</summary>
    public bool SupportsSubAgents { get; init; } = false;

    /// <summary>Check if this provider supports sub-agent coordination</summary>
    public bool HasSubAgentCapability() => SupportsSubAgents || Provider switch
    {
        AIProvider.Claude => true,   // Claude has native tool/sub-agent support
        AIProvider.Cursor => true,   // Cursor uses Claude APIs with sub-agent support
        AIProvider.Ollama => false,  // Depends on model, conservative default
        _ => false
    };

    public static AIProviderConfig ForClaude(string? executablePath = null, string? model = null) => new()
    {
        Provider = AIProvider.Claude,
        ExecutablePath = executablePath ?? "claude",
        // Use stream-json with partial messages for real-time streaming
        // Add --model flag if model specified
        Arguments = string.IsNullOrWhiteSpace(model)
            ? "-p --dangerously-skip-permissions --output-format stream-json --verbose --include-partial-messages"
            : $"-p --dangerously-skip-permissions --output-format stream-json --verbose --include-partial-messages --model {model}",
        UsesStdin = true,
        UsesStreamJson = true
    };

    public static AIProviderConfig ForCodex(string? executablePath = null, string? model = null) => new()
    {
        Provider = AIProvider.Codex,
        ExecutablePath = executablePath ?? "codex",
        // exec for non-interactive mode, - to read prompt from stdin
        // --dangerously-bypass-approvals-and-sandbox for full auto mode
        // Add --model flag if model specified
        Arguments = string.IsNullOrWhiteSpace(model)
            ? "exec --dangerously-bypass-approvals-and-sandbox -"
            : $"exec --dangerously-bypass-approvals-and-sandbox --model {model} -",
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

    public static AIProviderConfig ForGemini(string? executablePath = null, string? model = null) => new()
    {
        Provider = AIProvider.Gemini,
        ExecutablePath = executablePath ?? "gemini",
        // --yolo for auto-approve, -o stream-json for streaming, -m for model
        // Uses positional prompt argument
        Arguments = string.IsNullOrWhiteSpace(model)
            ? "--yolo -o stream-json"
            : $"--yolo -o stream-json -m {model}",
        UsesStdin = false,
        UsesPromptArgument = true,  // Prompt is passed as positional argument
        UsesStreamJson = true
    };

    public static AIProviderConfig ForCursor(string? executablePath = null, string? model = null) => new()
    {
        Provider = AIProvider.Cursor,
        ExecutablePath = executablePath ?? "cursor",
        // Cursor agent mode with auto-approve
        // --model to specify which model
        Arguments = string.IsNullOrWhiteSpace(model)
            ? "agent --auto-approve"
            : $"agent --auto-approve --model {model}",
        UsesStdin = false,
        UsesPromptArgument = true  // Prompt is passed as argument
    };

    public static AIProviderConfig ForOllama(string? baseUrl = null, string? model = null) => new()
    {
        Provider = AIProvider.Ollama,
        ExecutablePath = baseUrl ?? "http://localhost:11434",  // Use ExecutablePath to store base URL
        Arguments = model ?? "llama3.1:8b",  // Use Arguments to store model name
        UsesStdin = false,
        UsesPromptArgument = false
    };
}
