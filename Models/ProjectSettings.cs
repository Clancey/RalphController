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

    /// <summary>Custom executable path for the provider (optional)</summary>
    public string? ExecutablePath { get; set; }

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
            return JsonSerializer.Deserialize<ProjectSettings>(json, JsonOptions) ?? new ProjectSettings();
        }
        catch
        {
            return new ProjectSettings();
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
