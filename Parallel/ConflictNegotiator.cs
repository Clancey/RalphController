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
        return await NegotiateResolutionAsync(
            conflicts, agent1Id, agent1Branch, agent2Id, agent2Branch,
            agent1TaskDescription: null, agent2TaskDescription: null,
            cancellationToken);
    }

    /// <summary>
    /// Resolve conflicts via AI negotiation with task context.
    /// Enhanced overload that includes task descriptions and intent for better resolution.
    /// </summary>
    public async Task<ConflictResolution> NegotiateResolutionAsync(
        List<GitConflict> conflicts,
        string agent1Id,
        string agent1Branch,
        string agent2Id,
        string agent2Branch,
        string? agent1TaskDescription,
        string? agent2TaskDescription,
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

        var prompt = BuildNegotiationPrompt(
            conflicts, agent1Id, agent1Branch, agent2Id, agent2Branch,
            agent1TaskDescription, agent2TaskDescription);
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
        string agent1Id,
        string agent1Branch,
        string agent2Id,
        string agent2Branch,
        string? agent1TaskDescription,
        string? agent2TaskDescription)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Two agents made conflicting changes to the same files.");
        sb.AppendLine();

        // Agent A context (the branch being merged in)
        sb.AppendLine($"Agent A ({agent1Id}): {agent1TaskDescription ?? "No task description available"}");

        // Try to get diff for Agent A's branch
        var diffA = GetBranchDiff(agent1Branch);
        if (!string.IsNullOrEmpty(diffA))
        {
            sb.AppendLine("Their changes:");
            sb.AppendLine(TruncateDiff(diffA, MaxDiffLength));
        }
        sb.AppendLine();

        // Agent B context (the target branch / previously merged work)
        sb.AppendLine($"Agent B ({agent2Id}): {agent2TaskDescription ?? "No task description available"}");

        // Try to get diff for Agent B's branch
        var diffB = GetBranchDiff(agent2Branch);
        if (!string.IsNullOrEmpty(diffB))
        {
            sb.AppendLine("Their changes:");
            sb.AppendLine(TruncateDiff(diffB, MaxDiffLength));
        }
        sb.AppendLine();

        // Conflicted files with content
        sb.AppendLine("Conflicted files:");
        foreach (var conflict in conflicts.Take(MaxConflictFiles))
        {
            sb.AppendLine();
            sb.AppendLine($"--- {conflict.FilePath} ---");

            if (File.Exists(conflict.FullPath))
            {
                try
                {
                    var content = File.ReadAllText(conflict.FullPath);
                    var preview = content.Length > MaxFilePreviewLength
                        ? content[..MaxFilePreviewLength] + "\n... (truncated)"
                        : content;
                    sb.AppendLine(preview);
                }
                catch
                {
                    sb.AppendLine("(unable to read file)");
                }
            }
        }

        if (conflicts.Count > MaxConflictFiles)
        {
            sb.AppendLine($"... and {conflicts.Count - MaxConflictFiles} more conflicted files");
        }

        sb.AppendLine();
        sb.AppendLine("Resolve the conflict by producing the merged file content that");
        sb.AppendLine("preserves both agents' intended changes. If the changes are");
        sb.AppendLine("fundamentally incompatible, prefer Agent A's changes (merged first)");
        sb.AppendLine("and note what was lost.");
        sb.AppendLine();
        sb.AppendLine("RESPONSE FORMAT (for each conflicted file):");
        sb.AppendLine("---RESOLUTION---");
        sb.AppendLine("file: <relative path>");
        sb.AppendLine("content: <resolved content, no conflict markers>");
        sb.AppendLine("---END_RESOLUTION---");

        return sb.ToString();
    }

    /// <summary>Maximum characters of diff output to include per agent</summary>
    private const int MaxDiffLength = 2000;

    /// <summary>Maximum number of conflicted files to include in prompt</summary>
    private const int MaxConflictFiles = 8;

    /// <summary>Maximum characters of file content preview</summary>
    private const int MaxFilePreviewLength = 1500;

    /// <summary>
    /// Get the diff for a branch relative to its merge base.
    /// Returns empty string if the diff cannot be obtained.
    /// </summary>
    private string GetBranchDiff(string branchName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"diff {branchName}~1..{branchName} --stat -p",
                WorkingDirectory = _config.TargetDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            if (process == null) return "";

            // Read stdout/stderr concurrently before WaitForExit to avoid deadlock
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit(10_000);

            return outputTask.Result;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Truncate diff output to a maximum length, preserving complete lines.
    /// </summary>
    private static string TruncateDiff(string diff, int maxLength)
    {
        if (diff.Length <= maxLength) return diff;

        var truncated = diff[..maxLength];
        var lastNewline = truncated.LastIndexOf('\n');
        if (lastNewline > 0)
        {
            truncated = truncated[..lastNewline];
        }
        return truncated + "\n... (diff truncated)";
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
            // Strip agentic flags — negotiation is analysis only, no file editing.
            var arguments = _providerConfig.Arguments;
            if (_providerConfig.UsesStreamJson)
            {
                arguments = arguments
                    .Replace("--output-format stream-json", "--output-format text")
                    .Replace("--verbose", "")
                    .Replace("--include-partial-messages", "");
            }
            arguments = arguments
                .Replace("--dangerously-skip-permissions", "")
                .Replace("--dangerously-bypass-approvals-and-sandbox", "")
                .Replace("--allow-all-tools", "")
                .Replace("--auto-approve", "");
            if (_providerConfig.Provider == AIProvider.Claude && !arguments.Contains("--max-turns"))
            {
                arguments += " --max-turns 1";
            }
            arguments = string.Join(' ', arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));

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
    public string? Agent1TaskDescription { get; set; }
    public string? Agent2TaskDescription { get; set; }
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
