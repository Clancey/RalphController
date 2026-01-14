using System.Text.Json.Serialization;

namespace RalphController.Models;

/// <summary>
/// Model capability tiers - ranked by capability and cost
/// </summary>
public enum ModelTier
{
    /// <summary>Most capable models - for complex planning, synthesis, architecture</summary>
    Expert = 1,

    /// <summary>Balanced capability - for implementation, reviewing, general tasks</summary>
    Capable = 2,

    /// <summary>Fast and economical - for simple checks, validation, quick tasks</summary>
    Fast = 3
}

/// <summary>
/// Configuration for multi-model support (rotation and verification)
/// </summary>
public class MultiModelConfig
{
    /// <summary>List of models to use (in order for rotation)</summary>
    public List<ModelSpec> Models { get; set; } = new();

    /// <summary>Strategy for model switching</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ModelSwitchStrategy Strategy { get; set; } = ModelSwitchStrategy.None;

    /// <summary>Verification-specific settings (when Strategy = Verification)</summary>
    public VerificationConfig? Verification { get; set; }

    /// <summary>For round-robin: switch every N iterations (default: 1)</summary>
    public int RotateEveryN { get; set; } = 1;

    /// <summary>Returns true if multi-model is configured and has at least one model</summary>
    [JsonIgnore]
    public bool IsEnabled => Strategy != ModelSwitchStrategy.None && Models.Count > 0;

    /// <summary>Returns true if this is a valid configuration</summary>
    [JsonIgnore]
    public bool IsValid => Strategy == ModelSwitchStrategy.None || Models.Count >= (Strategy == ModelSwitchStrategy.Verification ? 2 : 1);
}

/// <summary>
/// Specification for a single model
/// </summary>
public class ModelSpec
{
    /// <summary>AI provider for this model</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AIProvider Provider { get; set; }

    /// <summary>Model identifier (e.g., "opus", "sonnet", "llama3.1:8b")</summary>
    public string Model { get; set; } = "";

    /// <summary>Base URL for Ollama/LMStudio (null uses default)</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Display label (e.g., "Opus", "Fast Sonnet")</summary>
    public string? Label { get; set; }

    /// <summary>Custom executable path (overrides provider default)</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Explicit capability tier (null = auto-infer from model name)</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ModelTier? Tier { get; set; }

    /// <summary>Gets display name (label or model)</summary>
    [JsonIgnore]
    public string DisplayName => Label ?? Model;

    /// <summary>Gets the effective tier (explicit or inferred from model name)</summary>
    [JsonIgnore]
    public ModelTier EffectiveTier => Tier ?? InferTierFromModelName();

    /// <summary>
    /// User-configurable tier overrides (loaded from ralph.json)
    /// Maps model name patterns to tiers
    /// </summary>
    public static Dictionary<string, ModelTier> TierOverrides { get; set; } = new();

    /// <summary>
    /// Infers capability tier from model name patterns
    /// Checks user overrides first, then falls back to built-in rules
    /// </summary>
    private ModelTier InferTierFromModelName()
    {
        var name = Model?.ToLowerInvariant() ?? "";

        // Check user overrides first (patterns can be partial matches)
        foreach (var (pattern, tier) in TierOverrides)
        {
            if (name.Contains(pattern.ToLowerInvariant()))
                return tier;
        }

        // Expert tier: Most capable models (expensive, high-quality)
        if (name.Contains("opus") ||
            name.Contains("pro") ||
            name.Contains("70b") ||
            name.Contains("32b") ||
            name.Contains("405b") ||
            name.Contains("glm-4") ||  // GLM-4.x series
            name.Contains("gpt-5") && !name.Contains("mini") && !name.Contains("nano") ||
            name.Contains("o1") && !name.Contains("mini") ||
            name.Contains("o3") ||
            name.Contains("deep-seek-r1") ||
            name.Contains("deepseek-r1") ||
            name.Contains("codex-max") ||
            name.Contains("qwen-max") ||
            name.Contains("claude-sonnet-4"))  // Sonnet 4.x is expert-level
            return ModelTier.Expert;

        // Fast tier: Quick and economical models
        if (name.Contains("haiku") ||
            name.Contains("flash") ||
            name.Contains("mini") ||
            name.Contains("nano") ||
            name.Contains("8b") ||
            name.Contains("7b") ||
            name.Contains("3b") ||
            name.Contains("1b") ||
            name.Contains("fast") ||
            name.Contains("turbo"))
            return ModelTier.Fast;

        // Default to Capable tier (balanced performance/cost)
        return ModelTier.Capable;
    }

