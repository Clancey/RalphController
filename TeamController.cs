using RalphController.Models;
using RalphController.Parallel;
using RalphController.Git;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RalphController;

/// <summary>
/// Orchestrates teams mode: decompose -> parallel execute -> verify & merge
/// </summary>
public class TeamController : IDisposable
{
    private readonly RalphConfig _config;
    private readonly TeamConfig _teamConfig;
    private readonly TaskQueue _taskQueue;
    private readonly GitWorktreeManager _gitManager;
    private readonly ConflictNegotiator _negotiator;
    private readonly ConcurrentDictionary<string, TeamAgent> _agents = new();
    private readonly SemaphoreSlim _mergeSemaphore;
    private CancellationTokenSource? _stopCts;
    private bool _disposed;
    private TeamControllerState _state = TeamControllerState.Idle;

    // Events
    public event Action<TeamControllerState>? OnStateChanged;
    public event Action<string>? OnOutput;
    public event Action<string>? OnError;
    public event Action<AgentStatistics>? OnAgentUpdate;
    public event Action<TaskQueueStatistics>? OnQueueUpdate;
    public event Action<TeamVerificationResult>? OnVerificationComplete;
    public event Action<TeamPhase>? OnPhaseChanged;

    public TeamController(RalphConfig config)
    {
        _config = config;
        _teamConfig = config.Teams ?? new TeamConfig();

        _taskQueue = new TaskQueue(TimeSpan.FromSeconds(_teamConfig.TaskClaimTimeoutSeconds));
        _gitManager = new GitWorktreeManager(config.TargetDirectory);
        _negotiator = new ConflictNegotiator(config, config.ProviderConfig);
        _mergeSemaphore = new SemaphoreSlim(_teamConfig.MaxConcurrentMerges);
    }

    /// <summary>Current controller state</summary>
    public TeamControllerState State => _state;

    /// <summary>Current phase</summary>
    public TeamPhase CurrentPhase { get; private set; } = TeamPhase.Idle;

    /// <summary>Task queue for monitoring</summary>
    public TaskQueue TaskQueue => _taskQueue;

    /// <summary>
    /// Start the three-phase teams execution
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        SetState(TeamControllerState.Initializing);
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            // Detect branches
            var sourceBranch = _teamConfig.SourceBranch;
            var targetBranch = _teamConfig.TargetBranch;

            if (string.IsNullOrEmpty(sourceBranch) || string.IsNullOrEmpty(targetBranch))
            {
                var currentBranch = await _gitManager.GetCurrentBranchAsync(_stopCts.Token);
                sourceBranch = string.IsNullOrEmpty(sourceBranch) ? currentBranch : sourceBranch;
                targetBranch = string.IsNullOrEmpty(targetBranch) ? currentBranch : targetBranch;
            }

            OnOutput?.Invoke($"Source: {sourceBranch}, Target: {targetBranch}");

            // Phase 1: Decompose
            SetPhase(TeamPhase.Decomposing);
            SetState(TeamControllerState.Running);
            await DecomposeAsync(_stopCts.Token);

            var stats = _taskQueue.GetStatistics();
            OnOutput?.Invoke($"Decomposed into {stats.Total} tasks");
            OnQueueUpdate?.Invoke(stats);

            if (stats.Total == 0)
            {
                OnError?.Invoke("No tasks found after decomposition");
                SetState(TeamControllerState.Failed);
                return;
            }

            // Phase 2: Parallel Execution
            SetPhase(TeamPhase.Executing);
            await ExecuteAsync(_stopCts.Token);

            // Phase 3: Verify & Merge
            SetPhase(TeamPhase.Verifying);
            await VerifyAndMergeAsync(_stopCts.Token);

