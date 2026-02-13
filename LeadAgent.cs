using RalphController.Models;
using RalphController.Git;
using RalphController.Merge;
using RalphController.Parallel;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace RalphController;

/// <summary>
/// AI-driven Lead Agent (Tier 1 in lead-driven mode).
/// Decision loop: build prompt with project state → run AI CLI → parse LeadDecision → execute.
/// Creates ephemeral TaskAgents (up to AgentCount concurrently), evaluates results, merges worktrees.
/// </summary>
public class LeadAgent : IDisposable
{
    private readonly RalphConfig _config;
    private readonly TeamConfig _teamConfig;
    private readonly TaskStore _taskStore;
    private readonly GitWorktreeManager _gitManager;
    private readonly MergeManager _mergeManager;
    private readonly ModelSpec? _leadModel;
    private readonly SemaphoreSlim _mergeLock = new(1, 1);
    private bool _disposed;
    private int _consecutiveParseFailures;
    private const int MaxParseFailures = 3;

    // Track running TaskAgents and their background tasks
    private readonly ConcurrentDictionary<string, RunningTaskAgent> _runningAgents = new();
    private int _agentModelIndex;

    // Backoff: if agents fail very quickly, throttle spawning to prevent churn
    private DateTime _lastAgentFailure = DateTime.MinValue;
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(3);

    /// <summary>Current state of the lead agent</summary>
    public AgentState State { get; private set; } = AgentState.Idle;

    /// <summary>Statistics for TUI display</summary>
    public AgentStatistics Statistics { get; }

    /// <summary>Max concurrent TaskAgents (from AgentCount)</summary>
    public int MaxConcurrent => _teamConfig.AgentCount;

    /// <summary>Number of currently running TaskAgents</summary>
    public int RunningCount => _runningAgents.Count;

    // Events
    public event Action<string>? OnOutput;
    public event Action<string>? OnError;
    public event Action<LeadDecision>? OnDecision;
    public event Action<TaskAgent>? OnTaskAgentCreated;
    public event Action<TaskAgent>? OnTaskAgentDestroyed;
    public event Action<TaskStoreStatistics>? OnQueueUpdate;
    public event Action<AgentStatistics>? OnUpdate;

    public LeadAgent(
        RalphConfig config,
        TeamConfig teamConfig,
        TaskStore taskStore,
        GitWorktreeManager gitManager,
        MergeManager mergeManager)
    {
        _config = config;
        _teamConfig = teamConfig;
        _taskStore = taskStore;
        _gitManager = gitManager;
        _mergeManager = mergeManager;
        _leadModel = teamConfig.LeadModel;

        var modelLabel = _leadModel?.DisplayName ?? config.Provider.ToString();
        Statistics = new AgentStatistics
        {
            AgentId = "lead",
            Name = $"Lead [{modelLabel}]",
            State = AgentState.Spawning,
            AssignedModel = _leadModel
        };
    }

    /// <summary>
    /// Main decision loop. Runs until all tasks are done or cancelled.
    /// Launches up to AgentCount TaskAgents concurrently.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        SetState(AgentState.Ready);
        OnOutput?.Invoke($"Lead agent starting decision loop (max {MaxConcurrent} concurrent agents)...");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Reap any completed agents first
                await ReapCompletedAgentsAsync(cancellationToken);

                // Backoff after rapid failures to prevent agent churn
                var timeSinceFailure = DateTime.UtcNow - _lastAgentFailure;
                if (timeSinceFailure < FailureBackoff)
                {
                    var waitTime = FailureBackoff - timeSinceFailure;
                    OnOutput?.Invoke($"Backoff: waiting {waitTime.TotalSeconds:F0}s after agent failure...");
                    await Task.Delay(waitTime, cancellationToken);
                }

                var stats = _taskStore.GetStatistics();
                OnQueueUpdate?.Invoke(stats);

                // Check if we're done: no pending tasks, nothing running
                if (stats.Pending == 0 && stats.InProgress == 0 && _runningAgents.IsEmpty)
                {
                    OnOutput?.Invoke("All tasks resolved. Declaring complete.");
                    break;
                }

