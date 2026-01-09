using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RalphController.Models;

namespace RalphController;

/// <summary>
/// Direct Ollama API client with native tool calling and streaming support
/// </summary>
public class OllamaClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _workingDirectory;
    private readonly List<ChatMessage> _conversationHistory = new();
    private bool _disposed;
    private bool _stopRequested;

    /// <summary>Fired when text output is received (streaming)</summary>
    public event Action<string>? OnOutput;

    /// <summary>Fired when a tool is being called</summary>
    public event Action<string, string>? OnToolCall;

    /// <summary>Fired when a tool returns a result</summary>
    public event Action<string, string>? OnToolResult;

    /// <summary>Fired when an error occurs</summary>
    public event Action<string>? OnError;

    /// <summary>Fired when an iteration completes</summary>
    public event Action<int>? OnIterationComplete;

    public OllamaClient(string baseUrl, string model, string workingDirectory)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _workingDirectory = workingDirectory;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    public void Stop() => _stopRequested = true;

    /// <summary>
    /// Run a complete agent loop with the given prompt, streaming output
    /// </summary>
    public async Task<OllamaResult> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        _stopRequested = false;

        try
        {
            // Initialize conversation with system prompt and user message
            _conversationHistory.Clear();
            _conversationHistory.Add(new ChatMessage
            {
                Role = "system",
                Content = GetSystemPrompt()
            });
            _conversationHistory.Add(new ChatMessage
            {
                Role = "user",
                Content = prompt
            });

            var maxIterations = 50;  // Safety limit
            var iteration = 0;

            while (iteration < maxIterations && !cancellationToken.IsCancellationRequested && !_stopRequested)
            {
                iteration++;

                var (textContent, toolCalls, finishReason) = await SendStreamingRequestAsync(
                    text =>
                    {
                        outputBuilder.Append(text);
                        OnOutput?.Invoke(text);
                    },
                    cancellationToken);

                if (_stopRequested) break;

                // Build assistant message for history
                var assistantMessage = new ChatMessage
                {
                    Role = "assistant",
                    Content = string.IsNullOrEmpty(textContent) ? null : textContent,
                    ToolCalls = toolCalls.Count > 0 ? toolCalls : null
                };
                _conversationHistory.Add(assistantMessage);

                // Check if there are tool calls to execute (native or parsed from text)
                if (toolCalls.Count == 0 && !string.IsNullOrEmpty(textContent))
                {
                    // Try to parse tool calls from text (for models that don't use native tool calling)
                    toolCalls = ParseToolCallsFromText(textContent);
                }

                if (toolCalls.Count > 0)
                {
                    foreach (var toolCall in toolCalls)
                    {
                        var toolName = toolCall.Function?.Name ?? "unknown";
                        var toolArgs = toolCall.Function?.Arguments ?? "{}";

                        OnToolCall?.Invoke(toolName, toolArgs);
                        OnOutput?.Invoke($"\n[Tool: {toolName}]\n");

                        // Execute the tool
                        var toolResult = await ExecuteToolAsync(toolName, toolArgs, cancellationToken);

                        // Truncate long results for display
                        var displayResult = toolResult.Length > 1000
                            ? toolResult.Substring(0, 1000) + "\n... (truncated)"
                            : toolResult;
                        OnToolResult?.Invoke(toolName, displayResult);
                        OnOutput?.Invoke($"[Result: {displayResult}]\n\n");

                        // Add tool result to conversation
                        _conversationHistory.Add(new ChatMessage
                        {
                            Role = "tool",
                            Content = toolResult,
                            ToolCallId = toolCall.Id
                        });
                    }

                    OnIterationComplete?.Invoke(iteration);
                    continue;
                }

                // No tool calls - check if we're done
                if (finishReason == "stop" || finishReason == "end_turn" || !string.IsNullOrEmpty(textContent))
                {
                    OnIterationComplete?.Invoke(iteration);
                    break;
                }
            }

            if (iteration >= maxIterations)
            {
                errorBuilder.AppendLine($"Reached maximum iterations ({maxIterations})");
            }

            return new OllamaResult
            {
                Success = errorBuilder.Length == 0,
                Output = outputBuilder.ToString(),
                Error = errorBuilder.ToString(),
                Iterations = iteration
            };
        }
        catch (OperationCanceledException)
        {
            return new OllamaResult
            {
                Success = false,
                Output = outputBuilder.ToString(),
                Error = "Operation cancelled",
                Iterations = 0
            };
        }
        catch (Exception ex)
        {
            errorBuilder.AppendLine($"Exception: {ex.Message}");
            OnError?.Invoke(ex.Message);

            return new OllamaResult
            {
                Success = false,
                Output = outputBuilder.ToString(),
                Error = errorBuilder.ToString(),
                Iterations = 0
            };
        }
    }

    private async Task<(string textContent, List<ToolCall> toolCalls, string? finishReason)> SendStreamingRequestAsync(
        Action<string> onText,
        CancellationToken cancellationToken)
    {
        var request = new ChatCompletionRequest
        {
            Model = _model,
            Messages = _conversationHistory,
            Tools = GetToolDefinitions(),
            Stream = true
        };

        var url = $"{_baseUrl}/v1/chat/completions";
        var json = JsonSerializer.Serialize(request, JsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            OnError?.Invoke($"HTTP {response.StatusCode}: {errorContent}");
            return ("", new List<ToolCall>(), "error");
        }

        var textBuilder = new StringBuilder();
        var toolCalls = new Dictionary<int, ToolCall>();
        string? finishReason = null;

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested && !_stopRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(line)) continue;

            // SSE format: "data: {...}" or "data: [DONE]"
            if (!line.StartsWith("data: ")) continue;
            var data = line.Substring(6);

            if (data == "[DONE]") break;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];

                    // Check finish reason
                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind != JsonValueKind.Null)
                    {
                        finishReason = fr.GetString();
                    }

                    // Get delta content
                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        // Text content
                        if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        {
                            var text = content.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                textBuilder.Append(text);
                                onText(text);
                            }
                        }

                        // Tool calls (accumulated across chunks)
                        if (delta.TryGetProperty("tool_calls", out var tcArray))
                        {
                            foreach (var tc in tcArray.EnumerateArray())
                            {
                                var index = tc.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;

                                if (!toolCalls.ContainsKey(index))
                                {
                                    toolCalls[index] = new ToolCall
                                    {
                                        Id = tc.TryGetProperty("id", out var id) ? id.GetString() ?? $"call_{index}" : $"call_{index}",
                                        Type = "function",
                                        Function = new FunctionCall { Name = "", Arguments = "" }
                                    };
                                }

                                if (tc.TryGetProperty("function", out var fn))
                                {
                                    if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                                    {
                                        toolCalls[index].Function!.Name += name.GetString();
                                    }
                                    if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                                    {
                                        toolCalls[index].Function!.Arguments += args.GetString();
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore malformed JSON chunks
            }
        }

        return (textBuilder.ToString(), toolCalls.Values.ToList(), finishReason);
    }

    /// <summary>
    /// Parse tool calls from text for models that output XML-style tool calls instead of native tool_calls
    /// Supports formats like: <function=name><parameter=key>value</parameter></function>
    /// </summary>
    private static List<ToolCall> ParseToolCallsFromText(string text)
    {
        var toolCalls = new List<ToolCall>();

        // Pattern 1: <function=name><parameter=key>value</parameter></function>
        var functionPattern = new System.Text.RegularExpressions.Regex(
            @"<function=(\w+)>(.*?)</function>",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        var matches = functionPattern.Matches(text);
        var callIndex = 0;

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var funcName = match.Groups[1].Value;
            var paramContent = match.Groups[2].Value;

            // Parse parameters
            var paramPattern = new System.Text.RegularExpressions.Regex(
                @"<parameter=(\w+)>\s*(.*?)\s*</parameter>",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            var args = new Dictionary<string, string>();
            foreach (System.Text.RegularExpressions.Match paramMatch in paramPattern.Matches(paramContent))
            {
                var paramName = paramMatch.Groups[1].Value;
                var paramValue = paramMatch.Groups[2].Value.Trim();
                args[paramName] = paramValue;
            }

            if (args.Count > 0)
            {
                var argsJson = JsonSerializer.Serialize(args);
                toolCalls.Add(new ToolCall
                {
                    Id = $"text_call_{callIndex++}",
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = funcName,
                        Arguments = argsJson
                    }
                });
            }
        }

        return toolCalls;
    }

    private async Task<string> ExecuteToolAsync(string toolName, string argsJson, CancellationToken cancellationToken)
    {
        try
        {
            using var argsDoc = JsonDocument.Parse(argsJson);
            var args = argsDoc.RootElement;

            return toolName switch
            {
                "read_file" => await ExecuteReadFileAsync(args),
                "write_file" => await ExecuteWriteFileAsync(args),
                "edit_file" => await ExecuteEditFileAsync(args),
                "bash" => await ExecuteBashAsync(args, cancellationToken),
                "glob" => await ExecuteGlobAsync(args),
                "grep" => await ExecuteGrepAsync(args, cancellationToken),
                "list_directory" => await ExecuteListDirectoryAsync(args),
                _ => $"Unknown tool: {toolName}"
            };
        }
        catch (Exception ex)
        {
            return $"Error executing {toolName}: {ex.Message}";
        }
    }

    private async Task<string> ExecuteReadFileAsync(JsonElement args)
    {
        var filePath = args.GetProperty("file_path").GetString() ?? "";
        var fullPath = GetFullPath(filePath);

        if (!File.Exists(fullPath))
        {
            return $"Error: File not found: {fullPath}";
        }

        var content = await File.ReadAllTextAsync(fullPath);

        // Add line numbers
        var lines = content.Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            sb.AppendLine($"{i + 1,5}| {lines[i]}");
        }

        return sb.ToString();
    }

    private async Task<string> ExecuteWriteFileAsync(JsonElement args)
    {
        var filePath = args.GetProperty("file_path").GetString() ?? "";
        var content = args.GetProperty("content").GetString() ?? "";
        var fullPath = GetFullPath(filePath);

        // Ensure directory exists
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(fullPath, content);
        return $"Successfully wrote {content.Length} characters to {filePath}";
    }

    private async Task<string> ExecuteEditFileAsync(JsonElement args)
    {
        var filePath = args.GetProperty("file_path").GetString() ?? "";
        var oldString = args.GetProperty("old_string").GetString() ?? "";
        var newString = args.GetProperty("new_string").GetString() ?? "";
        var fullPath = GetFullPath(filePath);

        if (!File.Exists(fullPath))
        {
            return $"Error: File not found: {fullPath}";
        }

        var content = await File.ReadAllTextAsync(fullPath);

        if (!content.Contains(oldString))
        {
            return $"Error: old_string not found in file. Make sure to match the exact text including whitespace.";
        }

        var count = content.Split(new[] { oldString }, StringSplitOptions.None).Length - 1;
        if (count > 1)
        {
            return $"Error: old_string appears {count} times in file. Please provide more context to make it unique.";
        }

        var newContent = content.Replace(oldString, newString);
        await File.WriteAllTextAsync(fullPath, newContent);

        return $"Successfully replaced text in {filePath}";
    }

    private async Task<string> ExecuteBashAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var command = args.GetProperty("command").GetString() ?? "";

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return "Error: Command timed out after 2 minutes";
        }

        var output = await outputTask;
        var error = await errorTask;

        var result = new StringBuilder();
        if (!string.IsNullOrEmpty(output))
        {
            result.AppendLine("STDOUT:");
            result.AppendLine(output);
        }
        if (!string.IsNullOrEmpty(error))
        {
            result.AppendLine("STDERR:");
            result.AppendLine(error);
        }
        result.AppendLine($"Exit code: {process.ExitCode}");

        return result.ToString();
    }

    private Task<string> ExecuteGlobAsync(JsonElement args)
    {
        var pattern = args.GetProperty("pattern").GetString() ?? "*";
        var path = args.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : _workingDirectory;
        path ??= _workingDirectory;

        var fullPath = GetFullPath(path);

        try
        {
            var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher();
            matcher.AddInclude(pattern);

            var result = matcher.Execute(new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(new DirectoryInfo(fullPath)));

            var files = result.Files.Select(f => f.Path).Take(100).ToList();

            if (files.Count == 0)
            {
                return Task.FromResult($"No files found matching pattern '{pattern}' in {path}");
            }

            return Task.FromResult(string.Join("\n", files));
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
    }

    private async Task<string> ExecuteGrepAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var pattern = args.GetProperty("pattern").GetString() ?? "";
        var path = args.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : _workingDirectory;
        path ??= _workingDirectory;

        var fullPath = GetFullPath(path);

        // Use ripgrep if available, otherwise grep
        var rgPath = "/opt/homebrew/bin/rg";
        var useRg = File.Exists(rgPath);

        var psi = new ProcessStartInfo
        {
            FileName = useRg ? rgPath : "/usr/bin/grep",
            Arguments = useRg
                ? $"-n --color=never \"{pattern.Replace("\"", "\\\"")}\" \"{fullPath}\""
                : $"-rn \"{pattern.Replace("\"", "\\\"")}\" \"{fullPath}\"",
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return "Error: Search timed out";
        }

        if (string.IsNullOrEmpty(output))
        {
            return $"No matches found for pattern '{pattern}'";
        }

        // Limit output
        var lines = output.Split('\n').Take(50).ToList();
        if (lines.Count == 50)
        {
            lines.Add("... (truncated, more results available)");
        }

        return string.Join("\n", lines);
    }

    private Task<string> ExecuteListDirectoryAsync(JsonElement args)
    {
        var path = args.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : _workingDirectory;
        path ??= _workingDirectory;

        var fullPath = GetFullPath(path);

        if (!Directory.Exists(fullPath))
        {
            return Task.FromResult($"Error: Directory not found: {fullPath}");
        }

        var sb = new StringBuilder();

        try
        {
            var dirs = Directory.GetDirectories(fullPath).Select(d => "[DIR]  " + Path.GetFileName(d));
            var files = Directory.GetFiles(fullPath).Select(f => "[FILE] " + Path.GetFileName(f));

            foreach (var item in dirs.Concat(files).OrderBy(x => x))
            {
                sb.AppendLine(item);
            }

            return Task.FromResult(sb.Length > 0 ? sb.ToString() : "(empty directory)");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
    }

    private string GetFullPath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }
        return Path.GetFullPath(Path.Combine(_workingDirectory, path));
    }

    private static string GetSystemPrompt() => @"You are an expert software engineer working autonomously to complete tasks. You have access to tools that let you read files, write files, edit files, run bash commands, and search the codebase.

