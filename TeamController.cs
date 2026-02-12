using RalphController.Models;
using RalphController.Merge;
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
    private readonly TaskStore _taskStore;
    private readonly GitWorktreeManager _gitManager;
    private readonly ConflictNegotiator _negotiator;
    private readonly MergeManager _mergeManager;
    private readonly ConcurrentDictionary<string, TeamAgent> _agents = new();
    private readonly SemaphoreSlim _mergeSemaphore;
    private CancellationTokenSource? _stopCts;
    private bool _disposed;
    private volatile TeamControllerState _state = TeamControllerState.Idle;

    // Events
    public event Action<TeamControllerState>? OnStateChanged;
    public event Action<string>? OnOutput;
    public event Action<string>? OnError;
    public event Action<AgentStatistics>? OnAgentUpdate;
    public event Action<TaskStoreStatistics>? OnQueueUpdate;
    public event Action<TeamVerificationResult>? OnVerificationComplete;
    public event Action<TeamPhase>? OnPhaseChanged;

    public TeamController(RalphConfig config)
    {
        _config = config;
        _teamConfig = config.Teams ?? new TeamConfig();

        var teamName = _teamConfig.TeamName ?? "default";
        var storePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ralph", "teams", teamName, "tasks");
        _taskStore = TaskStore.LoadFromDisk(
            storePath,
            TimeSpan.FromSeconds(_teamConfig.TaskClaimTimeoutSeconds));
        _gitManager = new GitWorktreeManager(config.TargetDirectory);
        _negotiator = new ConflictNegotiator(config, config.ProviderConfig);
        _mergeManager = new MergeManager(_gitManager, _negotiator, _taskStore, _teamConfig, config);
        _mergeSemaphore = new SemaphoreSlim(_teamConfig.MaxConcurrentMerges);
    }

    /// <summary>Current controller state</summary>
    public TeamControllerState State => _state;

    /// <summary>Current phase (volatile for cross-thread visibility)</summary>
    public TeamPhase CurrentPhase => _currentPhase;
    private volatile TeamPhase _currentPhase = TeamPhase.Idle;

    /// <summary>When the current phase started (for elapsed time display)</summary>
    public DateTime PhaseStartedAt { get; private set; } = DateTime.UtcNow;

    /// <summary>Task store for monitoring</summary>
    public TaskStore TaskStore => _taskStore;

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

            // Phase 1: Decompose (skip if tasks restored from disk)
            SetPhase(TeamPhase.Decomposing);
            SetState(TeamControllerState.Running);

            var existingStats = _taskStore.GetStatistics();
            if (existingStats.Total > 0 && existingStats.Pending > 0)
            {
                OnOutput?.Invoke($"Restored {existingStats.Total} tasks from previous session ({existingStats.Completed} completed, {existingStats.Pending} pending)");
            }
            else
            {
                await DecomposeAsync(_stopCts.Token);
            }

            var stats = _taskStore.GetStatistics();
            if (existingStats.Total == 0)
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
            _taskStore.DeletePersistenceFiles();
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
        var taskIndex = 0;
        var tasks = new List<AgentTask>();

        foreach (var line in lines)
        {
            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                category = line.Trim('#').Trim();
                continue;
            }

            if (line.TrimStart().StartsWith("- ["))
            {
                taskIndex++;
                var task = ParseTaskFromLine(line, category, taskIndex);
                if (task != null)
                {
                    tasks.Add(task);
                }
            }
        }

        if (tasks.Count > 0)
            _taskStore.AddTasks(tasks);
    }

    /// <summary>
    /// AI-driven task decomposition using the lead agent
    /// </summary>
    private async Task AIDecomposeAsync(CancellationToken cancellationToken)
    {
        OnOutput?.Invoke("Using AI to decompose tasks...");

        var prompt = BuildDecompositionPrompt();
        var providerConfig = _teamConfig.LeadModel?.ToProviderConfig() ?? _config.ProviderConfig;

        // For decomposition we need plain text output and NO tool use.
        // The lead agent should only analyze the prompt/plan and output structured tasks.
        // Remove --dangerously-skip-permissions so Claude doesn't enter an agentic loop
        // editing files instead of just decomposing.
        var arguments = providerConfig.Arguments;
        if (providerConfig.UsesStreamJson)
        {
            arguments = arguments
                .Replace("--output-format stream-json", "--output-format text")
                .Replace("--verbose", "")
                .Replace("--include-partial-messages", "");
        }
        // Strip permission bypass and agentic flags — decomposition is analysis only
        arguments = arguments
            .Replace("--dangerously-skip-permissions", "")
            .Replace("--dangerously-bypass-approvals-and-sandbox", "")  // Codex equivalent
            .Replace("--allow-all-tools", "")  // Copilot equivalent
            .Replace("--auto-approve", "");  // Cursor equivalent
        // Add max-turns to prevent agentic looping (Claude CLI)
        if (providerConfig.Provider == AIProvider.Claude && !arguments.Contains("--max-turns"))
        {
            arguments += " --max-turns 1";
        }
        // Collapse multiple spaces from removed flags
        arguments = string.Join(' ', arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        OnOutput?.Invoke($"Launching: {providerConfig.ExecutablePath} {arguments}");

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
            psi.Arguments = $"{arguments} \"{escapedPrompt}\"";
        }
        else
        {
            psi.Arguments = arguments;
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Failed to launch decomposition process: {ex.Message}");
            OnOutput?.Invoke("Falling back to plan-based decomposition...");
            await LoadTasksFromPlanAsync(cancellationToken);
            return;
        }

        if (process == null)
        {
            OnError?.Invoke("Failed to start lead agent for decomposition");
            return;
        }

        using (process)
        {
            if (providerConfig.UsesStdin || !providerConfig.UsesPromptArgument)
            {
                await process.StandardInput.WriteAsync(prompt);
                process.StandardInput.Close();
            }

            OnOutput?.Invoke("Lead agent is analyzing the project (this may take 1-3 minutes)...");

            // Read stdout line by line so we can show progress on output size
            var outputBuilder = new StringBuilder();
            var stderrLines = new StringBuilder();
            var decomposeStart = DateTime.UtcNow;
            var stdoutChars = 0;

            // Stream stderr line by line for progress visibility
            var stderrTask = Task.Run(async () =>
            {
                try
                {
                    while (!process.StandardError.EndOfStream)
                    {
                        var line = await process.StandardError.ReadLineAsync(cancellationToken);
                        if (line == null) break;
                        stderrLines.AppendLine(line);
                        if (line.Length > 0)
                        {
                            var hint = line.Length > 120 ? line[..120] + "..." : line;
                            OnOutput?.Invoke($"  [decompose] {hint}");
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            }, cancellationToken);

            // Read stdout line by line to track progress
            var stdoutTask = Task.Run(async () =>
            {
                try
                {
                    while (!process.StandardOutput.EndOfStream)
                    {
                        var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                        if (line == null) break;
                        outputBuilder.AppendLine(line);
                        Interlocked.Add(ref stdoutChars, line.Length);
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            }, cancellationToken);

            // Heartbeat timer — show elapsed time every 15s while waiting
            using var heartbeat = new System.Timers.Timer(15_000);
            heartbeat.Elapsed += (_, _) =>
            {
                if (!process.HasExited)
                {
                    var elapsed = DateTime.UtcNow - decomposeStart;
                    var chars = Interlocked.CompareExchange(ref stdoutChars, 0, 0);
                    var charInfo = chars > 0 ? $", {chars:N0} chars received" : "";
                    OnOutput?.Invoke($"Still decomposing... ({elapsed.TotalSeconds:F0}s elapsed{charInfo})");
                }
            };
            heartbeat.Start();

            await process.WaitForExitAsync(cancellationToken);
            heartbeat.Stop();

            await stdoutTask;
            await stderrTask;
            var output = outputBuilder.ToString();
            var error = stderrLines.ToString();

            var totalElapsed = DateTime.UtcNow - decomposeStart;
            OnOutput?.Invoke($"Decomposition finished in {totalElapsed.TotalSeconds:F0}s ({output.Length:N0} chars)");

            OnOutput?.Invoke($"Decomposition process exited with code {process.ExitCode}");

            if (process.ExitCode != 0)
            {
                OnError?.Invoke($"Lead agent decomposition failed (exit {process.ExitCode}): {error}");
                OnOutput?.Invoke("Falling back to plan-based decomposition...");
                await LoadTasksFromPlanAsync(cancellationToken);
                return;
            }

            // Parse the structured output
            var tasks = ParseDecomposedTasks(output);
            if (tasks.Count == 0)
            {
                OnOutput?.Invoke("AI decomposition produced no tasks, falling back to plan...");
                if (output.Length > 0)
                {
                    var preview = output.Length > 300 ? output[..300] + "..." : output;
                    OnOutput?.Invoke($"AI output preview: {preview}");
                }
                await LoadTasksFromPlanAsync(cancellationToken);
                return;
            }

            _taskStore.AddTasks(tasks);
            OnOutput?.Invoke($"AI decomposed into {tasks.Count} tasks");
        }
    }

    /// <summary>
    /// Phase 2: Parallel Execution
    /// </summary>
    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        OnOutput?.Invoke($"Phase 2: Starting {_teamConfig.AgentCount} agents...");

        // Clean up stale worktrees from interrupted previous runs
        if (_teamConfig.UseWorktrees)
        {
            var worktreeBaseDir = Path.Combine(_config.TargetDirectory, ".ralph-worktrees");
            await _gitManager.CleanupStaleWorktreesAsync(worktreeBaseDir, cancellationToken);
            OnOutput?.Invoke("Cleaned up stale worktrees from previous run");
        }

        var agentTasks = new List<Task>();

        for (int i = 0; i < _teamConfig.AgentCount; i++)
        {
            var agentId = Guid.NewGuid().ToString("N")[..8];
            var assignedModel = _teamConfig.GetAgentModel(i);

            var agent = new TeamAgent(_config, _teamConfig, agentId, i, _gitManager, assignedModel);
            agent.SetMergeManager(_mergeManager);

            // Wire events
            agent.OnStateChanged += _ => OnAgentUpdate?.Invoke(agent.Statistics);
            agent.OnTaskStart += task =>
                OnOutput?.Invoke($"[{agent.Statistics.Name}] Starting: {task.Title ?? task.Description}");
            agent.OnTaskComplete += (task, result) =>
            {
                _taskStore.Complete(task.TaskId, result);
                OnQueueUpdate?.Invoke(_taskStore.GetStatistics());
                OnOutput?.Invoke($"[{agent.Statistics.Name}] Completed: {task.Title ?? task.Description}");
            };
            agent.OnTaskFailed += (task, error) =>
            {
                _taskStore.Fail(task.TaskId, error);
                OnQueueUpdate?.Invoke(_taskStore.GetStatistics());
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
                claimAgentId => _taskStore.TryClaim(claimAgentId),
                cancellationToken);
            agentTasks.Add(agentTask);
        }

        // Wait for all agents to complete
        await Task.WhenAll(agentTasks);

        var finalStats = _taskStore.GetStatistics();
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
    /// Delegates to shared PlanUpdater for consistent matching across TeamController and TeamOrchestrator.
    /// </summary>
    private TeamVerificationResult MarkCompletedTasksInPlan()
    {
        return PlanUpdater.MarkCompletedTasks(
            _config.PlanFilePath,
            _taskStore.GetAll().ToList(),
            msg => OnOutput?.Invoke(msg));
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

        sb.AppendLine(@"IMPORTANT: You are a TASK PLANNER. DO NOT modify any files. DO NOT use tools to edit code.
Your ONLY job is to analyze the project prompt and implementation plan above, then output a structured task list.

Break the work into independent, parallelizable subtasks. Output ONLY in this EXACT format (no other text before or after):

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
- Aim for " + _teamConfig.AgentCount + @" to " + (_teamConfig.AgentCount * 3) + @" tasks total
- Output ONLY the task list block above — do not read, edit, or create any files");

        return sb.ToString();
    }

    /// <summary>
    /// Parse structured task output from AI decomposition.
    /// Assigns stable sequential IDs (task-1, task-2, ...) and resolves
    /// title-based DEPENDS_ON references to task IDs.
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

        // First pass: create tasks with stable IDs, collect title→id mapping
        var titleToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawDeps = new List<List<string>>(); // title-based deps per task
        var taskIndex = 0;

        foreach (Match taskMatch in taskMatches)
        {
            taskIndex++;
            var taskId = $"task-{taskIndex}";
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

            var depTitles = dependsOnStr.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? new List<string>()
                : dependsOnStr.Split(',').Select(d => d.Trim()).Where(d => !string.IsNullOrEmpty(d)).ToList();

            titleToId[title] = taskId;
            rawDeps.Add(depTitles);

            tasks.Add(new AgentTask
            {
                TaskId = taskId,
                Title = title,
                Description = description,
                Priority = priority,
                Files = files,
                DependsOn = new List<string>() // Filled in second pass
            });
        }

        // Second pass: resolve title-based dependencies to task IDs
        for (int i = 0; i < tasks.Count; i++)
        {
            foreach (var depTitle in rawDeps[i])
            {
                if (titleToId.TryGetValue(depTitle, out var depId))
                {
                    tasks[i].DependsOn.Add(depId);
                }
                else
                {
                    // Try fuzzy match — the AI might not use the exact title
                    var fuzzyMatch = titleToId.Keys
                        .FirstOrDefault(k => k.Contains(depTitle, StringComparison.OrdinalIgnoreCase)
                            || depTitle.Contains(k, StringComparison.OrdinalIgnoreCase));
                    if (fuzzyMatch != null)
                    {
                        tasks[i].DependsOn.Add(titleToId[fuzzyMatch]);
                    }
                    else
                    {
                        OnOutput?.Invoke($"Warning: Could not resolve dependency '{depTitle}' for task '{tasks[i].Title}'");
                    }
                }
            }
        }

        return tasks;
    }

    private AgentTask? ParseTaskFromLine(string line, string category, int taskIndex)
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
            TaskId = $"task-{taskIndex}",
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
    public TaskStoreStatistics GetQueueStatistics() => _taskStore.GetStatistics();

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
        _currentPhase = phase;
        PhaseStartedAt = DateTime.UtcNow;
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

        _taskStore?.Dispose();
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