                // If all slots full or no claimable tasks, wait for an agent to finish
                if (_runningAgents.Count >= MaxConcurrent || (stats.Pending == 0 && _runningAgents.Count > 0))
                {
                    SetState(AgentState.Idle);
                    var runningIds = string.Join(", ", _runningAgents.Keys);
                    OnOutput?.Invoke($"All {_runningAgents.Count} agent slots active ({runningIds}), waiting for completion...");
                    await WaitForAnyAgentCompletionAsync(cancellationToken);
                    continue;
                }

                // Fast-path: if there are claimable tasks and free slots, skip the AI
                // entirely and just pick the highest-priority pending task.
                // Only consult the AI for complex decisions (retries, skips, all tasks done).
                var claimable = _taskStore.GetClaimable();
                LeadDecision? decision;

                if (claimable.Count > 0)
                {
                    var next = claimable.OrderBy(t => t.Priority).First();
                    decision = new LeadDecision
                    {
                        Action = LeadAction.NextTask,
                        TaskId = next.TaskId,
                        Reason = $"Next highest-priority pending task"
                    };
                    OnOutput?.Invoke($"Fast-assigning task {next.TaskId}: {next.Title ?? next.Description}");
                    _consecutiveParseFailures = 0;
                }
                else if (stats.Failed > 0)
                {
                    // Complex decision needed (retries, skips) — ask the AI
                    SetState(AgentState.Deciding);
                    OnOutput?.Invoke($"Analyzing project state ({stats.Pending} pending, {stats.Completed} done, {stats.Failed} failed, {_runningAgents.Count} running)...");
                    decision = await GetNextDecisionAsync(cancellationToken);

                    if (decision == null)
                    {
                        _consecutiveParseFailures++;
                        if (_consecutiveParseFailures >= MaxParseFailures)
                        {
                            OnOutput?.Invoke($"Failed to parse decision {MaxParseFailures}x, falling back to sequential");
                            decision = CreateFallbackDecision();
                        }
                        else
                        {
                            OnError?.Invoke("Failed to parse lead decision, retrying...");
                            continue;
                        }
                    }
                    else
                    {
                        _consecutiveParseFailures = 0;
                    }
                }
                else
                {
                    // No claimable, no failed — agents still running, wait
                    if (_runningAgents.Count > 0)
                    {
                        OnOutput?.Invoke($"No pending tasks, waiting for {_runningAgents.Count} running agent(s)...");
                        await WaitForAnyAgentCompletionAsync(cancellationToken);
                        continue;
                    }
                    // Nothing left — declare complete
                    decision = new LeadDecision
                    {
                        Action = LeadAction.DeclareComplete,
                        Reason = "All tasks resolved"
                    };
                }

                if (decision == null)
                {
                    // No claimable tasks but agents still running — wait
                    if (_runningAgents.Count > 0)
                    {
                        await WaitForAnyAgentCompletionAsync(cancellationToken);
                    }
                    continue;
                }

                OnDecision?.Invoke(decision);
                OnOutput?.Invoke($"Decision: {decision.Action} {decision.TaskId ?? ""} — {decision.Reason ?? ""}");

