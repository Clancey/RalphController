using RalphController.Models;
using RalphController.Git;
using System.Text;

namespace RalphController.Merge;

/// <summary>
/// AI agent that resolves merge conflicts by running a full AI CLI process
/// in the merge working directory. Unlike ConflictNegotiator (which does text-based
/// resolution), this agent has full tool access — it can read files, edit them,
/// run builds, and iterate until the merge is clean.
/// </summary>
public class MergeFixAgent
{
    private readonly RalphConfig _config;
    private readonly TeamConfig _teamConfig;

    public event Action<string>? OnOutput;
    public event Action<string>? OnError;

    /// <summary>Duration of the last AI process call (for AI time tracking)</summary>
    public TimeSpan LastDuration { get; private set; }

    public MergeFixAgent(RalphConfig config, TeamConfig teamConfig)
    {
        _config = config;
        _teamConfig = teamConfig;
    }

    /// <summary>
    /// Spin up an AI coding agent to resolve merge conflicts in the given directory.
    /// The agent gets full tool access (read, edit, run build, git add, etc.).
    /// Returns true if the agent exits successfully (exit code 0).
    /// </summary>
    public async Task<bool> ResolveAsync(
        string workingDir,
        List<GitConflict> conflicts,
        string? mergeError,
        string? taskDescription,
        CancellationToken ct)
    {
        OnOutput?.Invoke($"Launching AI merge-fix agent in {workingDir} ({conflicts.Count} conflicts)...");

        var prompt = BuildPrompt(conflicts, mergeError, taskDescription);
        var providerConfig = GetProviderConfig();

        try
        {
            var result = await AIProcessRunner.RunAsync(
                providerConfig,
                prompt,
                workingDir,
                output =>
                {
                    OnOutput?.Invoke($"[merge-fix] {output}");
                },
                ct);

            LastDuration = result.Duration;

            if (result.Success)
            {
                OnOutput?.Invoke("Merge-fix agent completed successfully");
            }
            else
            {
                OnError?.Invoke($"Merge-fix agent failed: {result.Error}");
            }

            return result.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Merge-fix agent error: {ex.Message}");
            return false;
        }
    }

    private string BuildPrompt(
        List<GitConflict> conflicts,
        string? mergeError,
        string? taskDescription)
    {
        var sb = new StringBuilder();

        sb.AppendLine("--- MERGE CONFLICT RESOLUTION ---");
        sb.AppendLine("You are a merge-fix agent. A git merge has failed and you need to resolve the conflicts.");
        sb.AppendLine("You have full access to the repository — read files, edit them, run git commands, and run builds.");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(taskDescription))
        {
            sb.AppendLine("--- TASK CONTEXT ---");
            sb.AppendLine($"The merge that failed was for this task: {taskDescription}");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(mergeError))
        {
            sb.AppendLine("--- MERGE ERROR ---");
            sb.AppendLine(mergeError);
            sb.AppendLine();
        }

        sb.AppendLine("--- CONFLICTED FILES ---");
        foreach (var conflict in conflicts)
        {
            sb.AppendLine($"  - {conflict.FilePath}");
        }
        sb.AppendLine();

        // Include project context if available
        var promptContent = AIProcessRunner.TryReadFile(
            _config.PromptFilePath, null);
        if (!string.IsNullOrEmpty(promptContent))
        {
            sb.AppendLine("--- PROJECT CONTEXT ---");
            sb.AppendLine(AIProcessRunner.StripRalphStatusBlock(promptContent));
            sb.AppendLine();
        }

        var agentsContent = AIProcessRunner.TryReadFile(_config.AgentsFilePath);
        if (!string.IsNullOrEmpty(agentsContent))
        {
            sb.AppendLine("--- AGENT CONFIGURATION (agents.md) ---");
            sb.AppendLine(agentsContent);
            sb.AppendLine();
        }

        sb.AppendLine("--- INSTRUCTIONS ---");
        sb.AppendLine("1. Read the conflicted files and understand both sides of each conflict");
        sb.AppendLine("2. Resolve each conflict by editing the files to merge both changes correctly");
        sb.AppendLine("3. Remove ALL conflict markers (<<<<<<, ======, >>>>>>)");
        sb.AppendLine("4. Run `git add` on each resolved file");

        if (!string.IsNullOrEmpty(_teamConfig.VerifyCommand))
        {
            sb.AppendLine($"5. Run the build/test command to verify: {_teamConfig.VerifyCommand}");
            sb.AppendLine("6. If the build fails, fix the issues and re-run until it passes");
        }
        else
        {
            sb.AppendLine("5. If a build command is available, run it to verify the resolution compiles");
        }

        sb.AppendLine();
        sb.AppendLine("When all conflicts are resolved and the code compiles, exit normally.");

        return sb.ToString();
    }

    private AIProviderConfig GetProviderConfig()
    {
        // Use the lead model if configured, otherwise fall back to the default provider
        if (_teamConfig.LeadModel != null)
        {
            return _teamConfig.LeadModel.ToProviderConfig();
        }
        return _config.ProviderConfig;
    }
}