            SetPhase(TeamPhase.Complete);
            SetState(TeamControllerState.Stopped);
            OnOutput?.Invoke("Teams execution complete!");
        }
        catch (OperationCanceledException)
        {
            SetState(TeamControllerState.Stopped);
            OnOutput?.Invoke("Teams execution cancelled");
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Teams execution failed: {ex.Message}");
            SetState(TeamControllerState.Failed);
        }
    }

    /// <summary>
    /// Phase 1: Decompose tasks
    /// </summary>
    private async Task DecomposeAsync(CancellationToken cancellationToken)
    {
        OnOutput?.Invoke("Phase 1: Decomposing tasks...");

        if (_teamConfig.DecompositionStrategy == DecompositionStrategy.FromPlan)
        {
            await LoadTasksFromPlanAsync(cancellationToken);
        }
        else
        {
            await AIDecomposeAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Load tasks from implementation_plan.md
    /// </summary>
    private async Task LoadTasksFromPlanAsync(CancellationToken cancellationToken)
    {
        var planPath = _config.PlanFilePath;
        if (!File.Exists(planPath))
        {
            OnError?.Invoke($"Implementation plan not found: {planPath}");
            return;
        }

        var lines = await File.ReadAllLinesAsync(planPath, cancellationToken);
        var category = "General";

        foreach (var line in lines)
        {
            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                category = line.Trim('#').Trim();
                continue;
            }

            if (line.TrimStart().StartsWith("- ["))
            {
                var task = ParseTaskFromLine(line, category);
                if (task != null)
                {
                    _taskQueue.Enqueue(task);
                }
            }
        }
    }

    /// <summary>
    /// AI-driven task decomposition using the lead agent
    /// </summary>
    private async Task AIDecomposeAsync(CancellationToken cancellationToken)
    {
        OnOutput?.Invoke("Using AI to decompose tasks...");

        var prompt = BuildDecompositionPrompt();
        var providerConfig = _teamConfig.LeadModel?.ToProviderConfig() ?? _config.ProviderConfig;

        var psi = new ProcessStartInfo
        {
            FileName = providerConfig.ExecutablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _config.TargetDirectory
        };

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
            OnError?.Invoke("Failed to start lead agent for decomposition");
            return;
        }

        if (providerConfig.UsesStdin || !providerConfig.UsesPromptArgument)
        {
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            OnError?.Invoke($"Lead agent decomposition failed: {error}");
            // Fallback to plan-based decomposition
            OnOutput?.Invoke("Falling back to plan-based decomposition...");
            await LoadTasksFromPlanAsync(cancellationToken);
            return;
        }

        // Parse the structured output
        var tasks = ParseDecomposedTasks(output);
        if (tasks.Count == 0)
        {
            OnOutput?.Invoke("AI decomposition produced no tasks, falling back to plan...");
            await LoadTasksFromPlanAsync(cancellationToken);
            return;
        }

        _taskQueue.EnqueueRange(tasks);
        OnOutput?.Invoke($"AI decomposed into {tasks.Count} tasks");
    }

    /// <summary>
    /// Phase 2: Parallel Execution
    /// </summary>
    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        OnOutput?.Invoke($"Phase 2: Starting {_teamConfig.AgentCount} agents...");

        var agentTasks = new List<Task>();

        for (int i = 0; i < _teamConfig.AgentCount; i++)
        {
            var agentId = Guid.NewGuid().ToString("N")[..8];
            var assignedModel = _teamConfig.GetAgentModel(i);

            var agent = new TeamAgent(_config, _teamConfig, agentId, i, _gitManager, assignedModel);

            // Wire events
            agent.OnStateChanged += _ => OnAgentUpdate?.Invoke(agent.Statistics);
            agent.OnTaskStart += task =>
                OnOutput?.Invoke($"[{agent.Statistics.Name}] Starting: {task.Title ?? task.Description}");
            agent.OnTaskComplete += (task, result) =>
            {
                _taskQueue.Complete(task.TaskId, result);
                OnQueueUpdate?.Invoke(_taskQueue.GetStatistics());
                OnOutput?.Invoke($"[{agent.Statistics.Name}] Completed: {task.Title ?? task.Description}");
            };
            agent.OnTaskFailed += (task, error) =>
            {
                _taskQueue.Fail(task.TaskId, error, _teamConfig.MaxRetries);
                OnQueueUpdate?.Invoke(_taskQueue.GetStatistics());
                OnError?.Invoke($"[{agent.Statistics.Name}] Failed: {error}");
            };
            agent.OnOutput += msg => OnOutput?.Invoke($"[{agent.Statistics.Name}] {msg}");
            agent.OnError += msg => OnError?.Invoke($"[{agent.Statistics.Name}] {msg}");

            _agents[agentId] = agent;

            // Initialize worktree
            if (_teamConfig.UseWorktrees)
            {
                var initialized = await agent.InitializeAsync(cancellationToken);
                if (!initialized)
                {
                    OnError?.Invoke($"Failed to initialize agent {agent.Statistics.Name}");
                    continue;
                }
            }

            // Start agent loop
            var agentTask = agent.RunLoopAsync(
                claimAgentId => _taskQueue.TryClaim(claimAgentId),
                cancellationToken);
            agentTasks.Add(agentTask);
        }

        // Wait for all agents to complete
        await Task.WhenAll(agentTasks);

        var finalStats = _taskQueue.GetStatistics();
        OnOutput?.Invoke($"Execution complete: {finalStats.Completed}/{finalStats.Total} tasks done, {finalStats.Failed} failed");
        OnQueueUpdate?.Invoke(finalStats);
    }

    /// <summary>
    /// Phase 3: Verify and Merge
    /// </summary>
    private async Task VerifyAndMergeAsync(CancellationToken cancellationToken)
    {
        OnOutput?.Invoke("Phase 3: Verifying and merging...");

        // Merge each agent's work back
        foreach (var (agentId, agent) in _agents)
        {
            if (agent.Statistics.TasksCompleted == 0)
            {
                OnOutput?.Invoke($"[{agent.Statistics.Name}] No tasks completed, skipping merge");
                continue;
            }

            await _mergeSemaphore.WaitAsync(cancellationToken);
            try
            {
                OnOutput?.Invoke($"[{agent.Statistics.Name}] Merging work...");
                var result = await agent.MergeAsync(cancellationToken);

                if (result.Success)
                {
                    var shortSha = result.MergeCommitSha is { Length: >= 8 } sha ? sha[..8] : result.MergeCommitSha ?? "unknown";
                    OnOutput?.Invoke($"[{agent.Statistics.Name}] Merge successful: {shortSha}");
                }
                else if (result.Conflicts?.Count > 0)
                {
                    OnError?.Invoke($"[{agent.Statistics.Name}] {result.Conflicts.Count} conflicts detected");

                    if (_teamConfig.ConflictResolution == ConflictResolutionMode.AINegotiated)
                    {
                        var resolution = await _negotiator.NegotiateResolutionAsync(
                            result.Conflicts,
                            agentId, agent.Statistics.BranchName ?? "",
                            "main", _teamConfig.TargetBranch,
                            cancellationToken);

                        if (resolution.Success)
                        {
                            await _negotiator.ApplyResolutionAsync(
                                resolution,
                                agent.Statistics.WorktreePath ?? "",
                                cancellationToken);
                            OnOutput?.Invoke($"[{agent.Statistics.Name}] Conflicts resolved via AI");

                            // Retry merge after resolution
                            var retryResult = await agent.MergeAsync(cancellationToken);
                            if (retryResult.Success)
                            {
                                OnOutput?.Invoke($"[{agent.Statistics.Name}] Retry merge successful");
                            }
                        }
                    }
                }
                else
                {
                    OnError?.Invoke($"[{agent.Statistics.Name}] Merge failed: {result.Error}");
                }
            }
            finally
            {
                _mergeSemaphore.Release();
            }
        }

        // Mark completed tasks in the implementation plan directly (no separate AI commit)
        OnOutput?.Invoke("Marking completed tasks in implementation plan...");
        var verification = MarkCompletedTasksInPlan();
        OnVerificationComplete?.Invoke(verification);

        if (verification.AllTasksComplete)
        {
            OnOutput?.Invoke($"All {verification.TasksMarked} tasks marked complete in plan");
        }
        else
        {
            var completed = verification.TasksMarked;
            var incomplete = verification.IncompleteTasks.Count;
            OnOutput?.Invoke($"Marked {completed} tasks complete, {incomplete} tasks incomplete");
            foreach (var issue in verification.IncompleteTasks.Take(5))
            {
                OnError?.Invoke($"  - {issue}");
            }
        }

        // Cleanup worktrees
        if (_teamConfig.CleanupWorktreesOnSuccess)
        {
            foreach (var agent in _agents.Values)
            {
                await agent.CleanupAsync();
            }
        }
    }

    /// <summary>
    /// Mark completed tasks directly in the implementation plan file.
    /// Avoids spawning a separate AI agent and creating extra commits.
    /// </summary>
    private TeamVerificationResult MarkCompletedTasksInPlan()
    {
        var result = new TeamVerificationResult();
        var planPath = _config.PlanFilePath;

        if (!File.Exists(planPath))
        {
            result.Summary = "No implementation plan file found";
            result.AllTasksComplete = true; // Nothing to mark
            return result;
        }

        try
        {
            var planContent = File.ReadAllText(planPath);
            var completedTasks = _taskQueue.GetAllTasks()
                .Where(t => t.Status == Models.TaskStatus.Completed)
                .ToList();
            var failedTasks = _taskQueue.GetAllTasks()
                .Where(t => t.Status == Models.TaskStatus.Failed)
                .ToList();

            var tasksMarked = 0;

            foreach (var task in completedTasks)
            {
                // Try to match task title/description against unchecked items in the plan
                // Match patterns like "- [ ] task description" and replace with "- [x] task description"
                var patterns = new List<string>();
                if (!string.IsNullOrEmpty(task.Title))
                    patterns.Add(Regex.Escape(task.Title.Trim()));
                if (!string.IsNullOrEmpty(task.Description) && task.Description != task.Title)
                {
                    // Use first line of description for matching
                    var firstLine = task.Description.Split('\n')[0].Trim();
                    if (firstLine.Length > 10) // Only use meaningful descriptions
                        patterns.Add(Regex.Escape(firstLine));
                }

                foreach (var pattern in patterns)
                {
                    // Match "- [ ] <pattern>" with flexible whitespace
                    var checkboxPattern = $@"^(\s*-\s*)\[\s*\]\s*({pattern})";
                    var match = Regex.Match(planContent, checkboxPattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        planContent = planContent.Substring(0, match.Index)
                            + $"{match.Groups[1].Value}[x] {match.Groups[2].Value}"
                            + planContent.Substring(match.Index + match.Length);
                        tasksMarked++;
                        break; // Only mark once per task
                    }
                }
            }

            // Write back the updated plan
            if (tasksMarked > 0)
            {
                File.WriteAllText(planPath, planContent);
                OnOutput?.Invoke($"Updated {planPath}: marked {tasksMarked} tasks as complete");
            }

            // Build verification result
            result.TasksMarked = tasksMarked;
            result.AllTasksComplete = failedTasks.Count == 0 && completedTasks.Count > 0;

            foreach (var failed in failedTasks)
            {
                result.IncompleteTasks.Add($"FAILED: {failed.Title ?? failed.Description}");
            }

            // Check for any tasks that were completed but couldn't be matched in the plan
            var unmatched = completedTasks.Count - tasksMarked;
            if (unmatched > 0)
            {
                result.Summary = $"{tasksMarked} marked complete, {unmatched} completed but not found in plan, {failedTasks.Count} failed";
            }
            else
            {
                result.Summary = $"{tasksMarked} tasks marked complete, {failedTasks.Count} failed";
            }

            return result;
        }
        catch (Exception ex)
        {
            result.AllTasksComplete = false;
            result.IncompleteTasks.Add($"Error updating plan: {ex.Message}");
            return result;
        }
    }

    private string BuildDecompositionPrompt()
    {
        var sb = new StringBuilder();

        // Read prompt.md
        if (File.Exists(_config.PromptFilePath))
        {
            sb.AppendLine("## Project Prompt:");
            sb.AppendLine(File.ReadAllText(_config.PromptFilePath));
            sb.AppendLine();
        }

        // Read implementation_plan.md if it exists
        if (File.Exists(_config.PlanFilePath))
        {
            sb.AppendLine("## Implementation Plan:");
            sb.AppendLine(File.ReadAllText(_config.PlanFilePath));
            sb.AppendLine();
        }

        sb.AppendLine(@"Read the project prompt and implementation plan above. Break the work into independent,
parallelizable subtasks. Output in this EXACT format:

---TEAM_TASKS---
- TASK: <short title>
  DESCRIPTION: <what to implement>
  PRIORITY: <critical|high|normal|low>
  DEPENDS_ON: <comma-separated task titles, or ""none"">
  FILES: <likely files to modify>
- TASK: <next task>
  DESCRIPTION: <what to implement>
  PRIORITY: <critical|high|normal|low>
  DEPENDS_ON: <comma-separated task titles, or ""none"">
  FILES: <likely files to modify>
---END_TASKS---

Guidelines:
- Each task should be independently completable
- Minimize file overlap between tasks
- Order by dependency (independent tasks first)
- Critical tasks should be done first
- Aim for " + _teamConfig.AgentCount + @" to " + (_teamConfig.AgentCount * 3) + @" tasks total");

        return sb.ToString();
    }

    /// <summary>
    /// Parse structured task output from AI decomposition
    /// </summary>
    private List<AgentTask> ParseDecomposedTasks(string output)
    {
        var tasks = new List<AgentTask>();

        // Find the TEAM_TASKS block
        var match = Regex.Match(output,
            @"---TEAM_TASKS---\s*(.*?)\s*---END_TASKS---",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            OnOutput?.Invoke("Could not find ---TEAM_TASKS--- block in AI output");
            return tasks;
        }

        var taskBlock = match.Groups[1].Value;
        var taskMatches = Regex.Matches(taskBlock,
            @"- TASK:\s*(.+?)(?:\n\s+DESCRIPTION:\s*(.+?))?(?:\n\s+PRIORITY:\s*(.+?))?(?:\n\s+DEPENDS_ON:\s*(.+?))?(?:\n\s+FILES:\s*(.+?))?(?=\n- TASK:|\z)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match taskMatch in taskMatches)
        {
            var title = taskMatch.Groups[1].Value.Trim();
            var description = taskMatch.Groups[2].Success ? taskMatch.Groups[2].Value.Trim() : title;
            var priorityStr = taskMatch.Groups[3].Success ? taskMatch.Groups[3].Value.Trim().ToLower() : "normal";
            var dependsOnStr = taskMatch.Groups[4].Success ? taskMatch.Groups[4].Value.Trim() : "none";
            var filesStr = taskMatch.Groups[5].Success ? taskMatch.Groups[5].Value.Trim() : "";

            var priority = priorityStr switch
            {
                "critical" => TaskPriority.Critical,
                "high" => TaskPriority.High,
                "low" => TaskPriority.Low,
                _ => TaskPriority.Normal
            };

            var files = string.IsNullOrEmpty(filesStr)
                ? new List<string>()
                : filesStr.Split(',').Select(f => f.Trim()).Where(f => !string.IsNullOrEmpty(f)).ToList();

            var dependsOn = dependsOnStr.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? new List<string>()
                : dependsOnStr.Split(',').Select(d => d.Trim()).Where(d => !string.IsNullOrEmpty(d)).ToList();

            tasks.Add(new AgentTask
            {
                Title = title,
                Description = description,
                Priority = priority,
                Files = files,
                DependsOn = dependsOn
            });
        }

        return tasks;
    }

    private AgentTask? ParseTaskFromLine(string line, string category)
    {
        if (line.Contains("[x]")) return null;

        var isPriority = line.Contains("[!]");
        var description = line
            .Replace("- [ ]", "")
            .Replace("- [!]", "")
            .Replace("- [?]", "")
            .Replace("[!]", "")
            .Trim();

        if (string.IsNullOrWhiteSpace(description)) return null;

        return new AgentTask
        {
            Title = description.Length > 60 ? description[..60] + "..." : description,
            Description = description,
            SourceLine = line,
            Priority = isPriority ? TaskPriority.High : TaskPriority.Normal,
            Category = category
        };
    }

    /// <summary>
    /// Get statistics for all agents
    /// </summary>
    public List<AgentStatistics> GetAgentStatistics() =>
        _agents.Values.Select(a => a.Statistics).ToList();

    /// <summary>
    /// Get task queue statistics
    /// </summary>
    public TaskQueueStatistics GetQueueStatistics() => _taskQueue.GetStatistics();

    /// <summary>
    /// Stop all agents
    /// </summary>
    public void Stop()
    {
        SetState(TeamControllerState.Stopping);
        _stopCts?.Cancel();

        foreach (var agent in _agents.Values)
        {
            agent.Stop();
        }
    }

    private void SetState(TeamControllerState newState)
    {
        if (_state != newState)
        {
            _state = newState;
            OnStateChanged?.Invoke(newState);
        }
    }

    private void SetPhase(TeamPhase phase)
    {
        CurrentPhase = phase;
        OnPhaseChanged?.Invoke(phase);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        foreach (var agent in _agents.Values)
        {
            agent.Dispose();
        }

        _gitManager?.Dispose();
        _mergeSemaphore?.Dispose();

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// State of the team controller
/// </summary>
public enum TeamControllerState
{
    Idle,
    Initializing,
    Running,
    Stopping,
    Stopped,
    Failed
}

/// <summary>
/// Current phase of teams execution
/// </summary>
public enum TeamPhase
{
    Idle,
    Decomposing,
    Executing,
    Verifying,
    Complete
}

/// <summary>
/// Result of team verification
/// </summary>
public class TeamVerificationResult
{
    public bool AllTasksComplete { get; set; }
    public List<string> IncompleteTasks { get; set; } = new();
    public string? Summary { get; set; }
    public string? VerificationOutput { get; set; }
    public int TasksMarked { get; set; }
}
