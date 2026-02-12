using RalphController.Models;
using RalphController.Merge;
using RalphController.Parallel;
using RalphController.Git;
using RalphController.Messaging;
using System.Collections.Concurrent;
using System.Text;

namespace RalphController;

/// <summary>
/// Lead agent coordinator with continuous coordination loop.
/// Replaces 3-phase sequential model with: Decompose → Spawn → Coordinate → Synthesize → Merge
/// </summary>
public class TeamOrchestrator : IDisposable
{
    private readonly RalphConfig _config;
    private readonly TeamConfig _teamConfig;
    private readonly TaskStore _taskStore;
    private readonly GitWorktreeManager _gitManager;
    private readonly ConflictNegotiator _negotiator;
    private readonly MergeManager _mergeManager;
    private readonly ConcurrentDictionary<string, TeamAgent> _agents = new();
    private readonly ConcurrentDictionary<string, AgentMonitorInfo> _agentMonitor = new();
    private readonly SemaphoreSlim _mergeSemaphore;
    private MessageBus? _leadBus;
    private CancellationTokenSource? _stopCts;
    private bool _disposed;
    private volatile TeamOrchestratorState _state = TeamOrchestratorState.Idle;
    private readonly List<string> _agentFindings = new();
    private readonly Dictionary<string, DateTime> _agentStateTimestamps = new();

    public event Action<TeamOrchestratorState>? OnStateChanged;
    public event Action<string>? OnOutput;
    public event Action<string>? OnError;
    public event Action<AgentStatistics>? OnAgentUpdate;
    public event Action<TaskStoreStatistics>? OnQueueUpdate;


    public TeamOrchestrator(RalphConfig config)
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

        var mailboxDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ralph", "teams", teamName, "mailbox");
        _leadBus = MessageBus.CreateForLead(mailboxDir);

