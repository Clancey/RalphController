using RalphController.Models;
using RalphController.Git;
using System.Diagnostics;
using System.Text;

namespace RalphController.Parallel;

/// <summary>
/// Handles conflict resolution between parallel agents using AI negotiation
/// </summary>
public class ConflictNegotiator
{
    private readonly RalphConfig _config;
    private readonly AIProviderConfig _providerConfig;

    public event Action<ConflictInfo>? OnConflictDetected;
    public event Action<ConflictInfo>? OnNegotiationStart;
    public event Action<ConflictResolution>? OnConflictResolved;

    public ConflictNegotiator(RalphConfig config, AIProviderConfig providerConfig)
    {
        _config = config;
        _providerConfig = providerConfig;
    }

    /// <summary>
    /// Resolve conflicts via AI negotiation
    /// </summary>
    public async Task<ConflictResolution> NegotiateResolutionAsync(
        List<GitConflict> conflicts,
        string agent1Id,
        string agent1Branch,
        string agent2Id,
        string agent2Branch,
        CancellationToken cancellationToken = default)
    {
        var info = new ConflictInfo
        {
            Conflicts = conflicts,
            Agent1Id = agent1Id,
            Agent1Branch = agent1Branch,
            Agent2Id = agent2Id,
            Agent2Branch = agent2Branch,
            Timestamp = DateTime.UtcNow
        };

        OnConflictDetected?.Invoke(info);
        OnNegotiationStart?.Invoke(info);

        var prompt = BuildNegotiationPrompt(conflicts, agent1Branch, agent2Branch);
        var result = await RunAIProcessAsync(prompt, cancellationToken);

        if (!result.Success)
        {
            return new ConflictResolution
            {
                Success = false,
                Error = "AI negotiation failed",
                RequiresManualIntervention = true
            };
        }

        var resolution = ParseResolution(result.Output, conflicts);
        OnConflictResolved?.Invoke(resolution);
        return resolution;
    }

    /// <summary>
    /// Apply conflict resolution to files
    /// </summary>
    public async Task<bool> ApplyResolutionAsync(
        ConflictResolution resolution,
        string worktreePath,
        CancellationToken cancellationToken = default)
    {
        if (!resolution.Success || resolution.ResolvedFiles == null)
            return false;

        foreach (var resolvedFile in resolution.ResolvedFiles)
        {
            var filePath = Path.Combine(worktreePath, resolvedFile.FilePath);
            await File.WriteAllTextAsync(filePath, resolvedFile.ResolvedContent, cancellationToken);
        }

        foreach (var resolvedFile in resolution.ResolvedFiles)
        {
            var filePath = Path.Combine(worktreePath, resolvedFile.FilePath);
            await RunGitCommandAsync(worktreePath, $"add \"{filePath}\"");
        }

        return true;
    }

    private string BuildNegotiationPrompt(
        List<GitConflict> conflicts,
        string branch1,
        string branch2)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- CONFLICT NEGOTIATION REQUEST ---");
        sb.AppendLine();
        sb.AppendLine("Two AI agents have made conflicting changes. You must help resolve these conflicts.");
        sb.AppendLine();
        sb.AppendLine($"Branch 1: {branch1}");
        sb.AppendLine($"Branch 2: {branch2}");
        sb.AppendLine();
        sb.AppendLine("Conflicts found:");

        foreach (var conflict in conflicts.Take(5))
        {
            sb.AppendLine();
            sb.AppendLine($"File: {conflict.FilePath}");

            if (File.Exists(conflict.FullPath))
            {
                try
                {
                    var content = File.ReadAllText(conflict.FullPath);
                    var preview = content.Length > 500 ? content[..500] + "..." : content;
                    sb.AppendLine(preview);
                }
                catch
                {
                    sb.AppendLine("(unable to read file)");
                }
            }
        }

        if (conflicts.Count > 5)
        {
            sb.AppendLine($"... and {conflicts.Count - 5} more conflicts");
        }

        sb.AppendLine();
        sb.AppendLine("RESPONSE FORMAT (for each conflict):");
        sb.AppendLine("---RESOLUTION---");
        sb.AppendLine("file: <relative path>");
        sb.AppendLine("content: <resolved content, no conflict markers>");
        sb.AppendLine("---END_RESOLUTION---");

        return sb.ToString();
    }

    private ConflictResolution ParseResolution(string aiOutput, List<GitConflict> conflicts)
    {
        var resolution = new ConflictResolution { Success = true };
        var resolvedFiles = new List<ResolvedFile>();

        var lines = aiOutput.Split('\n');
        string? currentFile = null;
        var contentBuilder = new StringBuilder();
        bool inContent = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("file:"))
            {
                if (currentFile != null && inContent)
                {
                    resolvedFiles.Add(new ResolvedFile
                    {
                        FilePath = currentFile,
                        ResolvedContent = contentBuilder.ToString()
                    });
                }
                currentFile = line.Substring(5).Trim();
                contentBuilder.Clear();
                inContent = false;
            }
            else if (line.StartsWith("content:") || (inContent && !line.StartsWith("---END")))
            {
                if (line.StartsWith("content:"))
                {
                    inContent = true;
                    if (line.Length > 8)
                    {
                        contentBuilder.AppendLine(line.Substring(8).Trim());
                    }
                }
                else if (inContent)
                {
                    contentBuilder.AppendLine(line);
                }
            }
        }

        if (currentFile != null && inContent)
        {
            resolvedFiles.Add(new ResolvedFile
            {
                FilePath = currentFile,
                ResolvedContent = contentBuilder.ToString()
            });
        }

        resolution.ResolvedFiles = resolvedFiles;
        resolution.Success = resolvedFiles.Count > 0;
        return resolution;
    }

    private async Task<NegotiatorProcessResult> RunAIProcessAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        try
        {
            // For negotiation we need plain text output to parse resolution blocks.
            var arguments = _providerConfig.Arguments;
            if (_providerConfig.UsesStreamJson)
            {
                arguments = arguments
                    .Replace("--output-format stream-json", "--output-format text")
                    .Replace("--verbose", "")
                    .Replace("--include-partial-messages", "");
                // Collapse multiple spaces from removed flags
                arguments = string.Join(' ', arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }

            var psi = new ProcessStartInfo
            {
                FileName = _providerConfig.ExecutablePath,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return new NegotiatorProcessResult { Success = false, Error = "Failed to start AI process" };
            }

            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            // Read stdout/stderr concurrently before WaitForExit to avoid deadlock
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return new NegotiatorProcessResult
            {
                Success = process.ExitCode == 0,
                Output = await outputTask,
                Error = await errorTask
            };
        }
        catch (Exception ex)
        {
            return new NegotiatorProcessResult { Success = false, Error = ex.Message };
        }
    }

    private async Task<string> RunGitCommandAsync(string workingDir, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            if (process == null) return "";

            var output = await process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();
            return output;
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>
/// Information about a detected conflict
/// </summary>
public class ConflictInfo
{
    public List<GitConflict> Conflicts { get; set; } = new();
    public string Agent1Id { get; set; } = string.Empty;
    public string Agent1Branch { get; set; } = string.Empty;
    public string Agent2Id { get; set; } = string.Empty;
    public string Agent2Branch { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Result of conflict resolution
/// </summary>
public class ConflictResolution
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public bool RequiresManualIntervention { get; set; }
    public List<ResolvedFile>? ResolvedFiles { get; set; }
}

/// <summary>
/// A resolved file
/// </summary>
public class ResolvedFile
{
    public required string FilePath { get; init; }
    public required string ResolvedContent { get; init; }
}

internal class NegotiatorProcessResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
}
