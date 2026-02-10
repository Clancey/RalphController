using RalphController.Models;
using RalphController.Git;
using System.Diagnostics;

namespace RalphController;

/// <summary>
/// Individual team agent running in isolated worktree with assigned model
/// </summary>
public class TeamAgent : IDisposable
{
    private readonly RalphConfig _config;
    private readonly TeamConfig _teamConfig;
    private readonly string _agentId;
    private readonly string _worktreePath;
    private readonly string _branchName;
    private readonly GitWorktreeManager _gitManager;
    private readonly int _agentIndex;
    private readonly ModelSpec? _assignedModel;
    private CancellationTokenSource? _stopCts;
    private bool _disposed;

    /// <summary>Agent state</summary>
    public ParallelAgentState State { get; private set; } = ParallelAgentState.Idle;

    /// <summary>Statistics</summary>
    public AgentStatistics Statistics { get; }

    /// <summary>Current task</summary>
    public AgentTask? CurrentTask { get; private set; }

    // Events
    public event Action<ParallelAgentState>? OnStateChanged;
    public event Action<AgentTask>? OnTaskStart;
    public event Action<AgentTask, TaskResult>? OnTaskComplete;
    public event Action<AgentTask, string>? OnTaskFailed;
    public event Action<string>? OnOutput;
    public event Action<string>? OnError;
    public event Action<List<GitConflict>>? OnConflictDetected;

    public TeamAgent(
        RalphConfig config,
        TeamConfig teamConfig,
        string agentId,
        int agentIndex,
        GitWorktreeManager gitManager,
        ModelSpec? assignedModel = null)
    {
        _config = config;
        _teamConfig = teamConfig;
        _agentId = agentId;
        _agentIndex = agentIndex;
        _gitManager = gitManager;
        _assignedModel = assignedModel ?? teamConfig.GetAgentModel(agentIndex);

        _branchName = $"team-agent-{agentId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        _worktreePath = teamConfig.UseWorktrees
            ? Path.Combine(config.TargetDirectory, ".ralph-worktrees", $"team-{agentId}")
            : config.TargetDirectory;

        var modelLabel = _assignedModel?.DisplayName ?? config.Provider.ToString();
        Statistics = new AgentStatistics
        {
            AgentId = agentId,
            Name = $"Agent {_agentIndex + 1} [{modelLabel}]",
            WorktreePath = _worktreePath,
            BranchName = _branchName,
            AssignedModel = _assignedModel
        };
    }

