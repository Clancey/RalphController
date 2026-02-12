using RalphController.Models;
using RalphController.Git;
using RalphController.Merge;
using RalphController.Parallel;
using RalphController.Messaging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RalphController;

/// <summary>
/// Individual team agent running in isolated worktree with assigned model.
/// Implements state machine: Spawning → Ready → [Claiming → Working → Idle] (loop) → ShuttingDown → Stopped
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
    private readonly string? _spawnPrompt;
    private TaskStore? _taskStore;
    private MessageBus? _messageBus;
    private MergeManager? _mergeManager;
    private CancellationTokenSource? _stopCts;
    private CancellationTokenSource? _forceStopCts;
    private bool _shutdownRequested;

    private bool _disposed;
    private readonly SemaphoreSlim _idleSignal = new(0);
    private readonly List<Message> _pendingContext = new();
    private Message? _planApprovalResult;
    private int _planRevisionCount;
    private const int MaxPlanRevisions = 3;

    /// <summary>Agent ID</summary>
    public string AgentId => _agentId;

    /// <summary>Agent state</summary>
    public AgentState State { get; private set; } = AgentState.Idle;

    /// <summary>Statistics</summary>
    public AgentStatistics Statistics { get; }

    /// <summary>Current task</summary>
    public AgentTask? CurrentTask { get; private set; }

    /// <summary>Spawn prompt (task-specific context)</summary>
    public string? SpawnPrompt => _spawnPrompt;

    /// <summary>Message bus for inter-agent communication</summary>
    public MessageBus? MessageBus => _messageBus;

    /// <summary>Whether this agent requires plan approval before implementation</summary>
    public bool RequirePlanApproval { get; private set; }

    // Events
    public event Action<AgentState>? OnStateChanged;
    public event Action<AgentTask>? OnTaskStart;
    public event Action<AgentTask, TaskResult>? OnTaskComplete;
    public event Action<AgentTask, string>? OnTaskFailed;
    public event Action<string>? OnOutput;
    public event Action<string>? OnError;
    public event Action<List<GitConflict>>? OnConflictDetected;
    public event Action<TeamAgent>? OnIdle;
    public event Action<TeamAgent>? OnStopped;

    /// <summary>
    /// Create a new team agent
    /// </summary>
    public TeamAgent(
        RalphConfig config,
        TeamConfig teamConfig,
        string agentId,
        int agentIndex,
        GitWorktreeManager gitManager,
        ModelSpec? assignedModel = null,
        string? spawnPrompt = null)
    {
        _config = config;
        _teamConfig = teamConfig;
        _agentId = agentId;
        _agentIndex = agentIndex;
        _gitManager = gitManager;
        _assignedModel = assignedModel ?? teamConfig.GetAgentModel(agentIndex);
        _spawnPrompt = spawnPrompt;

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
    /// Create a team agent from spawn configuration
    /// </summary>
    public static TeamAgent FromConfig(
        AgentSpawnConfig spawnConfig,
        RalphConfig config,
        TeamConfig teamConfig,
        GitWorktreeManager gitManager)
    {
        return new TeamAgent(
            config,
            teamConfig,
            Guid.NewGuid().ToString("N")[..8],
            spawnConfig.AgentIndex,
            gitManager,
            spawnConfig.Model,
            spawnConfig.SpawnPrompt)
        {
            RequirePlanApproval = spawnConfig.RequirePlanApproval
        };
    }

    /// <summary>
    /// Set the task store for this agent (required for RunAsync)
    /// </summary>
    public void SetTaskStore(TaskStore taskStore)
    {
        _taskStore = taskStore;
        _taskStore.TaskUnblocked += OnTaskUnblocked;
    }

    /// <summary>
    /// Set the message bus for this agent (required for message processing)
    /// </summary>
    public void SetMessageBus(MessageBus messageBus)
    {
        _messageBus = messageBus;
    }

    /// <summary>
    /// Set the merge manager for this agent (required for merge operations)
    /// </summary>
    public void SetMergeManager(MergeManager mergeManager)
    {
        _mergeManager = mergeManager;
    }

    private void OnTaskUnblocked(AgentTask task)
    {
        _idleSignal.Release();
    }

    /// <summary>
    /// Process pending messages from inbox. Handles shutdown requests, plan approvals, etc.
    /// </summary>
    private void ProcessPendingMessages()
    {
        if (_messageBus == null) return;

        var messages = _messageBus.Poll();
        foreach (var msg in messages)
        {
            switch (msg.Type)
            {
                case MessageType.ShutdownRequest:
                    HandleShutdownRequest(msg);
                    break;
                case MessageType.PlanApproval:
                    _pendingContext.Add(msg);
                    break;
                case MessageType.TaskAssignment:
                    HandleTaskAssignment(msg);
                    break;
                case MessageType.Text:
                case MessageType.Broadcast:
                    _pendingContext.Add(msg);
                    break;
            }
        }
    }

    private void HandleShutdownRequest(Message msg)
    {
        if (State == AgentState.Idle)
        {
            _shutdownRequested = true;
            _messageBus?.Send(Message.ShutdownResponseMessage(_agentId, true));
            _idleSignal.Release();
        }
        else
        {
            _shutdownRequested = true;
            _messageBus?.Send(Message.ShutdownResponseMessage(
                _agentId,
                false,
                $"Currently {State}. Will shutdown after current task."));
        }
    }

    private void HandleTaskAssignment(Message msg)
    {
        if (msg.Metadata?.TryGetValue("taskId", out var taskId) == true)
        {
            OnOutput?.Invoke($"Received task assignment: {taskId}");
        }
    }

    /// <summary>
    /// Request graceful shutdown. Agent will finish current task before stopping.
    /// </summary>
    public void RequestShutdown()
    {
        _shutdownRequested = true;
        _idleSignal.Release();
    }

    /// <summary>
    /// Force stop the agent immediately (used after RequestShutdown timeout).
    /// </summary>
    public void ForceStop()
    {
        _forceStopCts?.Cancel();
        _stopCts?.Cancel();
    }

    /// <summary>
    /// Initialize the agent's worktree
    /// </summary>
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_teamConfig.UseWorktrees) return true;

        SetState(AgentState.Spawning);

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
            SetState(AgentState.Error);
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
        SetState(AgentState.Working);

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
    /// Run the agent in a loop, claiming tasks from the queue.
    /// Uses TaskStore if set, otherwise uses the claimTask callback.
    /// Implements state machine: Ready → [Claiming → Working → Idle] (loop) → ShuttingDown → Stopped
    /// </summary>
    public async Task RunLoopAsync(
        Func<string, AgentTask?>? claimTask = null,
        CancellationToken cancellationToken = default)
    {
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _forceStopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        SetState(AgentState.Ready);

        try
        {
            while (!_stopCts.Token.IsCancellationRequested && !_shutdownRequested)
            {
                ProcessPendingMessages();

                if (_shutdownRequested && State == AgentState.Idle)
                {
                    SetState(AgentState.ShuttingDown);
                    break;
                }

                SetState(AgentState.Claiming);
                
                AgentTask? task;
                if (_taskStore != null)
                {
                    task = _taskStore.TryClaim(_agentId);
                }
                else if (claimTask != null)
                {
                    task = claimTask(_agentId);
                }
                else
                {
                    OnError?.Invoke("No TaskStore or claimTask callback provided");
                    SetState(AgentState.Error);
                    return;
                }

                if (task == null)
                {
                    var stats = _taskStore?.GetStatistics();
                    if (stats != null && stats.Pending == 0 && stats.InProgress == 0)
                    {
                        OnOutput?.Invoke("All tasks resolved");
                        OnIdle?.Invoke(this);
                        SetState(AgentState.Idle);
                        await WaitForShutdownOrNewWork(_stopCts.Token);
                        continue;
                    }
                    else if (stats != null && stats.Pending > 0)
                    {
                        SetState(AgentState.Idle);
                        OnIdle?.Invoke(this);
                        await WaitForClaimableTask(_stopCts.Token);
                        continue;
                    }
                    else
                    {
                        break;
                    }
                }

                if (_shutdownRequested)
                {
                    _messageBus?.Send(Message.ShutdownResponseMessage(_agentId, false, "Shutdown requested, finishing current task first"));
                }

                if (RequirePlanApproval)
                {
                    var planApproved = await RunPlanApprovalCycle(task, _stopCts.Token);
                    if (!planApproved)
                    {
                        continue;
                    }
                }

                SetState(AgentState.Working);
                await ExecuteTaskAsync(task, _stopCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetState(AgentState.Stopped);
            OnStopped?.Invoke(this);
        }
    }

    /// <summary>
    /// Run the plan-before-implement cycle for a task.
    /// Returns true if plan was approved, false if rejected or timed out.
    /// </summary>
    private async Task<bool> RunPlanApprovalCycle(AgentTask task, CancellationToken cancellationToken)
    {
        _planRevisionCount = 0;
        
        while (_planRevisionCount < MaxPlanRevisions && !cancellationToken.IsCancellationRequested)
        {
            SetState(AgentState.PlanningWork);
            OnOutput?.Invoke($"Planning task: {task.Title ?? task.Description}");

            var plan = await ProducePlanAsync(task, cancellationToken);
            
            _messageBus?.Send(Message.PlanSubmissionMessage(_agentId, plan, task.TaskId));
            OnOutput?.Invoke("Plan submitted, waiting for approval...");

            _planApprovalResult = await _messageBus?.WaitForMessage(MessageType.PlanApproval, TimeSpan.FromMinutes(10), cancellationToken)!;
            
            if (_planApprovalResult == null)
            {
                OnOutput?.Invoke("Plan approval timeout, proceeding with implementation");
                return true;
            }

            var approved = _planApprovalResult.Metadata?.TryGetValue("approved", out var approvedStr) == true
                && approvedStr == "true";

            if (approved)
            {
                OnOutput?.Invoke("Plan approved");
                return true;
            }

            var feedback = _planApprovalResult.Metadata?.GetValueOrDefault("feedback", "") ?? "";
            OnOutput?.Invoke($"Plan rejected: {feedback}");
            
            _pendingContext.Add(_planApprovalResult);
            _planRevisionCount++;
        }

        if (_planRevisionCount >= MaxPlanRevisions)
        {
            OnOutput?.Invoke($"Max plan revisions ({MaxPlanRevisions}) reached, proceeding with last plan");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Produce an implementation plan for a task (read-only analysis).
    /// </summary>
    private async Task<string> ProducePlanAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var planPrompt = BuildPlanPrompt(task);
        var result = await RunAIProcessAsync(planPrompt, cancellationToken);
        return result.Output ?? "No plan produced";
    }

    /// <summary>
    /// Build a prompt for plan-only analysis (no modifications).
    /// </summary>
    private string BuildPlanPrompt(AgentTask task)
    {
        var promptBuilder = new StringBuilder();

        if (!string.IsNullOrEmpty(_spawnPrompt))
        {
            promptBuilder.AppendLine("--- SPAWN CONTEXT ---");
            promptBuilder.AppendLine(_spawnPrompt);
            promptBuilder.AppendLine();
        }

        promptBuilder.AppendLine("--- PLANNING MODE ---");
        promptBuilder.AppendLine("You are in PLANNING MODE. Analyze the task and produce a detailed implementation plan.");
        promptBuilder.AppendLine("DO NOT modify any files. Only analyze and plan.");
        promptBuilder.AppendLine();

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
        promptBuilder.AppendLine("OUTPUT FORMAT:");
        promptBuilder.AppendLine("1. Summarize your understanding of the task");
        promptBuilder.AppendLine("2. List the files you will need to modify");
        promptBuilder.AppendLine("3. Describe the changes you will make to each file");
        promptBuilder.AppendLine("4. Identify any potential issues or dependencies");
        promptBuilder.AppendLine("5. Estimate the complexity and risk level");

        if (_pendingContext.Count > 0)
        {
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("--- FEEDBACK FROM PREVIOUS PLAN ---");
            foreach (var msg in _pendingContext.Where(m => m.Type == MessageType.PlanApproval))
            {
                var feedback = msg.Metadata?.GetValueOrDefault("feedback", "");
                if (!string.IsNullOrEmpty(feedback))
                {
                    promptBuilder.AppendLine(feedback);
                }
            }
        }

        return promptBuilder.ToString();
    }

    /// <summary>
    /// Wait for a claimable task with exponential backoff (1s → 30s).
    /// Wakes immediately when TaskUnblocked event fires.
    /// Also processes pending messages during idle.
    /// </summary>
    private async Task WaitForClaimableTask(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(30);

        while (!cancellationToken.IsCancellationRequested && !_shutdownRequested)
        {
            ProcessPendingMessages();

            if (_shutdownRequested)
            {
                return;
            }

            if (_taskStore?.GetClaimable().Count > 0)
            {
                return;
            }

            try
            {
                await _idleSignal.WaitAsync(delay, cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TimeoutException)
            {
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
        }
    }

    /// <summary>
    /// Wait for shutdown request or new work to become available.
    /// Also processes pending messages during idle.
    /// </summary>
    private async Task WaitForShutdownOrNewWork(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_shutdownRequested)
        {
            ProcessPendingMessages();

            if (_shutdownRequested)
            {
                return;
            }

            if (_taskStore?.GetClaimable().Count > 0)
            {
                return;
            }

            try
            {
                await _idleSignal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TimeoutException)
            {
            }
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

        SetState(AgentState.Claiming);
        Statistics.MergesAttempted++;

        try
        {
            var targetBranch = _teamConfig.TargetBranch;
            if (string.IsNullOrEmpty(targetBranch))
            {
                targetBranch = await _gitManager.GetCurrentBranchAsync(cancellationToken);
            }

            if (_mergeManager == null)
            {
                return new MergeResult
                {
                    Success = false,
                    Error = "MergeManager not configured for this agent"
                };
            }

            MergeResult result;
            switch (_teamConfig.MergeStrategy)
            {
                case MergeStrategy.RebaseThenMerge:
                    result = await _mergeManager.RebaseAndMergeAsync(
                        _worktreePath, _branchName, targetBranch, cancellationToken);
                    break;
                case MergeStrategy.MergeDirect:
                    result = await _mergeManager.MergeDirectAsync(
                        _worktreePath, _branchName, targetBranch, cancellationToken);
                    break;
                default:
                    result = await _mergeManager.SequentialMergeAsync(
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
            if (State == AgentState.Claiming)
            {
                SetState(AgentState.Stopped);
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

    private void SetState(AgentState newState)
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

        // Add spawn prompt first (task-specific context from orchestrator)
        if (!string.IsNullOrEmpty(_spawnPrompt))
        {
            promptBuilder.AppendLine("--- SPAWN CONTEXT ---");
            promptBuilder.AppendLine(_spawnPrompt);
            promptBuilder.AppendLine();
        }

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

            // Read output — stream stdout line-by-line to parse stream-json and
            // emit only meaningful text, avoiding raw JSON flooding the TUI.
            var outputBuilder = new StringBuilder();
            var textBuilder = new StringBuilder();  // Accumulated parsed text
            var lastProgressAt = DateTime.UtcNow;
            var usesStreamJson = providerConfig.UsesStreamJson;

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            // Read stdout line by line
            while (!process.StandardOutput.EndOfStream)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line == null) break;

                outputBuilder.AppendLine(line);
                Statistics.OutputChars += line.Length;

                if (usesStreamJson)
                {
                    // Parse stream-json to extract text deltas
                    var text = ParseStreamJsonLine(line);
                    if (text != null)
                    {
                        textBuilder.Append(text);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    // Non-stream-json providers: forward non-empty lines directly
                    textBuilder.AppendLine(line);
                }

                // Emit a progress heartbeat every 30 seconds so TUI shows activity
                if ((DateTime.UtcNow - lastProgressAt).TotalSeconds >= 30)
                {
                    lastProgressAt = DateTime.UtcNow;
                    var charsSoFar = textBuilder.Length;
                    OnOutput?.Invoke($"Working... ({charsSoFar:N0} chars of output so far)");
                }
            }

            await process.WaitForExitAsync(cancellationToken);

            var output = outputBuilder.ToString();
            var error = await stderrTask;

            // Emit a summary of the parsed text output (last few meaningful lines)
            var parsedText = textBuilder.ToString().Trim();
            if (!string.IsNullOrEmpty(parsedText))
            {
                // Show last meaningful line as a completion indicator
                var lastLines = parsedText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lastLines.Length > 0)
                {
                    var lastLine = lastLines[^1].Trim();
                    if (lastLine.Length > 200) lastLine = lastLine[..200] + "...";
                    OnOutput?.Invoke(lastLine);
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

    /// <summary>
    /// Parse a line of stream-json output to extract text content.
    /// Returns the text delta or null if the line isn't a text event.
    /// </summary>
    private static string? ParseStreamJsonLine(string line)
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

            // Gemini stream-json: similar structure
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_taskStore != null)
        {
            _taskStore.TaskUnblocked -= OnTaskUnblocked;
        }

        _stopCts?.Cancel();
        _stopCts?.Dispose();
        _forceStopCts?.Cancel();
        _forceStopCts?.Dispose();
        _idleSignal?.Dispose();

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