        _gitManager = new GitWorktreeManager(config.TargetDirectory);
        _negotiator = new ConflictNegotiator(config, config.ProviderConfig);
        _mergeManager = new MergeManager(_gitManager, _negotiator, _taskStore, _teamConfig);
        _mergeSemaphore = new SemaphoreSlim(_teamConfig.MaxConcurrentMerges);
    }

    public TeamOrchestratorState State => _state;
    public TaskStore TaskStore => _taskStore;
    public IReadOnlyDictionary<string, TeamAgent> Agents => _agents;
    public bool DelegateMode => _teamConfig.DelegateMode;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetState(TeamOrchestratorState.Initializing);

        if (_teamConfig.DelegateMode)
        {
            OnOutput?.Invoke("Running in DELEGATE MODE — lead coordinates only, no file edits");
        }

        try
        {
            var tasks = await DecomposeAsync(_stopCts.Token);
            if (tasks.Count == 0)
            {
                OnError?.Invoke("No tasks found after decomposition");
                SetState(TeamOrchestratorState.Failed);
                return;
            }

            _taskStore.AddTasks(tasks);
            OnQueueUpdate?.Invoke(_taskStore.GetStatistics());

            await SpawnAgentsAsync(_stopCts.Token);
            await CoordinateAsync(_stopCts.Token);
            await SynthesizeResultsAsync(_stopCts.Token);
            await MergeAndCleanupAsync(_stopCts.Token);

            SetState(TeamOrchestratorState.Complete);
            OnOutput?.Invoke("Teams execution complete!");
        }
        catch (OperationCanceledException)
        {
            SetState(TeamOrchestratorState.Stopped);
            OnOutput?.Invoke("Teams execution cancelled");
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Teams execution failed: {ex.Message}");
            SetState(TeamOrchestratorState.Failed);
        }
    }

    public async Task<IReadOnlyList<AgentTask>> DecomposeAsync(CancellationToken cancellationToken)
    {
        OnOutput?.Invoke("Decomposing tasks...");
        SetState(TeamOrchestratorState.Decomposing);

        var existingStats = _taskStore.GetStatistics();
        if (existingStats.Total > 0 && existingStats.Pending > 0)
        {
            OnOutput?.Invoke($"Restored {existingStats.Total} tasks from previous session");
            return _taskStore.GetAll().ToList();
        }

        var tasks = new List<AgentTask>();
        var planPath = _config.PlanFilePath;

        if (!File.Exists(planPath))
        {
            OnError?.Invoke($"Implementation plan not found: {planPath}");
            return tasks;
        }

        var lines = await File.ReadAllLinesAsync(planPath, cancellationToken);
        var taskIndex = 0;
        string? currentCategory = null;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            if (trimmedLine.StartsWith("## "))
            {
                currentCategory = trimmedLine[3..].Trim();
                continue;
            }

            if (!trimmedLine.StartsWith("- [ ]") && !trimmedLine.StartsWith("- [!]") && !trimmedLine.StartsWith("- [?]"))
            {
                continue;
            }

            taskIndex++;
            var task = ParseTaskFromLine(trimmedLine, currentCategory, taskIndex);
            if (task != null)
            {
                tasks.Add(task);
            }
        }

        OnOutput?.Invoke($"Decomposed into {tasks.Count} tasks");
        return tasks;
    }

    public async Task SpawnAgentsAsync(CancellationToken cancellationToken)
    {
        OnOutput?.Invoke($"Spawning {_teamConfig.AgentCount} agents...");
        SetState(TeamOrchestratorState.Spawning);

        // Clean up stale worktrees from interrupted previous runs
        if (_teamConfig.UseWorktrees)
        {
            var worktreeBaseDir = Path.Combine(_config.TargetDirectory, ".ralph-worktrees");
            await _gitManager.CleanupStaleWorktreesAsync(worktreeBaseDir, cancellationToken);
            OnOutput?.Invoke("Cleaned up stale worktrees from previous run");
        }

        var sourceBranch = _teamConfig.SourceBranch;
        if (string.IsNullOrEmpty(sourceBranch))
        {
            sourceBranch = await _gitManager.GetCurrentBranchAsync(cancellationToken);
        }

        var spawnTasks = new List<Task>();

        for (int i = 0; i < _teamConfig.AgentCount; i++)
        {
            var agentId = $"agent-{i + 1}";
            var agent = new TeamAgent(
                _config,
                _teamConfig,
                agentId,
                i,
                _gitManager,
                _teamConfig.GetAgentModel(i));

            agent.SetTaskStore(_taskStore);
            agent.SetMergeManager(_mergeManager);

            var mailboxDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ralph", "teams", _teamConfig.TeamName ?? "default", "mailbox");
            agent.SetMessageBus(new MessageBus(mailboxDir, agentId));

            agent.OnOutput += output => OnOutput?.Invoke($"[{agentId}] {output}");
            agent.OnError += error => OnError?.Invoke($"[{agentId}] {error}");
            agent.OnStateChanged += state =>
            {
                _agentStateTimestamps[agentId] = DateTime.UtcNow;
                OnAgentUpdate?.Invoke(agent.Statistics);
            };
            agent.OnIdle += a => RunHookAsync("TeammateIdle", a.AgentId);
            agent.OnTaskComplete += (task, _) => RunHookAsync("TaskCompleted", task.TaskId);

            _agents[agentId] = agent;
            _agentMonitor[agentId] = new AgentMonitorInfo { AgentId = agentId };
            _agentStateTimestamps[agentId] = DateTime.UtcNow;

            spawnTasks.Add(Task.Run(async () =>
            {
                var initialized = await agent.InitializeAsync(cancellationToken);
                if (!initialized)
                {
                    OnError?.Invoke($"Failed to initialize agent {agentId}");
                }
            }, cancellationToken));
        }

        await Task.WhenAll(spawnTasks);
        OnOutput?.Invoke("All agents spawned");
    }

    public async Task CoordinateAsync(CancellationToken cancellationToken)
    {
        OnOutput?.Invoke("Starting coordination loop...");
        SetState(TeamOrchestratorState.Coordinating);

        var agentTasks = _agents.Values
            .Select(a => Task.Run(() => a.RunLoopAsync(null, cancellationToken), cancellationToken))
            .ToList();

        while (!cancellationToken.IsCancellationRequested)
        {
            ProcessLeadMessages();

            if (await CheckForStuckAgentsAsync())
            {
                await HandleStuckAgentsAsync();
            }

            UpdateAgentMonitoring();

            if (AllTasksResolved() && AllAgentsIdleOrStopped())
            {
                OnOutput?.Invoke("All tasks resolved, all agents idle/stopped");
                break;
            }

            OnQueueUpdate?.Invoke(_taskStore.GetStatistics());
            await Task.Delay(1000, cancellationToken);
        }

        await Task.WhenAll(agentTasks);
    }

    private void ProcessLeadMessages()
    {
        if (_leadBus == null) return;

        var messages = _leadBus.Poll();
        foreach (var msg in messages)
        {
            switch (msg.Type)
            {
                case MessageType.StatusUpdate:
                    if (_agentMonitor.TryGetValue(msg.FromAgentId, out var monitor))
                    {
                        monitor.LastStatus = msg.Content;
                        monitor.LastMessageAt = DateTime.UtcNow;
                    }
                    break;

                case MessageType.PlanSubmission:
                    HandlePlanSubmission(msg);
                    break;

                case MessageType.ShutdownResponse:
                    OnOutput?.Invoke($"Agent {msg.FromAgentId} shutdown response: {msg.Content}");
                    break;

                case MessageType.Text:
                    _agentFindings.Add($"[{msg.FromAgentId}] {msg.Content}");
                    break;
            }
        }
    }

    private void HandlePlanSubmission(Message msg)
    {
        var taskId = msg.Metadata?.GetValueOrDefault("taskId", "");
        OnOutput?.Invoke($"Plan received from {msg.FromAgentId} for task {taskId}");

        var approved = EvaluatePlan(msg.Content, taskId ?? "");
        var feedback = approved ? "" : "Plan needs revision: ensure it addresses the task and touches only expected files.";

        _leadBus?.Send(Message.PlanApprovalMessage("lead", msg.FromAgentId, approved, feedback));
    }

    private bool EvaluatePlan(string plan, string taskId)
    {
        if (string.IsNullOrWhiteSpace(plan)) return false;
        if (plan.Length < 50) return false;

        var task = _taskStore.GetById(taskId);
        if (task == null) return true;

        var taskKeywords = task.Description.Split(' ')
            .Where(w => w.Length > 4)
            .Select(w => w.ToLower())
            .ToHashSet();

        var planLower = plan.ToLower();
        var keywordMatches = taskKeywords.Count(k => planLower.Contains(k));

        return keywordMatches >= 2 || plan.Length > 200;
    }

    private async Task<bool> CheckForStuckAgentsAsync()
    {
        var avgTaskTime = await GetAverageTaskTimeAsync();
        if (avgTaskTime == TimeSpan.Zero) return false;

        var stuckThreshold = TimeSpan.FromTicks(avgTaskTime.Ticks * 2);
        var now = DateTime.UtcNow;

        foreach (var (agentId, agent) in _agents)
        {
            if (agent.State != AgentState.Working) continue;

            if (!_agentStateTimestamps.TryGetValue(agentId, out var stateTime)) continue;
            var timeInState = now - stateTime;
            if (timeInState <= stuckThreshold) continue;

            // Also check if agent has sent messages recently (activity proxy)
            if (_agentMonitor.TryGetValue(agentId, out var monitor) &&
                (now - monitor.LastMessageAt) > stuckThreshold)
            {
                return true;
            }
        }

        return false;
    }

    private async Task HandleStuckAgentsAsync()
    {
        var avgTaskTime = await GetAverageTaskTimeAsync();
        if (avgTaskTime == TimeSpan.Zero) return;

        var stuckThreshold = TimeSpan.FromTicks(avgTaskTime.Ticks * 2);
        var now = DateTime.UtcNow;

        foreach (var (agentId, agent) in _agents)
        {
            if (agent.State != AgentState.Working || agent.CurrentTask == null) continue;
            if (!_agentStateTimestamps.TryGetValue(agentId, out var stateTime)) continue;

            var timeInState = now - stateTime;
            if (timeInState <= stuckThreshold) continue;

            // Check no recent messages from this agent
            if (_agentMonitor.TryGetValue(agentId, out var monitor) &&
                (now - monitor.LastMessageAt) <= stuckThreshold)
            {
                continue;  // Agent is communicating, not actually stuck
            }

            var taskId = agent.CurrentTask.TaskId;
            OnOutput?.Invoke($"Agent {agentId} appears stuck on task {taskId} ({timeInState.TotalSeconds:F0}s)");

            // Send a status check message to the stuck agent
            _leadBus?.Send(Message.TextMessage("lead", agentId, "Status check: are you still working on your current task?"));

            // Find an idle agent to reassign to
            var idleAgent = _agents.Values.FirstOrDefault(a =>
                a.AgentId != agentId &&
                a.State == AgentState.Idle);

            if (idleAgent != null)
            {
                OnOutput?.Invoke($"Reassigning task {taskId} from stuck agent {agentId} to idle agent {idleAgent.AgentId}");
                _taskStore.ReassignTask(taskId, null);  // Reset to Pending so idle agent can claim it
                _leadBus?.Send(Message.TaskAssignmentMessage("lead", idleAgent.AgentId, taskId,
                    agent.CurrentTask.Description));
            }
            else
            {
                OnOutput?.Invoke($"No idle agents available to reassign task {taskId}");
            }
        }
    }

    private Task<TimeSpan> GetAverageTaskTimeAsync()
    {
        var completedTasks = _taskStore.GetAll()
            .Where(t => t.Status == Models.TaskStatus.Completed && t.CompletedAt.HasValue && t.ClaimedAt.HasValue)
            .ToList();

        if (completedTasks.Count == 0) return Task.FromResult(TimeSpan.Zero);

        var avgTicks = (long)completedTasks
            .Average(t => (t.CompletedAt!.Value - t.ClaimedAt!.Value).Ticks);

        return Task.FromResult(TimeSpan.FromTicks(avgTicks));
    }

    private void UpdateAgentMonitoring()
    {
        foreach (var (agentId, agent) in _agents)
        {
            if (_agentMonitor.TryGetValue(agentId, out var monitor))
            {
                monitor.CurrentState = agent.State;
                monitor.CurrentTask = agent.CurrentTask?.TaskId;
                OnAgentUpdate?.Invoke(agent.Statistics);
            }
        }
    }

    private bool AllTasksResolved()
    {
        var stats = _taskStore.GetStatistics();
        return stats.Pending == 0 && stats.InProgress == 0;
    }

    private bool AllAgentsIdleOrStopped()
    {
        return _agents.Values.All(a =>
            a.State == AgentState.Idle ||
            a.State == AgentState.Stopped ||
            a.State == AgentState.ShuttingDown);
    }

    public async Task SynthesizeResultsAsync(CancellationToken cancellationToken)
    {
        OnOutput?.Invoke("Synthesizing results...");
        SetState(TeamOrchestratorState.Synthesizing);

        var results = new StringBuilder();
        results.AppendLine("# Teams Execution Results");
        results.AppendLine();

        var stats = _taskStore.GetStatistics();
        results.AppendLine($"## Summary");
        results.AppendLine($"- Total tasks: {stats.Total}");
        results.AppendLine($"- Completed: {stats.Completed}");
        results.AppendLine($"- Failed: {stats.Failed}");
        results.AppendLine();

        results.AppendLine("## Task Details");
        foreach (var task in _taskStore.GetAll())
        {
            var status = task.Status.ToString();
            var agent = task.ClaimedByAgentId ?? "unassigned";
            results.AppendLine($"- [{status}] {task.Title ?? task.TaskId} (by {agent})");
        }

        if (_agentFindings.Count > 0)
        {
            results.AppendLine();
            results.AppendLine("## Agent Findings");
            foreach (var finding in _agentFindings)
            {
                results.AppendLine($"- {finding}");
            }
        }

        OnOutput?.Invoke(results.ToString());

        // Mark completed tasks in the implementation plan
        OnOutput?.Invoke("Marking completed tasks in implementation plan...");
        var verification = PlanUpdater.MarkCompletedTasks(
            _config.PlanFilePath,
            _taskStore.GetAll().ToList(),
            msg => OnOutput?.Invoke(msg));

        if (verification.AllTasksComplete)
        {
            OnOutput?.Invoke($"All {verification.TasksMarked} tasks marked complete in plan");
        }
        else
        {
            OnOutput?.Invoke($"Marked {verification.TasksMarked} tasks complete, {verification.IncompleteTasks.Count} incomplete");
        }
    }

    public async Task MergeAndCleanupAsync(CancellationToken cancellationToken)
    {
        OnOutput?.Invoke("Merging and cleaning up...");
        SetState(TeamOrchestratorState.Merging);

        foreach (var agent in _agents.Values)
        {
            try
            {
                var result = await agent.MergeAsync(cancellationToken);
                if (result.Success)
                {
                    OnOutput?.Invoke($"[{agent.AgentId}] Merge successful");
                }
                else if (result.Conflicts?.Count > 0)
                {
                    OnOutput?.Invoke($"[{agent.AgentId}] Conflicts detected: {result.Conflicts.Count}");
                }

                await agent.CleanupAsync();
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"[{agent.AgentId}] Merge failed: {ex.Message}");
            }
        }

        _taskStore.DeletePersistenceFiles();
    }

    public void AddTask(AgentTask task) => _taskStore.AddTask(task);

    public void ReassignTask(string taskId, string newAgentId)
    {
        _taskStore.ReassignTask(taskId, newAgentId);
        OnOutput?.Invoke($"Task {taskId} reassigned to {newAgentId}");
    }

    public void CancelTask(string taskId)
    {
        _taskStore.CancelTask(taskId);
        OnOutput?.Invoke($"Task {taskId} cancelled");
    }

    public void RequestShutdown(string agentId)
    {
        if (_agents.TryGetValue(agentId, out var agent))
        {
            agent.RequestShutdown();
            OnOutput?.Invoke($"Shutdown requested for {agentId}");
        }
    }

    public async Task ShutdownAll()
    {
        foreach (var agent in _agents.Values)
        {
            agent.RequestShutdown();
        }

        await Task.WhenAll(_agents.Values.Select(a => Task.Run(() => a.Dispose())));
    }

    private AgentTask? ParseTaskFromLine(string line, string? category, int taskIndex)
    {
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
    /// Get the delegate mode coordinator instructions for the lead's AI prompt.
    /// When delegate mode is on, the lead should not edit files or run build commands.
    /// </summary>
    public string? GetDelegateModeInstructions()
    {
        if (!_teamConfig.DelegateMode) return null;

        return """
            --- DELEGATE MODE ---
            You are a COORDINATOR. You must NOT:
            - Edit, create, or delete any files
            - Run build, test, or shell commands
            - Make direct code changes

            You CAN:
            - Spawn and shut down agents
            - Send and receive messages
            - Create, assign, reassign, and cancel tasks
            - Review and approve agent plans
            - Synthesize findings into reports
            - Provide guidance and feedback to agents

            All implementation work must be delegated to your team agents.
            """;
    }

    /// <summary>
    /// Run a hook command if configured for the given event.
    /// Hooks are shell commands defined in TeamConfig.Hooks.
    /// </summary>
    private void RunHookAsync(string hookName, string context)
    {
        if (!_teamConfig.Hooks.TryGetValue(hookName, out var command))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    WorkingDirectory = _config.TargetDirectory
                };
                psi.Environment["RALPH_HOOK"] = hookName;
                psi.Environment["RALPH_CONTEXT"] = context;
                psi.Environment["RALPH_TEAM"] = _teamConfig.TeamName;

                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return;

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (process.ExitCode != 0)
                {
                    OnError?.Invoke($"Hook '{hookName}' failed (exit {process.ExitCode}): {stderr}");
                }
                else if (!string.IsNullOrWhiteSpace(stdout))
                {
                    OnOutput?.Invoke($"Hook '{hookName}': {stdout.Trim()}");
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Hook '{hookName}' error: {ex.Message}");
            }
        });
    }

    private void SetState(TeamOrchestratorState newState)
    {
        if (_state != newState)
        {
            _state = newState;
            OnStateChanged?.Invoke(newState);
        }
    }

    public void Stop()
    {
        _stopCts?.Cancel();
        foreach (var agent in _agents.Values)
        {
            agent.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stopCts?.Cancel();
        _stopCts?.Dispose();
        _leadBus?.Dispose();
        _mergeSemaphore?.Dispose();

        foreach (var agent in _agents.Values)
        {
            agent.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}

public enum TeamOrchestratorState
{
    Idle,
    Initializing,
    Decomposing,
    Spawning,
    Coordinating,
    Synthesizing,
    Merging,
    Complete,
    Stopped,
    Failed
}

internal class AgentMonitorInfo
{
    public string AgentId { get; init; } = "";
    public AgentState CurrentState { get; set; }
    public string? CurrentTask { get; set; }
    public string? LastStatus { get; set; }
    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;
}
