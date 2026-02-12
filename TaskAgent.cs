using RalphController.Models;
using RalphController.Git;
using System.Text;

namespace RalphController;

/// <summary>
/// Ephemeral per-task agent (Tier 2 in lead-driven mode).
/// Created by LeadAgent for ONE task, gets its own worktree, runs 3 sub-agent phases:
///   1. Plan  — read-only analysis, returns plan text
///   2. Code  — implementation, commits to worktree
///   3. Verify — verification (runs VerifyCommand if configured)
/// </summary>
public class TaskAgent : IDisposable
{
    private readonly RalphConfig _config;
    private readonly TeamConfig _teamConfig;
    private readonly AgentTask _task;
    private readonly string _agentId;
    private readonly string _worktreePath;
    private readonly string _branchName;
    private readonly GitWorktreeManager _gitManager;
    private readonly ModelSpec? _assignedModel;
    private bool _disposed;

    /// <summary>Agent identifier (e.g., "task-agent-3")</summary>
    public string AgentId => _agentId;

    /// <summary>The task this agent is executing</summary>
    public AgentTask Task => _task;

    /// <summary>Branch name for the worktree</summary>
    public string BranchName => _branchName;

    /// <summary>Worktree path</summary>
    public string WorktreePath => _worktreePath;

    /// <summary>Current sub-agent phase</summary>
    public SubAgentPhase CurrentPhase { get; private set; } = SubAgentPhase.None;

    /// <summary>Statistics for TUI display</summary>
    public AgentStatistics Statistics { get; }

    // Events
    public event Action<string>? OnOutput;
    public event Action<string>? OnError;
    public event Action<SubAgentPhase>? OnPhaseChanged;
    public event Action<AgentStatistics>? OnUpdate;

    public TaskAgent(
        RalphConfig config,
        TeamConfig teamConfig,
        AgentTask task,
        GitWorktreeManager gitManager,
        ModelSpec? assignedModel = null)
    {
        _config = config;
        _teamConfig = teamConfig;
        _task = task;
        _gitManager = gitManager;
        _assignedModel = assignedModel ?? teamConfig.LeadModel;

        _agentId = $"task-agent-{task.TaskId}";
        _branchName = $"ralph/task-{task.TaskId}";
        _worktreePath = teamConfig.UseWorktrees
            ? Path.Combine(config.TargetDirectory, ".ralph-worktrees", _agentId)
            : config.TargetDirectory;

        var modelLabel = _assignedModel?.DisplayName ?? config.Provider.ToString();
        Statistics = new AgentStatistics
        {
            AgentId = _agentId,
            Name = $"Task: {Truncate(task.Title ?? task.Description, 40)} [{modelLabel}]",
            WorktreePath = _worktreePath,
            BranchName = _branchName,
            AssignedModel = _assignedModel,
            CurrentTask = task
        };
    }