IMPORTANT GUIDELINES:
1. Always read files before editing them to understand the current state
2. Use the edit_file tool for precise changes - it requires an exact match of old_string
3. Use bash for running tests, builds, and git commands
4. Use glob to find files by pattern
5. Use grep to search for text in files
6. Be thorough but focused - complete one task at a time
7. After making changes, verify they work by running tests or builds
8. Commit successful changes with descriptive messages

When you have completed the task or have no more work to do, simply respond with your final summary without calling any tools.";

    private static List<ToolDefinition> GetToolDefinitions() => new()
    {
        new ToolDefinition
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = "read_file",
                Description = "Read the contents of a file. Returns the file content with line numbers.",
                Parameters = new ParametersDefinition
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertyDefinition>
                    {
                        ["file_path"] = new() { Type = "string", Description = "The path to the file to read (absolute or relative to working directory)" }
                    },
                    Required = new[] { "file_path" }
                }
            }
        },
        new ToolDefinition
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = "write_file",
                Description = "Write content to a file, creating it if it doesn't exist or overwriting if it does.",
                Parameters = new ParametersDefinition
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertyDefinition>
                    {
                        ["file_path"] = new() { Type = "string", Description = "The path to the file to write" },
                        ["content"] = new() { Type = "string", Description = "The content to write to the file" }
                    },
                    Required = new[] { "file_path", "content" }
                }
            }
        },
        new ToolDefinition
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = "edit_file",
                Description = "Edit a file by replacing an exact string with new content. The old_string must match exactly (including whitespace).",
                Parameters = new ParametersDefinition
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertyDefinition>
                    {
                        ["file_path"] = new() { Type = "string", Description = "The path to the file to edit" },
                        ["old_string"] = new() { Type = "string", Description = "The exact string to find and replace" },
                        ["new_string"] = new() { Type = "string", Description = "The string to replace it with" }
                    },
                    Required = new[] { "file_path", "old_string", "new_string" }
                }
            }
        },
        new ToolDefinition
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = "bash",
                Description = "Execute a bash command and return the output. Use for running tests, builds, git commands, etc.",
                Parameters = new ParametersDefinition
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertyDefinition>
                    {
                        ["command"] = new() { Type = "string", Description = "The bash command to execute" }
                    },
                    Required = new[] { "command" }
                }
            }
        },
        new ToolDefinition
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = "glob",
                Description = "Find files matching a glob pattern (e.g., '**/*.cs', 'src/**/*.ts')",
                Parameters = new ParametersDefinition
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertyDefinition>
                    {
                        ["pattern"] = new() { Type = "string", Description = "The glob pattern to match files" },
                        ["path"] = new() { Type = "string", Description = "The directory to search in (defaults to working directory)" }
                    },
                    Required = new[] { "pattern" }
                }
            }
        },
        new ToolDefinition
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = "grep",
                Description = "Search for a pattern in files using regex. Returns matching lines with file paths and line numbers.",
                Parameters = new ParametersDefinition
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertyDefinition>
                    {
                        ["pattern"] = new() { Type = "string", Description = "The regex pattern to search for" },
                        ["path"] = new() { Type = "string", Description = "The file or directory to search in (defaults to working directory)" }
                    },
                    Required = new[] { "pattern" }
                }
            }
        },
        new ToolDefinition
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = "list_directory",
                Description = "List files and directories in a path",
                Parameters = new ParametersDefinition
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertyDefinition>
                    {
                        ["path"] = new() { Type = "string", Description = "The directory to list (defaults to working directory)" }
                    },
                    Required = Array.Empty<string>()
                }
            }
        }
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}

