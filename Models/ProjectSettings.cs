using System.Text.Json;
using System.Text.Json.Serialization;

namespace RalphController.Models;

/// <summary>
/// Project-specific settings persisted in .ralph.json
/// </summary>
public class ProjectSettings
{
    private const string SettingsFileName = ".ralph.json";

    /// <summary>Last used AI provider for this project</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AIProvider? Provider { get; set; }

    /// <summary>Model to use for Claude (e.g., claude-sonnet-4, claude-opus-4)</summary>
    public string? ClaudeModel { get; set; }

    /// <summary>Model to use for Codex (e.g., codex-1, o3)</summary>
    public string? CodexModel { get; set; }

    /// <summary>Model to use for Copilot (e.g., gpt-5, claude-sonnet-4)</summary>
    public string? CopilotModel { get; set; }

    /// <summary>Model to use for Gemini (e.g., gemini-2.5-pro, gemini-2.5-flash)</summary>
    public string? GeminiModel { get; set; }

    /// <summary>Model to use for Cursor (e.g., claude-sonnet, gpt-4)</summary>
    public string? CursorModel { get; set; }

    /// <summary>Model to use for OpenCode (e.g., anthropic/claude-3-5-sonnet)</summary>
    public string? OpenCodeModel { get; set; }

    /// <summary>Model to use for Copilot SDK (e.g., gpt-5, claude-sonnet-4.5)</summary>
    public string? CopilotSdkModel { get; set; }

    /// <summary>Base URL for Ollama/LMStudio API (e.g., http://127.0.0.1:11434)</summary>
    public string? OllamaUrl { get; set; }

    /// <summary>Model to use for Ollama/LMStudio (e.g., llama3.1:8b)</summary>
    public string? OllamaModel { get; set; }

    /// <summary>Custom executable path for the provider (optional)</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Multi-model configuration (rotation, verification)</summary>
    public MultiModelConfig? MultiModel { get; set; }

    /// <summary>When these settings were last updated</summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// Load project settings from the target directory
    /// </summary>
    public static ProjectSettings Load(string targetDirectory)
    {
        var settingsPath = Path.Combine(targetDirectory, SettingsFileName);

        if (!File.Exists(settingsPath))
        {
            return new ProjectSettings();
        }

        try
        {
            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<ProjectSettings>(json, JsonOptions) ?? new ProjectSettings();

            // Fix generic labels like "Model 2", "Model 3" to use actual model names
            settings.FixGenericLabels();

            return settings;
        }
        catch
        {
            return new ProjectSettings();
        }
    }

    /// <summary>
    /// Fix generic labels in multi-model config to use actual model names
    /// </summary>
    private void FixGenericLabels()
    {
        if (MultiModel?.Models == null) return;

        foreach (var model in MultiModel.Models)
        {
            // If label is generic like "Model 2", "Model 3", "Primary", "Verifier" etc., use the actual model name
            if (string.IsNullOrEmpty(model.Label) ||
                model.Label == "Primary" ||
                model.Label == "Verifier" ||
                System.Text.RegularExpressions.Regex.IsMatch(model.Label, @"^Model \d+$"))
            {
                // Use the actual model name, or provider name as fallback
                model.Label = !string.IsNullOrEmpty(model.Model)
                    ? model.Model
                    : model.Provider.ToString();
            }
        }
    }

    /// <summary>
    /// Save project settings to the target directory
    /// </summary>
    public void Save(string targetDirectory)
    {
        var settingsPath = Path.Combine(targetDirectory, SettingsFileName);
        LastUpdated = DateTime.UtcNow;

        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(settingsPath, json);
        }
        catch
        {
            // Silently fail - settings are optional
        }
    }

    /// <summary>
    /// Get the settings file path for a directory
    /// </summary>
    public static string GetSettingsPath(string targetDirectory)
    {
        return Path.Combine(targetDirectory, SettingsFileName);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