    /// <summary>
    /// Initialize the agent's worktree
    /// </summary>
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_teamConfig.UseWorktrees) return true;

        SetState(ParallelAgentState.Initializing);

        var sourceBranch = _teamConfig.SourceBranch;
        if (string.IsNullOrEmpty(sourceBranch))
        {
            sourceBranch = await _gitManager.GetCurrentBranchAsync(cancellationToken);
        }

        var created = await _gitManager.CreateWorktreeAsync(
            _worktreePath,
            _branchName,
            sourceBranch,
            cancellationToken);

        if (!created)
        {
            OnError?.Invoke($"Failed to create worktree at {_worktreePath}");
            SetState(ParallelAgentState.Failed);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Execute a specific task
    /// </summary>
    public async Task<TaskResult> ExecuteTaskAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CurrentTask = task;
        Statistics.CurrentTask = task;
        OnTaskStart?.Invoke(task);
        SetState(ParallelAgentState.Running);

        try
        {
            var prompt = BuildTaskPrompt(task);
            var result = await RunAIProcessAsync(prompt, _stopCts.Token);

            Statistics.Iterations++;
            Statistics.LastActivityAt = DateTime.UtcNow;

            var taskResult = new TaskResult(
                result.Success,
                result.Success ? $"Completed: {task.Description}" : $"Failed: {result.Error}",
                result.FilesModified ?? new List<string>(),
                result.Output,
                task.ClaimedAt.HasValue ? DateTime.UtcNow - task.ClaimedAt.Value : TimeSpan.Zero
            );

            if (result.Success)
            {
                Statistics.TasksCompleted++;
                OnTaskComplete?.Invoke(task, taskResult);

                if (_teamConfig.UseWorktrees)
                {
                    await _gitManager.CommitWorktreeAsync(
                        _worktreePath,
                        $"[{Statistics.Name}] {task.Title ?? task.Description}",
                        cancellationToken);
                }
            }
            else
            {
                Statistics.TasksFailed++;
                OnTaskFailed?.Invoke(task, result.Error ?? "Unknown error");
            }

            return taskResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Statistics.TasksFailed++;
            OnTaskFailed?.Invoke(task, ex.Message);
            return new TaskResult(false, ex.Message, new List<string>());
        }
        finally
        {
            CurrentTask = null;
            Statistics.CurrentTask = null;
        }
    }

    /// <summary>
    /// Run the agent in a loop, claiming tasks from the queue
    /// </summary>
    public async Task RunLoopAsync(
        Func<string, AgentTask?> claimTask,
        CancellationToken cancellationToken = default)
    {
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetState(ParallelAgentState.Running);

        try
        {
            while (!_stopCts.Token.IsCancellationRequested)
            {
                var task = claimTask(_agentId);
                if (task == null)
                {
                    // No more tasks available
                    break;
                }

                await ExecuteTaskAsync(task, _stopCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        finally
        {
            SetState(ParallelAgentState.Stopped);
        }
    }

    /// <summary>
    /// Attempt to merge work back to target branch
    /// </summary>
    public async Task<MergeResult> MergeAsync(CancellationToken cancellationToken = default)
    {
        if (!_teamConfig.UseWorktrees)
        {
            return new MergeResult { Success = true };
        }

        SetState(ParallelAgentState.Merging);
        Statistics.MergesAttempted++;

        try
        {
            var targetBranch = _teamConfig.TargetBranch;
            if (string.IsNullOrEmpty(targetBranch))
            {
                targetBranch = await _gitManager.GetCurrentBranchAsync(cancellationToken);
            }

            MergeResult result;
            switch (_teamConfig.MergeStrategy)
            {
                case MergeStrategy.RebaseThenMerge:
                    result = await _gitManager.RebaseAndMergeAsync(
                        _worktreePath, _branchName, targetBranch, cancellationToken);
                    break;
                case MergeStrategy.MergeDirect:
                    result = await _gitManager.MergeDirectAsync(
                        _worktreePath, _branchName, targetBranch, cancellationToken);
                    break;
                default:
                    result = await _gitManager.SequentialMergeAsync(
                        _worktreePath, _branchName, targetBranch, cancellationToken);
                    break;
            }

            if (result.Success)
            {
                Statistics.MergesSucceeded++;
            }
            else if (result.Conflicts?.Count > 0)
            {
                Statistics.ConflictsDetected += result.Conflicts.Count;
                OnConflictDetected?.Invoke(result.Conflicts);
            }

            return result;
        }
        finally
        {
            if (State == ParallelAgentState.Merging)
            {
                SetState(ParallelAgentState.Stopped);
            }
        }
    }

    /// <summary>
    /// Clean up worktree
    /// </summary>
    public async Task CleanupAsync()
    {
        if (_teamConfig.UseWorktrees && _teamConfig.CleanupWorktreesOnSuccess)
        {
            await _gitManager.RemoveWorktreeAsync(_worktreePath);
        }
    }

    /// <summary>
    /// Stop the agent
    /// </summary>
    public void Stop()
    {
        _stopCts?.Cancel();
    }

    private void SetState(ParallelAgentState newState)
    {
        if (State != newState)
        {
            State = newState;
            Statistics.State = newState;
            OnStateChanged?.Invoke(newState);
        }
    }

    private string BuildTaskPrompt(AgentTask task)
    {
        var promptBuilder = new System.Text.StringBuilder();

        // Read the main prompt file
        // When RalphFolder is set, project files are in a shared location (not per-worktree)
        var promptPath = !string.IsNullOrEmpty(_config.RalphFolder)
            ? _config.PromptFilePath
            : _teamConfig.UseWorktrees
                ? Path.Combine(_worktreePath, _config.PromptFile)
                : _config.PromptFilePath;

        if (File.Exists(promptPath))
        {
            var mainPrompt = File.ReadAllText(promptPath);
            promptBuilder.AppendLine(mainPrompt);
            promptBuilder.AppendLine();
        }
        else if (File.Exists(_config.PromptFilePath))
        {
            var mainPrompt = File.ReadAllText(_config.PromptFilePath);
            promptBuilder.AppendLine(mainPrompt);
            promptBuilder.AppendLine();
        }

        // Add task-specific instructions
        promptBuilder.AppendLine("--- YOUR ASSIGNED TASK ---");
        if (!string.IsNullOrEmpty(task.Title))
        {
            promptBuilder.AppendLine($"TASK: {task.Title}");
        }
        promptBuilder.AppendLine($"DESCRIPTION: {task.Description}");

        if (task.Files.Count > 0)
        {
            promptBuilder.AppendLine($"LIKELY FILES: {string.Join(", ", task.Files)}");
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("INSTRUCTIONS:");
        promptBuilder.AppendLine("- Focus ONLY on this specific task");
        promptBuilder.AppendLine("- Do not modify files unrelated to this task");
        promptBuilder.AppendLine("- Commit your changes when done");
        promptBuilder.AppendLine("- Report completion with ---RALPH_STATUS--- block");
        promptBuilder.AppendLine();

        // Add implementation plan context
        var planPath = !string.IsNullOrEmpty(_config.RalphFolder)
            ? _config.PlanFilePath
            : _teamConfig.UseWorktrees
                ? Path.Combine(_worktreePath, _config.PlanFile)
                : _config.PlanFilePath;

        if (File.Exists(planPath))
        {
            promptBuilder.AppendLine("--- IMPLEMENTATION PLAN CONTEXT ---");
            var planLines = File.ReadAllLines(planPath).Take(30);
            foreach (var line in planLines)
            {
                promptBuilder.AppendLine(line);
            }
            promptBuilder.AppendLine("...");
        }

        return promptBuilder.ToString();
    }

    private AIProviderConfig GetProviderConfig()
    {
        if (_assignedModel != null)
        {
            return _assignedModel.ToProviderConfig();
        }
        return _config.ProviderConfig;
    }

    private async Task<AgentProcessResult> RunAIProcessAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        try
        {
            var providerConfig = GetProviderConfig();
            var workingDir = _teamConfig.UseWorktrees ? _worktreePath : _config.TargetDirectory;

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
                // Escape prompt for command line
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

            // Read output
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            var error = await errorTask;

            // Stream output to listeners
            if (!string.IsNullOrEmpty(output))
            {
                foreach (var line in output.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        OnOutput?.Invoke(line);
                        Statistics.OutputChars += line.Length;
                    }
                }
            }

            if (!string.IsNullOrEmpty(error))
            {
                Statistics.ErrorChars += error.Length;
            }

            // Get modified files
            var modifiedFiles = _teamConfig.UseWorktrees
                ? await _gitManager.GetModifiedFilesAsync(_worktreePath, cancellationToken)
                : new List<string>();

            return new AgentProcessResult
            {
                Success = process.ExitCode == 0,
                Output = output,
                Error = error,
                FilesModified = modifiedFiles
            };
        }
        catch (Exception ex)
        {
            return new AgentProcessResult { Success = false, Error = ex.Message };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stopCts?.Cancel();
        _stopCts?.Dispose();

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Result from AI process in team agent
/// </summary>
internal class AgentProcessResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public List<string>? FilesModified { get; set; }
}