#region API Models

public class ChatCompletionRequest
{
    public string Model { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = new();
    public List<ToolDefinition>? Tools { get; set; }
    public bool Stream { get; set; }
}

public class ChatMessage
{
    public string Role { get; set; } = "";
    public string? Content { get; set; }
    [JsonPropertyName("tool_calls")]
    public List<ToolCall>? ToolCalls { get; set; }
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }
}

public class ToolCall
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "function";
    public FunctionCall? Function { get; set; }
}

public class FunctionCall
{
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "{}";
}

public class ToolDefinition
{
    public string Type { get; set; } = "function";
    public FunctionDefinition? Function { get; set; }
}

public class FunctionDefinition
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ParametersDefinition? Parameters { get; set; }
}

public class ParametersDefinition
{
    public string Type { get; set; } = "object";
    public Dictionary<string, PropertyDefinition> Properties { get; set; } = new();
    public string[]? Required { get; set; }
}

public class PropertyDefinition
{
    public string Type { get; set; } = "string";
    public string? Description { get; set; }
}

public class ChatCompletionResponse
{
    public string? Id { get; set; }
    public List<Choice>? Choices { get; set; }
}

public class Choice
{
    public int Index { get; set; }
    public ChatMessage? Message { get; set; }
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public record OllamaResult
{
    public required bool Success { get; init; }
    public required string Output { get; init; }
    public required string Error { get; init; }
    public required int Iterations { get; init; }
}

#endregion