                await ExecuteDecisionAsync(decision, cancellationToken);
            }

            // Wait for all remaining agents to finish
            if (!_runningAgents.IsEmpty)
            {
                OnOutput?.Invoke($"Waiting for {_runningAgents.Count} running agent(s) to finish...");
                await WaitForAllAgentsAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            OnOutput?.Invoke("Lead agent cancelled");
        }
        finally
        {
            // Clean up any remaining agents
            foreach (var running in _runningAgents.Values)
            {
                running.TaskAgent.Dispose();
            }
            _runningAgents.Clear();
            SetState(AgentState.Stopped);
        }
    }

    // --- Agent lifecycle ---

    /// <summary>
    /// Check for and process completed agents (merge, cleanup, report).
    /// </summary>
    private async Task ReapCompletedAgentsAsync(CancellationToken ct)
    {
        var completedIds = _runningAgents
            .Where(kvp => kvp.Value.BackgroundTask.IsCompleted)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in completedIds)
        {
            if (!_runningAgents.TryRemove(id, out var running))
                continue;

            try
            {
                // Get the result (task is already completed, this won't block)
                var result = await running.BackgroundTask;
                await HandleTaskAgentCompletion(running.TaskAgent, running.TaskId, result, ct);

                // Track fast failures for backoff
                if (!result.Success)
                    _lastAgentFailure = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Error reaping agent {id}: {ex.Message}");
                _taskStore.Fail(running.TaskId, ex.Message);
                _lastAgentFailure = DateTime.UtcNow;
            }
            finally
            {
                OnTaskAgentDestroyed?.Invoke(running.TaskAgent);
                running.TaskAgent.Dispose();
                OnQueueUpdate?.Invoke(_taskStore.GetStatistics());
            }
        }
    }

    /// <summary>
    /// Wait until at least one running agent completes.
    /// </summary>
    private async Task WaitForAnyAgentCompletionAsync(CancellationToken ct)
    {
        if (_runningAgents.IsEmpty) return;

        var tasks = _runningAgents.Values.Select(r => r.BackgroundTask).ToArray();
        await Task.WhenAny(tasks).WaitAsync(ct);

        // Now reap the completed ones
        await ReapCompletedAgentsAsync(ct);
    }

    /// <summary>
    /// Wait for all running agents to complete.
    /// </summary>
    private async Task WaitForAllAgentsAsync(CancellationToken ct)
    {
        while (!_runningAgents.IsEmpty)
        {
            await WaitForAnyAgentCompletionAsync(ct);
        }
    }

    /// <summary>
    /// Handle a completed TaskAgent: evaluate, merge, report.
    /// </summary>
    private async Task HandleTaskAgentCompletion(
        TaskAgent taskAgent, string taskId, TaskAgentResult result, CancellationToken ct)
    {
        OnOutput?.Invoke($"[{taskAgent.AgentId}] Completed: {result.Summary}");

        if (result.Success)
        {
            // Merge worktree branch back (serialize merges to avoid conflicts)
            OnOutput?.Invoke($"[{taskAgent.AgentId}] Merging branch {taskAgent.BranchName}...");

            // Show both lead and task agent as merging in TUI
            SetState(AgentState.MergingWork);
            taskAgent.Statistics.State = AgentState.MergingWork;
            OnUpdate?.Invoke(taskAgent.Statistics);

            await _mergeLock.WaitAsync(ct);
            try
            {
                var mergeResult = await MergeBranchAsync(taskAgent, ct);
                if (mergeResult.Success)
                {
                    _taskStore.Complete(taskId, new TaskResult(
                        true,
                        result.Summary,
                        result.AllFilesModified,
                        result.Code?.Output ?? "",
                        Statistics.TotalDuration));
                    Statistics.TasksCompleted++;
                    OnOutput?.Invoke($"Task {taskId} completed and merged successfully");
                }
                else
                {
                    // Try AI merge-fix agent before giving up
                    OnOutput?.Invoke($"Merge failed for {taskId}, attempting AI merge-fix agent...");
                    var task = taskAgent.Task;
                    var fixAgent = new MergeFixAgent(_config, _teamConfig);
                    fixAgent.OnOutput += output => OnOutput?.Invoke(output);
                    fixAgent.OnError += error => OnError?.Invoke(error);

                    var resolved = await fixAgent.ResolveAsync(
                        _gitManager.RepositoryRoot,
                        mergeResult.Conflicts ?? new(),
                        mergeResult.Error,
                        task.Description,
                        ct);

                    Statistics.AITime += fixAgent.LastDuration;

                    if (resolved)
                    {
                        // Commit the resolution and mark success
                        await _gitManager.CommitWorktreeAsync(
                            _gitManager.RepositoryRoot,
                            $"[merge-fix] {task.Title ?? task.Description}",
                            ct);
                        _taskStore.Complete(taskId, new TaskResult(
                            true,
                            $"{result.Summary} (merge conflicts resolved by AI)",
                            result.AllFilesModified,
                            result.Code?.Output ?? "",
                            Statistics.TotalDuration));
                        Statistics.TasksCompleted++;
                        OnOutput?.Invoke($"Task {taskId} merge conflicts resolved and committed");
                    }
                    else
                    {
                        _taskStore.Fail(taskId, $"Merge failed: {mergeResult.Error}");
                        Statistics.TasksFailed++;
                        OnError?.Invoke($"Merge failed for {taskId}: {mergeResult.Error}");
                    }
                }
            }
            finally
            {
                _mergeLock.Release();
                SetState(AgentState.Idle);
            }
        }
        else
        {
            _taskStore.Fail(taskId, result.Summary);
            Statistics.TasksFailed++;
            OnError?.Invoke($"Task {taskId} failed: {result.Summary}");
        }

        // Cleanup worktree
        await taskAgent.CleanupAsync();
        OnUpdate?.Invoke(Statistics);
    }

    // --- Decision making ---

    private async Task<LeadDecision?> GetNextDecisionAsync(CancellationToken ct)
    {
        OnOutput?.Invoke("Running AI for decision on failed tasks...");

        var prompt = BuildDecisionPrompt();
        var providerConfig = GetProviderConfig();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_teamConfig.LeadDecisionTimeoutSeconds));

        AgentProcessResult result;
        try
        {
            result = await AIProcessRunner.RunAsync(
                providerConfig,
                prompt,
                _config.TargetDirectory,
                output =>
                {
                    Statistics.LastActivityAt = DateTime.UtcNow;
                    OnOutput?.Invoke(output);
                },
                timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout — not the outer cancel. Don't count as parse failure.
            OnError?.Invoke($"Lead AI timed out after {_teamConfig.LeadDecisionTimeoutSeconds}s, using fallback");
            return CreateFallbackDecision();
        }

        Statistics.OutputChars += result.OutputChars;
        Statistics.AITime += result.Duration;
        Statistics.Iterations++;
        Statistics.LastActivityAt = DateTime.UtcNow;
        OnUpdate?.Invoke(Statistics);

        if (!result.Success)
        {
            OnError?.Invoke($"Lead AI process failed: {result.Error}");
            return null;
        }

        var text = result.ParsedText.Length > 0 ? result.ParsedText : result.Output;
        return ParseDecision(text);
    }

    private LeadDecision? CreateFallbackDecision()
    {
        var claimable = _taskStore.GetClaimable();
        if (claimable.Count == 0)
        {
            var stats = _taskStore.GetStatistics();
            if (stats.Pending == 0 && stats.InProgress == 0 && _runningAgents.IsEmpty)
            {
                return new LeadDecision
                {
                    Action = LeadAction.DeclareComplete,
                    Reason = "All tasks resolved (fallback)"
                };
            }
            return null; // Agents still running, wait
        }

        var next = claimable
            .OrderBy(t => t.Priority)
            .First();

        return new LeadDecision
        {
            Action = LeadAction.NextTask,
            TaskId = next.TaskId,
            Reason = "Sequential fallback (AI parse failures)"
        };
    }

    // --- Decision execution ---

    private async Task ExecuteDecisionAsync(LeadDecision decision, CancellationToken ct)
    {
        switch (decision.Action)
        {
            case LeadAction.NextTask:
                LaunchTaskAgent(decision.TaskId!, ct);
                break;

            case LeadAction.RetryTask:
                RetryAndLaunchTaskAgent(decision.TaskId!, ct);
                break;

            case LeadAction.AddTask:
                ExecuteAddTask(decision);
                break;

            case LeadAction.SkipTask:
                ExecuteSkipTask(decision.TaskId!);
                break;

            case LeadAction.DeclareComplete:
                OnOutput?.Invoke("Lead declares all work complete.");
                break;
        }

        // Small yield to let background agents make progress
        await Task.Yield();
    }

    /// <summary>
    /// Launch a TaskAgent in the background. Does NOT await completion.
    /// The agent runs its 3-phase cycle while the lead continues deciding.
    /// </summary>
    private void LaunchTaskAgent(string taskId, CancellationToken ct)
    {
        var task = _taskStore.GetById(taskId);
        if (task == null)
        {
            OnError?.Invoke($"Task {taskId} not found");
            return;
        }

        OnOutput?.Invoke($"Launching agent for task {taskId}: {task.Title ?? task.Description}");

        // Claim the task
        _taskStore.TryClaimTask(taskId, "lead");
        OnQueueUpdate?.Invoke(_taskStore.GetStatistics());

        // Create TaskAgent with round-robin model assignment
        var model = _teamConfig.GetAgentModel(_agentModelIndex);
        _agentModelIndex = (_agentModelIndex + 1) % Math.Max(1, _teamConfig.AgentCount);

        var taskAgent = new TaskAgent(
            _config,
            _teamConfig,
            task,
            _gitManager,
            model);

        WireTaskAgentEvents(taskAgent);
        OnTaskAgentCreated?.Invoke(taskAgent);

        // Launch in background
        var backgroundTask = Task.Run(async () =>
        {
            var initialized = await taskAgent.InitializeAsync(ct);
            if (!initialized)
            {
                OnError?.Invoke($"Failed to initialize TaskAgent for {taskId}");
                return new TaskAgentResult
                {
                    Success = false,
                    BranchName = taskAgent.BranchName,
                    Summary = "Worktree initialization failed"
                };
            }

            return await taskAgent.RunAsync(ct);
        }, ct);

        _runningAgents[taskId] = new RunningTaskAgent(taskAgent, taskId, backgroundTask);
        OnOutput?.Invoke($"Agent {taskAgent.AgentId} running ({_runningAgents.Count}/{MaxConcurrent} slots used)");
    }

    private void RetryAndLaunchTaskAgent(string taskId, CancellationToken ct)
    {
        var task = _taskStore.GetById(taskId);
        if (task == null)
        {
            OnError?.Invoke($"Task {taskId} not found for retry");
            return;
        }

        if (task.RetryCount >= task.MaxRetries)
        {
            OnOutput?.Invoke($"Task {taskId} has exceeded max retries ({task.MaxRetries})");
            return;
        }

        _taskStore.ReassignTask(taskId, null);
        task.RetryCount++;
        OnOutput?.Invoke($"Retrying task {taskId} (attempt {task.RetryCount})");

        LaunchTaskAgent(taskId, ct);
    }

    private void ExecuteAddTask(LeadDecision decision)
    {
        var newTask = new AgentTask
        {
            TaskId = $"task-{_taskStore.GetAll().Count + 1}",
            Title = decision.NewTaskTitle ?? decision.NewTaskDescription?[..Math.Min(60, decision.NewTaskDescription.Length)],
            Description = decision.NewTaskDescription ?? "",
            Priority = decision.NewTaskPriority ?? TaskPriority.Normal
        };

        _taskStore.AddTask(newTask);
        OnOutput?.Invoke($"Added new task: {newTask.TaskId} — {newTask.Title}");
        OnQueueUpdate?.Invoke(_taskStore.GetStatistics());
    }

    private void ExecuteSkipTask(string taskId)
    {
        _taskStore.Complete(taskId, new TaskResult(true, "Skipped by lead", new List<string>()));
        OnOutput?.Invoke($"Task {taskId} skipped");
        OnQueueUpdate?.Invoke(_taskStore.GetStatistics());
    }

    // --- Merge ---

    private async Task<MergeResult> MergeBranchAsync(TaskAgent taskAgent, CancellationToken ct)
    {
        var targetBranch = _teamConfig.TargetBranch;
        if (string.IsNullOrEmpty(targetBranch))
        {
            targetBranch = await _gitManager.GetCurrentBranchAsync(ct);
        }

        // Rebase the worktree branch onto the latest target before merging.
        // This brings in any work already merged by other agents, reducing conflicts.
        if (_teamConfig.UseWorktrees)
        {
            OnOutput?.Invoke($"[{taskAgent.AgentId}] Rebasing {taskAgent.BranchName} onto {targetBranch}...");
            var rebaseResult = await _gitManager.RunGitCommandAsync(
                taskAgent.WorktreePath,
                $"rebase {targetBranch}",
                ct);

            if (rebaseResult.ExitCode != 0)
            {
                OnOutput?.Invoke($"[{taskAgent.AgentId}] Rebase failed, aborting rebase and proceeding to merge...");
                await _gitManager.RunGitCommandAsync(taskAgent.WorktreePath, "rebase --abort", ct);
            }
        }

        var task = taskAgent.Task;
        var commitMessage = task.Title ?? task.Description;

        return _teamConfig.MergeStrategy switch
        {
            MergeStrategy.MergeDirect => await _mergeManager.MergeDirectAsync(
                taskAgent.WorktreePath, taskAgent.BranchName, targetBranch, ct, commitMessage),
            _ => await _mergeManager.RebaseAndMergeAsync(
                taskAgent.WorktreePath, taskAgent.BranchName, targetBranch, ct, commitMessage)
        };
    }

    // --- Prompt building ---

    private string BuildDecisionPrompt()
    {
        var sb = new StringBuilder();

        sb.AppendLine("--- LEAD AGENT DECISION MODE ---");
        sb.AppendLine("You are the LEAD AGENT coordinating a team of AI coding agents.");
        sb.AppendLine($"You can run up to {MaxConcurrent} agents concurrently.");
        sb.AppendLine("Your job is to decide what to do next based on the current project state.");
        sb.AppendLine();

        // Project context
        var promptContent = AIProcessRunner.TryReadFile(
            _config.PromptFilePath, null);
        if (!string.IsNullOrEmpty(promptContent))
        {
            sb.AppendLine("--- PROJECT CONTEXT ---");
            sb.AppendLine(AIProcessRunner.StripRalphStatusBlock(promptContent));
            sb.AppendLine();
        }

        // Currently running agents
        if (!_runningAgents.IsEmpty)
        {
            sb.AppendLine("--- CURRENTLY RUNNING AGENTS ---");
            foreach (var (taskId, running) in _runningAgents)
            {
                var phase = running.TaskAgent.CurrentPhase;
                var phaseStr = phase != SubAgentPhase.None ? $" (phase: {phase})" : "";
                sb.AppendLine($"  {running.TaskAgent.AgentId}: task {taskId}{phaseStr}");
            }
            sb.AppendLine($"  Slots used: {_runningAgents.Count}/{MaxConcurrent}");
            sb.AppendLine();
        }

        // Task summary (don't list every task — too much context for the AI)
        var allTasks = _taskStore.GetAll();
        var stats = _taskStore.GetStatistics();
        sb.AppendLine("--- TASK SUMMARY ---");
        sb.AppendLine($"Total: {stats.Total} | Pending: {stats.Pending} | InProgress: {stats.InProgress} | Completed: {stats.Completed} | Failed: {stats.Failed}");
        sb.AppendLine();

        // Only list failed tasks eligible for retry (this is why the AI is being consulted)
        var retryable = allTasks
            .Where(t => t.Status == Models.TaskStatus.Failed && t.RetryCount < t.MaxRetries)
            .ToList();
        if (retryable.Count > 0)
        {
            sb.AppendLine("FAILED TASKS (eligible for retry):");
            foreach (var task in retryable.Take(20))
            {
                sb.AppendLine($"  {task.TaskId}: {task.Title ?? task.Description} — Error: {Truncate(task.Error ?? "unknown", 100)}");
            }
            if (retryable.Count > 20)
                sb.AppendLine($"  ... and {retryable.Count - 20} more");
            sb.AppendLine();
        }

        // List permanently failed tasks (exceeded retries)
        var permFailed = allTasks
            .Where(t => t.Status == Models.TaskStatus.Failed && t.RetryCount >= t.MaxRetries)
            .ToList();
        if (permFailed.Count > 0)
        {
            sb.AppendLine("PERMANENTLY FAILED (max retries exceeded):");
            foreach (var task in permFailed.Take(10))
            {
                sb.AppendLine($"  {task.TaskId}: {task.Title ?? task.Description}");
            }
            if (permFailed.Count > 10)
                sb.AppendLine($"  ... and {permFailed.Count - 10} more");
            sb.AppendLine();
        }

        // Decision format
        sb.AppendLine("--- DECISION FORMAT ---");
        sb.AppendLine("You are being consulted because there are failed tasks that may need retry or skip decisions.");
        sb.AppendLine("You MUST output your decision in this exact format:");
        sb.AppendLine();
        sb.AppendLine("---LEAD_DECISION---");
        sb.AppendLine("ACTION: retry_task | skip_task | declare_complete");
        sb.AppendLine("TASK_ID: <task id, required for retry_task/skip_task>");
        sb.AppendLine("REASON: <brief explanation>");
        sb.AppendLine("---END_DECISION---");
        sb.AppendLine();
        sb.AppendLine("Choose retry_task to retry a failed task, skip_task to skip it, or declare_complete if all remaining failures are acceptable.");
        sb.AppendLine("Output exactly ONE decision block. Be concise — do not list tasks or analyze at length.");

        return sb.ToString();
    }

    // --- Decision parsing ---

    internal static LeadDecision? ParseDecision(string output)
    {
        var match = Regex.Match(
            output,
            @"---LEAD_DECISION---(.*?)---END_DECISION---",
            RegexOptions.Singleline);

        if (!match.Success)
            return null;

        var block = match.Groups[1].Value;
        var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;
            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 1)..].Trim();
            fields[key] = value;
        }

        if (!fields.TryGetValue("ACTION", out var actionStr))
            return null;

        var action = actionStr.ToLower().Replace("-", "_") switch
        {
            "next_task" => LeadAction.NextTask,
            "retry_task" => LeadAction.RetryTask,
            "add_task" => LeadAction.AddTask,
            "skip_task" => LeadAction.SkipTask,
            "declare_complete" => LeadAction.DeclareComplete,
            _ => (LeadAction?)null
        };

        if (action == null)
            return null;

        fields.TryGetValue("TASK_ID", out var taskId);
        fields.TryGetValue("REASON", out var reason);
        fields.TryGetValue("NEW_TASK_TITLE", out var newTitle);
        fields.TryGetValue("NEW_TASK_DESCRIPTION", out var newDesc);

        return new LeadDecision
        {
            Action = action.Value,
            TaskId = taskId,
            Reason = reason,
            NewTaskTitle = newTitle,
            NewTaskDescription = newDesc
        };
    }

    // --- Helpers ---

    private AIProviderConfig GetProviderConfig()
    {
        if (_leadModel != null)
        {
            return _leadModel.ToProviderConfig();
        }
        return _config.ProviderConfig;
    }

    private void SetState(AgentState state)
    {
        State = state;
        Statistics.State = state;
        OnUpdate?.Invoke(Statistics);
    }

    private void WireTaskAgentEvents(TaskAgent taskAgent)
    {
        taskAgent.OnOutput += output => OnOutput?.Invoke($"[{taskAgent.AgentId}] {output}");
        taskAgent.OnError += error => OnError?.Invoke($"[{taskAgent.AgentId}] {error}");
        taskAgent.OnUpdate += stats => OnUpdate?.Invoke(stats);
        taskAgent.OnPhaseChanged += phase =>
        {
            OnOutput?.Invoke($"[{taskAgent.AgentId}] Phase: {phase}");
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var running in _runningAgents.Values)
        {
            running.TaskAgent.Dispose();
        }
        _runningAgents.Clear();
        _mergeLock.Dispose();

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Tracks a TaskAgent running in the background.
    /// </summary>
    private record RunningTaskAgent(TaskAgent TaskAgent, string TaskId, Task<TaskAgentResult> BackgroundTask);
}