    /// <summary>
    /// Initialize the worktree for this task agent.
    /// </summary>
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_teamConfig.UseWorktrees) return true;

        Statistics.State = AgentState.Spawning;
        OnUpdate?.Invoke(Statistics);

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
            Statistics.State = AgentState.Error;
            OnUpdate?.Invoke(Statistics);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Run all configured sub-agent phases sequentially.
    /// Returns aggregated result.
    /// </summary>
    public async Task<TaskAgentResult> RunAsync(CancellationToken cancellationToken = default)
    {
        Statistics.State = AgentState.Working;
        OnUpdate?.Invoke(Statistics);

        SubAgentResult? planResult = null;
        SubAgentResult? codeResult = null;
        SubAgentResult? verifyResult = null;
        var allFiles = new List<string>();

        try
        {
            var phases = _teamConfig.SubAgentPhases;

            // Phase 1: Plan
            if (phases.Contains(SubAgentPhase.Plan))
            {
                SetPhase(SubAgentPhase.Plan);
                planResult = await RunPlanPhaseAsync(cancellationToken);
                if (!planResult.Success)
                {
                    return BuildResult(planResult, null, null, false, allFiles);
                }
            }

            // Phase 2: Code
            if (phases.Contains(SubAgentPhase.Code))
            {
                SetPhase(SubAgentPhase.Code);
                codeResult = await RunCodePhaseAsync(planResult?.Output, cancellationToken);
                if (!codeResult.Success)
                {
                    return BuildResult(planResult, codeResult, null, false, allFiles);
                }
                allFiles.AddRange(codeResult.FilesModified);

                // Commit after code phase
                if (_teamConfig.UseWorktrees)
                {
                    await _gitManager.CommitWorktreeAsync(
                        _worktreePath,
                        $"[{_agentId}] {_task.Title ?? _task.Description}",
                        cancellationToken);
                }
            }

            // Phase 3: Verify
            if (phases.Contains(SubAgentPhase.Verify))
            {
                SetPhase(SubAgentPhase.Verify);
                verifyResult = await RunVerifyPhaseAsync(cancellationToken);
                if (!verifyResult.Success)
                {
                    return BuildResult(planResult, codeResult, verifyResult, false, allFiles);
                }
            }

            SetPhase(SubAgentPhase.None);
            Statistics.TasksCompleted++;
            Statistics.State = AgentState.Stopped;
            OnUpdate?.Invoke(Statistics);

            return BuildResult(planResult, codeResult, verifyResult, true, allFiles);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"TaskAgent failed: {ex.Message}");
            Statistics.TasksFailed++;
            Statistics.State = AgentState.Error;
            OnUpdate?.Invoke(Statistics);

            return new TaskAgentResult
            {
                Plan = planResult,
                Code = codeResult,
                Verify = verifyResult,
                Success = false,
                BranchName = _branchName,
                Summary = $"Error: {ex.Message}",
                AllFilesModified = allFiles
            };
        }
    }

    /// <summary>
    /// Clean up the worktree.
    /// </summary>
    public async Task CleanupAsync()
    {
        if (_teamConfig.UseWorktrees && _teamConfig.CleanupWorktreesOnSuccess)
        {
            await _gitManager.RemoveWorktreeAsync(_worktreePath);
        }
    }

    // --- Phase implementations ---

    private async Task<SubAgentResult> RunPlanPhaseAsync(CancellationToken ct)
    {
        OnOutput?.Invoke("Starting Plan phase (read-only analysis)...");

        var prompt = BuildPlanPrompt();
        var result = await RunSubAgentAsync(prompt, ct);

        return new SubAgentResult
        {
            Success = result.Success,
            Output = result.ParsedText.Length > 0 ? result.ParsedText : result.Output,
            Error = result.Error,
            Phase = SubAgentPhase.Plan,
            FilesModified = new List<string>()
        };
    }

    private async Task<SubAgentResult> RunCodePhaseAsync(string? planOutput, CancellationToken ct)
    {
        OnOutput?.Invoke("Starting Code phase (implementation)...");

        var prompt = BuildCodePrompt(planOutput);
        var result = await RunSubAgentAsync(prompt, ct);

        var modifiedFiles = _teamConfig.UseWorktrees
            ? await _gitManager.GetModifiedFilesAsync(_worktreePath, ct)
            : new List<string>();

        return new SubAgentResult
        {
            Success = result.Success,
            Output = result.ParsedText.Length > 0 ? result.ParsedText : result.Output,
            Error = result.Error,
            Phase = SubAgentPhase.Code,
            FilesModified = modifiedFiles
        };
    }

    private async Task<SubAgentResult> RunVerifyPhaseAsync(CancellationToken ct)
    {
        OnOutput?.Invoke("Starting Verify phase (build/test/review)...");

        var prompt = BuildVerifyPrompt();
        var result = await RunSubAgentAsync(prompt, ct);

        return new SubAgentResult
        {
            Success = result.Success,
            Output = result.ParsedText.Length > 0 ? result.ParsedText : result.Output,
            Error = result.Error,
            Phase = SubAgentPhase.Verify,
            FilesModified = new List<string>()
        };
    }

    private async Task<AgentProcessResult> RunSubAgentAsync(string prompt, CancellationToken ct)
    {
        var providerConfig = GetProviderConfig();
        var workingDir = _teamConfig.UseWorktrees ? _worktreePath : _config.TargetDirectory;

        var result = await AIProcessRunner.RunAsync(
            providerConfig,
            prompt,
            workingDir,
            output =>
            {
                Statistics.LastActivityAt = DateTime.UtcNow;
                OnOutput?.Invoke(output);
            },
            ct);

        Statistics.OutputChars += result.OutputChars;
        Statistics.ErrorChars += result.ErrorChars;
        Statistics.Iterations++;
        Statistics.LastActivityAt = DateTime.UtcNow;

        return result;
    }

    // --- Prompt builders ---

    private string BuildPlanPrompt()
    {
        var sb = new StringBuilder();

        sb.AppendLine("--- TEAMS MODE (LEAD-DRIVEN) ---");
        sb.AppendLine("You are a PLANNING sub-agent. Analyze the task and produce a detailed implementation plan.");
        sb.AppendLine("DO NOT modify any files. Only analyze and plan.");
        sb.AppendLine("IMPORTANT: Ignore any instructions about picking tasks, RALPH_STATUS blocks, or EXIT_SIGNAL.");
        sb.AppendLine();

        AppendProjectContext(sb);

        sb.AppendLine("--- YOUR ASSIGNED TASK ---");
        AppendTaskDetails(sb);

        sb.AppendLine("OUTPUT FORMAT:");
        sb.AppendLine("1. Summarize your understanding of the task");
        sb.AppendLine("2. List the files you will need to modify");
        sb.AppendLine("3. Describe the changes you will make to each file");
        sb.AppendLine("4. Identify any potential issues or dependencies");
        sb.AppendLine("5. Estimate the complexity and risk level");

        return sb.ToString();
    }

    private string BuildCodePrompt(string? planOutput)
    {
        var sb = new StringBuilder();

        sb.AppendLine("--- TEAMS MODE (LEAD-DRIVEN) ---");
        sb.AppendLine("You are an IMPLEMENTATION sub-agent. Implement the changes described below.");
        sb.AppendLine("IMPORTANT: Ignore any instructions about picking tasks, RALPH_STATUS blocks, or EXIT_SIGNAL.");
        sb.AppendLine("When you are finished, simply exit normally.");
        sb.AppendLine();

        AppendProjectContext(sb);

        sb.AppendLine("--- YOUR ASSIGNED TASK ---");
        AppendTaskDetails(sb);

        if (!string.IsNullOrEmpty(planOutput))
        {
            sb.AppendLine("--- IMPLEMENTATION PLAN (from planning phase) ---");
            sb.AppendLine(planOutput);
            sb.AppendLine();
        }

        sb.AppendLine("INSTRUCTIONS:");
        sb.AppendLine("- Focus ONLY on this specific task");
        sb.AppendLine("- Do not modify files unrelated to this task");
        sb.AppendLine("- Commit your changes when done");
        sb.AppendLine("- When finished, exit normally");

        return sb.ToString();
    }

    private string BuildVerifyPrompt()
    {
        var sb = new StringBuilder();

        sb.AppendLine("--- TEAMS MODE (LEAD-DRIVEN) ---");
        sb.AppendLine("You are a VERIFICATION sub-agent. Review the changes and verify they are correct.");
        sb.AppendLine("IMPORTANT: Ignore any instructions about picking tasks, RALPH_STATUS blocks, or EXIT_SIGNAL.");
        sb.AppendLine();

        AppendProjectContext(sb);

        sb.AppendLine("--- YOUR ASSIGNED TASK ---");
        AppendTaskDetails(sb);

        if (!string.IsNullOrEmpty(_teamConfig.VerifyCommand))
        {
            sb.AppendLine($"--- VERIFICATION COMMAND ---");
            sb.AppendLine($"Run this command to verify the implementation:");
            sb.AppendLine($"  {_teamConfig.VerifyCommand}");
            sb.AppendLine();
        }

        sb.AppendLine("INSTRUCTIONS:");
        sb.AppendLine("- Review the code changes made for this task");
        if (!string.IsNullOrEmpty(_teamConfig.VerifyCommand))
        {
            sb.AppendLine($"- Run: {_teamConfig.VerifyCommand}");
        }
        sb.AppendLine("- Check for correctness, edge cases, and potential bugs");
        sb.AppendLine("- If issues are found, fix them");
        sb.AppendLine("- Report a summary of your findings");

        return sb.ToString();
    }

    private void AppendProjectContext(StringBuilder sb)
    {
        var promptPath = AIProcessRunner.ResolvePromptPath(_config, _teamConfig.UseWorktrees, _worktreePath);
        var promptContent = AIProcessRunner.TryReadFile(promptPath, _config.PromptFilePath);
        if (!string.IsNullOrEmpty(promptContent))
        {
            sb.AppendLine("--- PROJECT CONTEXT ---");
            sb.AppendLine("The following is the project prompt for reference. Ignore any task-picking or verification workflow instructions.");
            sb.AppendLine();
            sb.AppendLine(AIProcessRunner.StripRalphStatusBlock(promptContent));
            sb.AppendLine();
        }
    }

    private void AppendTaskDetails(StringBuilder sb)
    {
        if (!string.IsNullOrEmpty(_task.Title))
        {
            sb.AppendLine($"TASK: {_task.Title}");
        }
        sb.AppendLine($"DESCRIPTION: {_task.Description}");

        if (_task.Files.Count > 0)
        {
            sb.AppendLine($"LIKELY FILES: {string.Join(", ", _task.Files)}");
        }
        sb.AppendLine();
    }

    // --- Helpers ---

    private AIProviderConfig GetProviderConfig()
    {
        if (_assignedModel != null)
        {
            return _assignedModel.ToProviderConfig();
        }
        return _config.ProviderConfig;
    }

    private void SetPhase(SubAgentPhase phase)
    {
        CurrentPhase = phase;
        Statistics.CurrentSubPhase = phase;

        Statistics.State = phase switch
        {
            SubAgentPhase.Plan => AgentState.PlanningWork,
            SubAgentPhase.Code => AgentState.Coding,
            SubAgentPhase.Verify => AgentState.Verifying,
            _ => AgentState.Working
        };

        OnPhaseChanged?.Invoke(phase);
        OnUpdate?.Invoke(Statistics);
    }

    private TaskAgentResult BuildResult(
        SubAgentResult? plan,
        SubAgentResult? code,
        SubAgentResult? verify,
        bool success,
        List<string> allFiles)
    {
        var summaryParts = new List<string>();
        if (plan != null) summaryParts.Add($"Plan: {(plan.Success ? "OK" : "Failed")}");
        if (code != null) summaryParts.Add($"Code: {(code.Success ? "OK" : "Failed")}");
        if (verify != null) summaryParts.Add($"Verify: {(verify.Success ? "OK" : "Failed")}");

        return new TaskAgentResult
        {
            Plan = plan,
            Code = code,
            Verify = verify,
            Success = success,
            BranchName = _branchName,
            Summary = string.Join(", ", summaryParts),
            AllFilesModified = allFiles
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
        GC.SuppressFinalize(this);
    }
}