    /// <summary>
    /// Creates an AIProviderConfig from this spec
    /// </summary>
    public AIProviderConfig ToProviderConfig()
    {
        return Provider switch
        {
            AIProvider.Claude => CreateClaudeConfig(),
            AIProvider.Codex => AIProviderConfig.ForCodex(ExecutablePath),
            AIProvider.Copilot => AIProviderConfig.ForCopilot(ExecutablePath, Model),
            AIProvider.Gemini => AIProviderConfig.ForGemini(ExecutablePath, Model),
            AIProvider.Cursor => AIProviderConfig.ForCursor(ExecutablePath, Model),
            AIProvider.OpenCode => AIProviderConfig.ForOpenCode(ExecutablePath, NormalizeOpenCodeModel(Model)),
            AIProvider.Ollama => AIProviderConfig.ForOllama(BaseUrl, Model),
            _ => throw new ArgumentOutOfRangeException(nameof(Provider), $"Unknown provider: {Provider}")
        };
    }

    /// <summary>
    /// Normalizes OpenCode model names to include provider prefix
    /// </summary>
    private static string? NormalizeOpenCodeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        // If it already has provider prefix, use as-is
        if (model.Contains('/'))
            return model;

        // Known OpenCode models (without provider prefix)
        var openCodeModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "big-pickle", "glm-4.7-free", "gpt-5-nano", "grok-code", "minimax-m2.1-free"
        };

        if (openCodeModels.Contains(model))
            return $"opencode/{model}";

        // If it has a tag (like :8b, :70b), it's likely an Ollama model
        if (model.Contains(':'))
            return $"ollama/{model}";

        // Default to opencode provider for unrecognized models
        return $"opencode/{model}";
    }

    private AIProviderConfig CreateClaudeConfig()
    {
        // Claude uses --model flag for model selection
        var modelArg = string.IsNullOrWhiteSpace(Model) ? "" : $"--model {Model} ";
        return new AIProviderConfig
        {
            Provider = AIProvider.Claude,
            ExecutablePath = ExecutablePath ?? "claude",
            Arguments = $"-p --dangerously-skip-permissions --output-format stream-json --verbose --include-partial-messages {modelArg}".Trim(),
            UsesStdin = true,
            UsesStreamJson = true
        };
    }

    /// <summary>
    /// Creates a ModelSpec from shorthand notation (e.g., "claude:opus", "ollama:llama3.1:8b")
    /// </summary>
    public static ModelSpec Parse(string shorthand)
    {
        var parts = shorthand.Split(':', 2);
        var providerStr = parts[0].ToLowerInvariant();
        var model = parts.Length > 1 ? parts[1] : "";

        var provider = providerStr switch
        {
            "claude" => AIProvider.Claude,
            "codex" => AIProvider.Codex,
            "copilot" => AIProvider.Copilot,
            "gemini" => AIProvider.Gemini,
            "cursor" => AIProvider.Cursor,
            "opencode" => AIProvider.OpenCode,
            "ollama" => AIProvider.Ollama,
            _ => throw new ArgumentException($"Unknown provider: {providerStr}")
        };

        return new ModelSpec
        {
            Provider = provider,
            Model = model,
            Label = $"{providerStr.ToUpperInvariant()[0]}{providerStr[1..]}:{model}"
        };
    }
}

/// <summary>
/// Strategy for switching between models
/// </summary>
public enum ModelSwitchStrategy
{
    /// <summary>Single model (current behavior)</summary>
    None,

    /// <summary>Cycle through models each iteration</summary>
    RoundRobin,

    /// <summary>Use secondary model to verify completion</summary>
    Verification,

    /// <summary>Use secondary if primary fails or hits rate limit</summary>
    Fallback
}

/// <summary>
/// Configuration for verification mode
/// </summary>
public class VerificationConfig
{
    /// <summary>Index of verifier model in Models list (default: 1 = second model)</summary>
    public int VerifierIndex { get; set; } = 1;

    /// <summary>What triggers verification</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VerificationTrigger Trigger { get; set; } = VerificationTrigger.CompletionSignal;

    /// <summary>For EveryNIterations trigger: run verification every N iterations</summary>
    public int EveryNIterations { get; set; } = 5;

    /// <summary>Maximum verification attempts before forcing exit</summary>
    public int MaxVerificationAttempts { get; set; } = 3;
}

/// <summary>
/// What triggers a verification run
/// </summary>
public enum VerificationTrigger
{
    /// <summary>When ResponseAnalyzer detects completion signal</summary>
    CompletionSignal,

    /// <summary>Run verification every N iterations</summary>
    EveryNIterations,

    /// <summary>User-triggered via hotkey</summary>
    Manual
}
