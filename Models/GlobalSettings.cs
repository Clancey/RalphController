using System.Text.Json;
using System.Text.Json.Serialization;

namespace RalphController.Models;

/// <summary>
/// Global settings persisted in ~/.ralph/settings.json
/// Used for caching values across projects (like last used URLs)
/// </summary>
public class GlobalSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ralph");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>Last used Ollama/LMStudio API URL</summary>
    public string? LastOllamaUrl { get; set; }

    /// <summary>Last used Ollama/LMStudio model</summary>
    public string? LastOllamaModel { get; set; }

    /// <summary>Last used OpenCode model</summary>
    public string? LastOpenCodeModel { get; set; }

    /// <summary>When these settings were last updated</summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// Load global settings
    /// </summary>
    public static GlobalSettings Load()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return new GlobalSettings();
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<GlobalSettings>(json, JsonOptions) ?? new GlobalSettings();
        }
        catch
        {
            return new GlobalSettings();
        }
    }

    /// <summary>
    /// Save global settings
    /// </summary>
    public void Save()
    {
        try
        {
            // Ensure directory exists
            if (!Directory.Exists(SettingsDirectory))
            {
                Directory.CreateDirectory(SettingsDirectory);
            }

            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Silently fail - settings are optional
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
