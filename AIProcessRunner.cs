using RalphController.Models;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RalphController;

/// <summary>
/// Shared AI CLI process runner used by both TeamAgent (parallel mode)
/// and SubAgent/LeadAgent (lead-driven mode).
/// Extracted from TeamAgent to avoid duplicating ~130 lines of process management.
/// </summary>
public static class AIProcessRunner
{
    /// <summary>
    /// Run an AI CLI process with the given prompt and return the result.
    /// Handles stdin/stdout redirection, stream-json parsing, and progress heartbeats.
    /// </summary>
    public static async Task<AgentProcessResult> RunAsync(
        AIProviderConfig providerConfig,
        string prompt,
        string workingDir,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = providerConfig.ExecutablePath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDir
            };

            // Build arguments - append prompt if UsesPromptArgument
            if (providerConfig.UsesPromptArgument)
            {
                var escapedPrompt = prompt.Replace("\"", "\\\"");
                psi.Arguments = $"{providerConfig.Arguments} \"{escapedPrompt}\"";
            }
            else
            {
                psi.Arguments = providerConfig.Arguments;
            }

            using var process = Process.Start(psi);
            if (process == null)
            {
                return new AgentProcessResult { Success = false, Error = "Failed to start AI process" };
            }

            // Write prompt to stdin if applicable
            if (providerConfig.UsesStdin)
            {
                await process.StandardInput.WriteAsync(prompt);
                process.StandardInput.Close();
            }
            else if (!providerConfig.UsesPromptArgument)
            {
                await process.StandardInput.WriteAsync(prompt);
                process.StandardInput.Close();
            }

            // Read output — stream stdout line-by-line to parse stream-json and
            // emit only meaningful text, avoiding raw JSON flooding the TUI.
            var outputBuilder = new StringBuilder();
            var textBuilder = new StringBuilder();
            var lastProgressAt = DateTime.UtcNow;
            var usesStreamJson = providerConfig.UsesStreamJson;
            long outputChars = 0;

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            while (!process.StandardOutput.EndOfStream)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line == null) break;

                outputBuilder.AppendLine(line);
                outputChars += line.Length;

                if (usesStreamJson)
                {
                    var text = ParseStreamJsonLine(line);
                    if (text != null)
                    {
                        textBuilder.Append(text);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    textBuilder.AppendLine(line);
                }

                // Emit a progress heartbeat every 30 seconds
                if ((DateTime.UtcNow - lastProgressAt).TotalSeconds >= 30)
                {
                    lastProgressAt = DateTime.UtcNow;
                    var charsSoFar = textBuilder.Length;
                    onOutput?.Invoke($"Working... ({charsSoFar:N0} chars of output so far)");
                }
            }

            await process.WaitForExitAsync(cancellationToken);

            var output = outputBuilder.ToString();
            var error = await stderrTask;

            // Emit a summary of the parsed text output (last few meaningful lines)
            var parsedText = textBuilder.ToString().Trim();
            if (!string.IsNullOrEmpty(parsedText))
            {
                var lastLines = parsedText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lastLines.Length > 0)
                {
                    var lastLine = lastLines[^1].Trim();
                    if (lastLine.Length > 200) lastLine = lastLine[..200] + "...";
                    onOutput?.Invoke(lastLine);
                }
            }

            return new AgentProcessResult
            {
                Success = process.ExitCode == 0,
                Output = output,
                ParsedText = parsedText,
                Error = error,
                OutputChars = outputChars,
                ErrorChars = error?.Length ?? 0
            };
        }
        catch (Exception ex)
        {
            return new AgentProcessResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Parse a line of stream-json output to extract text content.
    /// Returns the text delta or null if the line isn't a text event.
    /// </summary>
    public static string? ParseStreamJsonLine(string line)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("{"))
                return null;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Claude stream-json: {"type":"stream_event","event":{"type":"content_block_delta","delta":{"text":"..."}}}
            if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "stream_event")
            {
                if (root.TryGetProperty("event", out var eventEl))
                {
                    if (eventEl.TryGetProperty("type", out var eventTypeEl) &&
                        eventTypeEl.GetString() == "content_block_delta")
                    {
                        if (eventEl.TryGetProperty("delta", out var deltaEl) &&
                            deltaEl.TryGetProperty("text", out var textEl))
                        {
                            return textEl.GetString();
                        }
                    }
                }
            }

            // Gemini stream-json: {"text":"..."}
            if (root.TryGetProperty("text", out var directTextEl))
            {
                return directTextEl.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Strip ---RALPH_STATUS---...---END_STATUS--- blocks and EXIT_SIGNAL lines from prompt content.
    /// </summary>
    public static string StripRalphStatusBlock(string content)
    {
        content = Regex.Replace(
            content,
            @"---RALPH_STATUS---.*?---END_STATUS---",
            "",
            RegexOptions.Singleline);

        content = Regex.Replace(
            content,
            @"^.*EXIT_SIGNAL.*$",
            "",
            RegexOptions.Multiline);

        content = Regex.Replace(content, @"\n{3,}", "\n\n");

        return content.Trim();
    }

    /// <summary>
    /// Try to read a file, returning its content or null if not found.
    /// Falls back to the canonical config path if the primary path doesn't exist.
    /// </summary>
    public static string? TryReadFile(string path, string? fallbackPath = null)
    {
        if (File.Exists(path))
            return File.ReadAllText(path);

        if (fallbackPath != null && path != fallbackPath && File.Exists(fallbackPath))
            return File.ReadAllText(fallbackPath);

        return null;
    }

    /// <summary>
    /// Resolve the prompt file path, accounting for RalphFolder and worktrees.
    /// </summary>
    public static string ResolvePromptPath(RalphConfig config, bool useWorktrees, string worktreePath)
    {
        if (!string.IsNullOrEmpty(config.RalphFolder))
            return config.PromptFilePath;

        return useWorktrees
            ? Path.Combine(worktreePath, config.PromptFile)
            : config.PromptFilePath;
    }

    /// <summary>
    /// Resolve the implementation plan file path, accounting for RalphFolder and worktrees.
    /// </summary>
    public static string ResolvePlanPath(RalphConfig config, bool useWorktrees, string worktreePath)
    {
        if (!string.IsNullOrEmpty(config.RalphFolder))
            return config.PlanFilePath;

        return useWorktrees
            ? Path.Combine(worktreePath, config.PlanFile)
            : config.PlanFilePath;
    }
}

/// <summary>
/// Result from AI process execution.
/// </summary>
public class AgentProcessResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = "";
    public string ParsedText { get; set; } = "";
    public string Error { get; set; } = "";
    public List<string>? FilesModified { get; set; }
    public long OutputChars { get; set; }
    public long ErrorChars { get; set; }
}
