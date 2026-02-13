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
        // CopilotSdk uses an in-process SDK, not a CLI process
        if (providerConfig.Provider == AIProvider.CopilotSdk)
        {
            return await RunCopilotSdkAsync(providerConfig, prompt, workingDir, onOutput, cancellationToken);
        }

        string? tempPromptFile = null;
        string? tempScriptFile = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDir
            };

            // Determine how to pass the prompt based on provider config
            if (providerConfig.UsesPromptArgument)
            {
                // Write prompt to temp file and use a shell script to avoid
                // argument length limits and quoting issues.
                // Also closes stdin via exec < /dev/null to prevent hangs.
                tempPromptFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(tempPromptFile, prompt, cancellationToken);

                if (OperatingSystem.IsWindows())
                {
                    tempScriptFile = Path.GetTempFileName() + ".bat";
                    var scriptContent = $"@echo off\ntype \"{tempPromptFile}\" | {providerConfig.ExecutablePath} {providerConfig.Arguments}";
                    await File.WriteAllTextAsync(tempScriptFile, scriptContent, cancellationToken);
                    psi.FileName = "cmd.exe";
                    psi.Arguments = $"/c \"{tempScriptFile}\"";
                }
                else
                {
                    tempScriptFile = Path.GetTempFileName() + ".sh";
                    var scriptContent = $"#!/bin/bash\nexec < /dev/null\n{providerConfig.ExecutablePath} {providerConfig.Arguments} \"$(cat '{tempPromptFile}')\"";
                    await File.WriteAllTextAsync(tempScriptFile, scriptContent, cancellationToken);
                    // Make executable
                    Process.Start("chmod", $"+x \"{tempScriptFile}\"")?.WaitForExit();
                    psi.FileName = "/bin/bash";
                    psi.Arguments = tempScriptFile;
                }

                psi.RedirectStandardInput = false;
            }
            else
            {
                // Stdin-based: write prompt to stdin then close it
                psi.FileName = providerConfig.ExecutablePath;
                psi.Arguments = providerConfig.Arguments;
                psi.RedirectStandardInput = true;
            }

            // Provider-specific environment variables
            if (providerConfig.Provider == AIProvider.OpenCode)
            {
                psi.Environment["OPENCODE_DISABLE_AUTOUPDATE"] = "true";
                psi.Environment["OPENCODE_PERMISSION"] = "{\"permission\":\"allow\"}";
            }

            using var process = Process.Start(psi);
            if (process == null)
            {
                return new AgentProcessResult { Success = false, Error = "Failed to start AI process", Duration = stopwatch.Elapsed };
            }

            // Write prompt to stdin if applicable
            if (psi.RedirectStandardInput)
            {
                await process.StandardInput.WriteAsync(prompt);
                process.StandardInput.Close();
            }

            // Read output — stream stdout line-by-line to parse stream-json /
            // OpenCode JSON and emit only meaningful text.
            var outputBuilder = new StringBuilder();
            var textBuilder = new StringBuilder();
            var lastProgressAt = DateTime.UtcNow;
            var usesStreamJson = providerConfig.UsesStreamJson;
            var isOpenCode = providerConfig.Provider == AIProvider.OpenCode;
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
                else if (isOpenCode)
                {
                    var text = ParseOpenCodeJsonLine(line);
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
                Error = error ?? "",
                OutputChars = outputChars,
                ErrorChars = error?.Length ?? 0,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            return new AgentProcessResult { Success = false, Error = ex.Message, Duration = stopwatch.Elapsed };
        }
        finally
        {
            // Clean up temp files
            if (tempPromptFile != null && File.Exists(tempPromptFile))
                try { File.Delete(tempPromptFile); } catch { }
            if (tempScriptFile != null && File.Exists(tempScriptFile))
                try { File.Delete(tempScriptFile); } catch { }
        }
    }

    /// <summary>
    /// Run a prompt via the Copilot SDK (in-process, not CLI).
    /// Maps CopilotSdkResult → AgentProcessResult so callers don't need to know the difference.
    /// </summary>
    private static async Task<AgentProcessResult> RunCopilotSdkAsync(
        AIProviderConfig providerConfig,
        string prompt,
        string workingDir,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var model = providerConfig.Arguments; // Model name stored in Arguments
        var token = string.IsNullOrEmpty(providerConfig.ExecutablePath) ? null : providerConfig.ExecutablePath;
        var outputChars = 0L;

        try
        {
            using var client = new CopilotSdkClient(workingDir, model, token);

            client.OnOutput += text =>
            {
                outputChars += text.Length;
                onOutput?.Invoke(text);
            };
            client.OnToolCall += (name, _) => onOutput?.Invoke($"[Tool: {name}]");
            client.OnError += err => onOutput?.Invoke($"[ERROR] {err}");

            var result = await client.RunAsync(prompt, cancellationToken);

            return new AgentProcessResult
            {
                Success = result.Success,
                Output = result.Output,
                ParsedText = result.Output,
                Error = result.Error,
                OutputChars = outputChars,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            return new AgentProcessResult
            {
                Success = false,
                Error = ex.Message,
                Duration = stopwatch.Elapsed
            };
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
    /// Parse a line of OpenCode JSON output to extract text content.
    /// Handles types: text, text_delta, content_block_delta, error.
    /// </summary>
    public static string? ParseOpenCodeJsonLine(string line)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("{"))
                return null;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeEl))
            {
                var type = typeEl.GetString();
                if (type is "text" or "text_delta" or "content_block_delta")
                {
                    if (root.TryGetProperty("text", out var textEl))
                        return textEl.GetString();
                    if (root.TryGetProperty("part", out var partEl) && partEl.TryGetProperty("text", out textEl))
                        return textEl.GetString();
                    if (root.TryGetProperty("content", out textEl))
                        return textEl.GetString();
                    if (root.TryGetProperty("delta", out var deltaEl) && deltaEl.TryGetProperty("text", out textEl))
                        return textEl.GetString();
                }
                else if (type == "error")
                {
                    string? errorMsg = null;
                    if (root.TryGetProperty("error", out var errorEl))
                    {
                        if (errorEl.TryGetProperty("message", out var msgEl))
                            errorMsg = msgEl.GetString();
                        else if (errorEl.TryGetProperty("data", out var dataEl) &&
                                 dataEl.TryGetProperty("message", out var dataMsgEl))
                            errorMsg = dataMsgEl.GetString();
                    }
                    else if (root.TryGetProperty("part", out var partEl) &&
                             partEl.TryGetProperty("error", out errorEl) &&
                             errorEl.TryGetProperty("message", out var msgEl2))
                    {
                        errorMsg = msgEl2.GetString();
                    }
                    if (errorMsg != null)
                        return $"Error: {errorMsg}";
                }
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
    public TimeSpan Duration { get; set; }
}
