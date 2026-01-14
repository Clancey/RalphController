using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using RalphController.Models;

namespace RalphController.Workflow;

/// <summary>
/// Orchestrates collaborative workflows with parallel agent execution
/// </summary>
public class WorkflowOrchestrator : IDisposable
{
    private readonly RalphConfig _config;
    private readonly CollaborationConfig _collaborationConfig;
    private readonly Dictionary<AgentRole, string> _systemPrompts = new();
    private readonly SemaphoreSlim _agentSemaphore;
    private CollaborationWorkflow? _currentWorkflow;
    private bool _disposed;

    // Events
    public event Action<WorkflowStep>? OnStepStarted;
    public event Action<WorkflowStep>? OnStepCompleted;
    public event Action<CollaborationWorkflow>? OnWorkflowStarted;
    public event Action<CollaborationWorkflow>? OnWorkflowCompleted;
    public event Action<string>? OnOutput;
    public event Action<string>? OnError;

    public WorkflowOrchestrator(RalphConfig config, CollaborationConfig collaborationConfig)
    {
        _config = config;
        _collaborationConfig = collaborationConfig;

        // Limit concurrent agent executions (for parallel steps)
        var maxParallel = Math.Max(2, Environment.ProcessorCount / 2);
        _agentSemaphore = new SemaphoreSlim(maxParallel);

        LoadSystemPrompts();
    }

    /// <summary>Current workflow being executed</summary>
    public CollaborationWorkflow? CurrentWorkflow => _currentWorkflow;

    #region Spec Workflow

    /// <summary>
    /// Run a spec workflow for a feature request
    /// </summary>
    public async Task<SpecResult> RunSpecWorkflowAsync(
        string featureRequest,
        List<string>? contextFiles = null,
        CancellationToken ct = default)
    {
        // Reset model rotation for fresh provider distribution
        ResetRoleRotation();

        _currentWorkflow = new CollaborationWorkflow
        {
            Type = WorkflowType.Spec,
            OriginalRequest = featureRequest,
            ContextFiles = contextFiles ?? new List<string>(),
            Status = WorkflowStatus.InProgress,
            StartedAt = DateTime.UtcNow
        };
        OnWorkflowStarted?.Invoke(_currentWorkflow);

        try
        {
            // Step 1: Planner creates initial spec
            OnOutput?.Invoke("▶ Starting spec workflow...");
            OnOutput?.Invoke("  Step 1: Planning initial spec");

            var plannerStep = await RunAgentStepAsync(
                AgentRole.Planner,
                BuildPlannerPrompt(featureRequest, contextFiles),
                ct);

            // Step 2: Challenger critiques (if enabled)
            WorkflowStep? challengerStep = null;
            if (_collaborationConfig.Spec?.EnableChallenger == true)
            {
                OnOutput?.Invoke("  Step 2: Challenging assumptions");
                challengerStep = await RunAgentStepAsync(
                    AgentRole.Challenger,
                    BuildChallengerPrompt(plannerStep.Response!, featureRequest),
                    ct);
            }

            // Step 3: Synthesizer creates final spec
            OnOutput?.Invoke($"  Step {(challengerStep != null ? 3 : 2)}: Synthesizing final spec");
            var synthesizerStep = await RunAgentStepAsync(
                AgentRole.Synthesizer,
                BuildSynthesizerPrompt(featureRequest, plannerStep.Response!, challengerStep?.Response),
                ct);

            _currentWorkflow.FinalOutput = synthesizerStep.Response;
            _currentWorkflow.Status = WorkflowStatus.Completed;
            _currentWorkflow.CompletedAt = DateTime.UtcNow;

            var result = new SpecResult
            {
                InitialSpec = plannerStep.Response!,
                Challenges = challengerStep?.Response,
                FinalSpec = synthesizerStep.Response!,
                Workflow = _currentWorkflow,
                Tasks = ExtractTasks(synthesizerStep.Response!)
            };

            OnOutput?.Invoke($"✓ Spec workflow complete ({_currentWorkflow.TotalDuration?.TotalSeconds:F1}s)");
            OnWorkflowCompleted?.Invoke(_currentWorkflow);

            return result;
        }
        catch (Exception ex)
        {
            _currentWorkflow.Status = WorkflowStatus.Failed;
            _currentWorkflow.Error = ex.Message;
            _currentWorkflow.CompletedAt = DateTime.UtcNow;
            OnError?.Invoke($"Spec workflow failed: {ex.Message}");
            throw;
        }
    }

    #endregion

    #region Review Workflow

    /// <summary>
    /// Run a code review workflow (supports parallel reviewers)
    /// </summary>
    public async Task<ReviewResult> RunReviewWorkflowAsync(
        List<string> changedFiles,
        string? commitRange = null,
        CancellationToken ct = default)
    {
        // Reset model rotation for fresh provider distribution
        ResetRoleRotation();

        _currentWorkflow = new CollaborationWorkflow
        {
            Type = WorkflowType.Review,
            OriginalRequest = $"Review {changedFiles.Count} files",
            ContextFiles = changedFiles,
            Status = WorkflowStatus.InProgress,
            StartedAt = DateTime.UtcNow
        };
        OnWorkflowStarted?.Invoke(_currentWorkflow);

        try
        {
            var reviewerCount = _collaborationConfig.Review?.ReviewerCount ?? 1;
            OnOutput?.Invoke($"▶ Starting review workflow with {reviewerCount} reviewer(s)...");

            // Run multiple reviewers IN PARALLEL
            var reviewTasks = new List<Task<WorkflowStep>>();
            for (int i = 0; i < reviewerCount; i++)
            {
                var reviewerIndex = i;
                reviewTasks.Add(RunAgentStepAsync(
                    AgentRole.Reviewer,
                    BuildReviewPrompt(changedFiles, commitRange, reviewerIndex),
                    ct,
                    stepLabel: $"Reviewer {i + 1}"));
            }

            OnOutput?.Invoke($"  Running {reviewerCount} reviewers in parallel...");
            var reviewSteps = await Task.WhenAll(reviewTasks);
            OnOutput?.Invoke($"  All reviewers complete");

            // Parse and consolidate findings
            var allFindings = new List<ReviewFinding>();
            foreach (var step in reviewSteps)
            {
                var findings = ParseReviewFindings(step.Response!, step.ModelName);
                allFindings.AddRange(findings);
            }

            var consolidatedFindings = ConsolidateFindings(allFindings);
            OnOutput?.Invoke($"  Found {consolidatedFindings.Count} unique issues");

            // Expert validation for critical issues (if enabled)
            if (_collaborationConfig.Review?.ExpertValidation == true &&
                consolidatedFindings.Any(f => f.Severity == ReviewSeverity.Critical))
            {
                OnOutput?.Invoke("  Running expert validation on critical issues...");
                var validationStep = await RunAgentStepAsync(
                    AgentRole.Reviewer,
                    BuildValidationPrompt(consolidatedFindings),
                    ct,
                    useExpertModel: true,
                    stepLabel: "Expert Validator");

                consolidatedFindings = ValidateFindings(consolidatedFindings, validationStep.Response!);
            }

            var summary = GenerateReviewSummary(consolidatedFindings);
            _currentWorkflow.FinalOutput = summary;
            _currentWorkflow.Status = WorkflowStatus.Completed;
            _currentWorkflow.CompletedAt = DateTime.UtcNow;

            var result = new ReviewResult
            {
                Findings = consolidatedFindings,
                Summary = summary,
                Workflow = _currentWorkflow
            };

            OnOutput?.Invoke($"✓ Review workflow complete: {(result.Approved ? "APPROVED" : "NEEDS WORK")}");
            OnWorkflowCompleted?.Invoke(_currentWorkflow);

            return result;
        }
        catch (Exception ex)
        {
            _currentWorkflow.Status = WorkflowStatus.Failed;
            _currentWorkflow.Error = ex.Message;
            _currentWorkflow.CompletedAt = DateTime.UtcNow;
            OnError?.Invoke($"Review workflow failed: {ex.Message}");
            throw;
        }
    }

    #endregion

    #region Task Workflow

    /// <summary>
    /// Result of a collaborative task workflow
    /// </summary>
    public class TaskWorkflowResult
    {
        public bool Success { get; set; }
        public string? PlannerAnalysis { get; set; }
        public string? ImplementerOutput { get; set; }
        public string? ReviewerFeedback { get; set; }
        public List<string> FilesModified { get; set; } = new();
        public List<string> IssuesFound { get; set; } = new();
        public bool NeedsMoreWork { get; set; }
        public CollaborationWorkflow? Workflow { get; set; }
    }

    /// <summary>
    /// Run ONLY the Planner step to analyze a task and create an implementation plan.
    /// This is used by the hybrid approach: Planner → Main AI Execution → Reviewer
    /// </summary>
    public async Task<string?> RunPlannerOnlyAsync(string taskPrompt, CancellationToken ct = default)
    {
        try
        {
            // Initialize a minimal workflow context if not already set
            _currentWorkflow ??= new CollaborationWorkflow
            {
                Type = WorkflowType.Spec,
                OriginalRequest = "Planner-only execution",
                Status = WorkflowStatus.InProgress,
                StartedAt = DateTime.UtcNow
            };

            OnOutput?.Invoke("▶ [Planner] Analyzing task and creating implementation plan...");

            var plannerStep = await RunAgentStepAsync(
                AgentRole.Planner,
                BuildTaskPlannerPrompt(taskPrompt),
                ct,
                stepLabel: "Planner");

            OnOutput?.Invoke($"  ✓ Planner complete ({plannerStep.ModelName})");
            return plannerStep.Response;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Planner failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Run ONLY the Reviewer step to check implementation results.
    /// This is used after the main AI execution to verify changes.
    /// </summary>
    public async Task<(bool approved, string? feedback, List<string> issues)> RunReviewerOnlyAsync(
        string originalTask,
        string implementationOutput,
        List<string> modifiedFiles,
        CancellationToken ct = default)
    {
        try
        {
            // Initialize a minimal workflow context if not already set
            _currentWorkflow ??= new CollaborationWorkflow
            {
                Type = WorkflowType.Review,
                OriginalRequest = "Reviewer-only execution",
                Status = WorkflowStatus.InProgress,
                StartedAt = DateTime.UtcNow
            };

            OnOutput?.Invoke("▶ [Reviewer] Checking implementation...");

            var reviewerStep = await RunAgentStepAsync(
                AgentRole.Reviewer,
                BuildPostExecutionReviewPrompt(originalTask, implementationOutput, modifiedFiles),
                ct,
                stepLabel: "Reviewer");

            OnOutput?.Invoke($"  ✓ Reviewer complete ({reviewerStep.ModelName})");

            var issues = ExtractReviewIssues(reviewerStep.Response!);
            var approved = issues.Count == 0 ||
                          reviewerStep.Response!.Contains("APPROVED", StringComparison.OrdinalIgnoreCase);

            if (approved)
                OnOutput?.Invoke("  ✓ Implementation APPROVED by reviewer");
            else
                OnOutput?.Invoke($"  ⚠ Reviewer found {issues.Count} issue(s)");

            return (approved, reviewerStep.Response, issues);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Reviewer failed: {ex.Message}");
            return (true, null, new List<string>()); // Don't block on reviewer failure
        }
    }

    private string BuildPostExecutionReviewPrompt(string originalTask, string output, List<string> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Post-Implementation Review");
        sb.AppendLine();
        sb.AppendLine("You are the REVIEWER agent. Check if the implementation correctly addresses the task.");
        sb.AppendLine();
        sb.AppendLine("### Original Task:");
        sb.AppendLine(originalTask);
        sb.AppendLine();
        sb.AppendLine("### Implementation Output:");
        sb.AppendLine(output.Length > 3000 ? output[..3000] + "\n...[truncated]" : output);
        sb.AppendLine();
        if (files.Count > 0)
        {
            sb.AppendLine("### Files Modified:");
            foreach (var file in files.Take(20))
                sb.AppendLine($"- {file}");
        }
        sb.AppendLine();
        sb.AppendLine("### Review Checklist:");
        sb.AppendLine("1. Does the implementation address the original task?");
        sb.AppendLine("2. Are there any obvious errors or issues?");
        sb.AppendLine("3. Is anything missing that was requested?");
        sb.AppendLine();
        sb.AppendLine("Respond with APPROVED if the implementation looks correct, or CHANGES REQUESTED with specific issues.");
        return sb.ToString();
    }

    /// <summary>
    /// Run a collaborative task workflow for each iteration
    /// Planner → Implementer → Reviewer (→ loop if issues found)
    /// </summary>
    public async Task<TaskWorkflowResult> RunTaskWorkflowAsync(
        string taskPrompt,
        string workingDirectory,
        int maxReviewCycles = 2,
        CancellationToken ct = default)
    {
        // NOTE: Do NOT reset rotation here - we want models to rotate ACROSS iterations
        // Each iteration should use different models from the pool

        _currentWorkflow = new CollaborationWorkflow
        {
            Type = WorkflowType.Spec, // Reuse spec type for now
            OriginalRequest = taskPrompt.Length > 200 ? taskPrompt[..200] + "..." : taskPrompt,
            Status = WorkflowStatus.InProgress,
            StartedAt = DateTime.UtcNow
        };
        OnWorkflowStarted?.Invoke(_currentWorkflow);

        var result = new TaskWorkflowResult { Workflow = _currentWorkflow };

        try
        {
            // Step 1: PLANNER analyzes the task
            OnOutput?.Invoke("");
            OnOutput?.Invoke("╔══════════════════════════════════════════════════════════════╗");
            OnOutput?.Invoke("║         MULTI-AGENT COLLABORATION - Task Workflow            ║");
            OnOutput?.Invoke("╚══════════════════════════════════════════════════════════════╝");
            OnOutput?.Invoke("");
            OnOutput?.Invoke("▶ Step 1: [Planner] Analyzing task and creating implementation plan...");

            var plannerStep = await RunAgentStepAsync(
                AgentRole.Planner,
                BuildTaskPlannerPrompt(taskPrompt),
                ct,
                stepLabel: "Planner");

            result.PlannerAnalysis = plannerStep.Response;
            OnOutput?.Invoke($"  ✓ Planner complete ({plannerStep.ModelName})");

            // Step 2: IMPLEMENTER executes the plan
            OnOutput?.Invoke("");
            OnOutput?.Invoke("▶ Step 2: [Implementer] Executing implementation plan...");

            var implementerStep = await RunAgentStepAsync(
                AgentRole.Implementer,
                BuildImplementerPrompt(taskPrompt, plannerStep.Response!),
                ct,
                stepLabel: "Implementer");

            result.ImplementerOutput = implementerStep.Response;
            result.FilesModified = ExtractModifiedFiles(implementerStep.Response!);
            OnOutput?.Invoke($"  ✓ Implementer complete ({implementerStep.ModelName})");

            // Step 3: REVIEWER checks the implementation
            for (int reviewCycle = 0; reviewCycle < maxReviewCycles; reviewCycle++)
            {
                OnOutput?.Invoke("");
                OnOutput?.Invoke($"▶ Step 3{(reviewCycle > 0 ? $".{reviewCycle + 1}" : "")}: [Reviewer] Reviewing implementation...");

                var reviewerStep = await RunAgentStepAsync(
                    AgentRole.Reviewer,
                    BuildTaskReviewerPrompt(taskPrompt, result.PlannerAnalysis!, result.ImplementerOutput!, result.FilesModified),
                    ct,
                    stepLabel: $"Reviewer{(reviewCycle > 0 ? $" (cycle {reviewCycle + 1})" : "")}");

                result.ReviewerFeedback = reviewerStep.Response;
                var issues = ExtractReviewIssues(reviewerStep.Response!);
                result.IssuesFound = issues;
                OnOutput?.Invoke($"  ✓ Reviewer complete ({reviewerStep.ModelName})");

                // Check if reviewer approved
                if (issues.Count == 0 || reviewerStep.Response!.Contains("APPROVED", StringComparison.OrdinalIgnoreCase))
                {
                    OnOutput?.Invoke("  ✓ Implementation APPROVED by reviewer");
                    result.NeedsMoreWork = false;
                    break;
                }
                else if (reviewCycle < maxReviewCycles - 1)
                {
                    // Run another implementation cycle to address issues
                    OnOutput?.Invoke($"  ⚠ {issues.Count} issue(s) found - running fix cycle...");
                    OnOutput?.Invoke("");
                    OnOutput?.Invoke($"▶ Step 3.{reviewCycle + 1}b: [Implementer] Addressing review feedback...");

                    var fixStep = await RunAgentStepAsync(
                        AgentRole.Implementer,
                        BuildFixPrompt(taskPrompt, result.ImplementerOutput!, reviewerStep.Response!),
                        ct,
                        stepLabel: "Implementer (fixes)");

                    result.ImplementerOutput = fixStep.Response;
                    result.FilesModified.AddRange(ExtractModifiedFiles(fixStep.Response!));
                    OnOutput?.Invoke($"  ✓ Fixes applied ({fixStep.ModelName})");
                }
                else
                {
                    OnOutput?.Invoke($"  ⚠ {issues.Count} issue(s) remain after {maxReviewCycles} cycles");
                    result.NeedsMoreWork = true;
                }
            }

            // Optional: CHALLENGER finds edge cases (if enabled)
            if (_collaborationConfig.Verification?.EnableChallenger == true && !result.NeedsMoreWork)
            {
                OnOutput?.Invoke("");
                OnOutput?.Invoke("▶ Step 4: [Challenger] Finding edge cases and potential issues...");

                var challengerStep = await RunAgentStepAsync(
                    AgentRole.Challenger,
                    BuildTaskChallengerPrompt(taskPrompt, result.ImplementerOutput!, result.ReviewerFeedback!),
                    ct,
                    stepLabel: "Challenger");

                var challenges = ExtractChallengePoints(challengerStep.Response!);
                if (challenges.Count > 0)
                {
                    OnOutput?.Invoke($"  ⚠ Challenger raised {challenges.Count} point(s)");
                    result.IssuesFound.AddRange(challenges);
                }
                else
                {
                    OnOutput?.Invoke($"  ✓ No critical issues found ({challengerStep.ModelName})");
                }
            }

            result.Success = !result.NeedsMoreWork;
            _currentWorkflow.FinalOutput = result.ImplementerOutput;
            _currentWorkflow.Status = result.Success ? WorkflowStatus.Completed : WorkflowStatus.InProgress;
            _currentWorkflow.CompletedAt = DateTime.UtcNow;

            OnOutput?.Invoke("");
            OnOutput?.Invoke($"╔══════════════════════════════════════════════════════════════╗");
            OnOutput?.Invoke($"║  Task Workflow Complete: {(result.Success ? "SUCCESS" : "NEEDS MORE WORK"),-30} ║");
            OnOutput?.Invoke($"╚══════════════════════════════════════════════════════════════╝");
            OnOutput?.Invoke("");

            OnWorkflowCompleted?.Invoke(_currentWorkflow);
            return result;
        }
        catch (Exception ex)
        {
            _currentWorkflow.Status = WorkflowStatus.Failed;
            _currentWorkflow.Error = ex.Message;
            _currentWorkflow.CompletedAt = DateTime.UtcNow;
            OnError?.Invoke($"Task workflow failed: {ex.Message}");
            result.Success = false;
            return result;
        }
    }

    private string BuildTaskPlannerPrompt(string taskPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Task Analysis and Implementation Planning");
        sb.AppendLine();
        sb.AppendLine("You are the PLANNER agent. Analyze this task and create a detailed implementation plan.");
        sb.AppendLine();
        sb.AppendLine("### Task:");
        sb.AppendLine(taskPrompt);
        sb.AppendLine();
        sb.AppendLine("### Your Analysis Should Include:");
        sb.AppendLine("1. **Understanding**: What exactly needs to be done?");
        sb.AppendLine("2. **Files Affected**: Which files will need changes?");
        sb.AppendLine("3. **Implementation Steps**: Ordered list of specific changes");
        sb.AppendLine("4. **Edge Cases**: What edge cases should be handled?");
        sb.AppendLine("5. **Testing**: How should this be tested?");
        sb.AppendLine("6. **Risks**: What could go wrong?");
        sb.AppendLine();
        sb.AppendLine("Be specific and actionable. The Implementer agent will use this plan.");

        return sb.ToString();
    }

    private string BuildImplementerPrompt(string taskPrompt, string plannerAnalysis)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Implementation Task");
        sb.AppendLine();
        sb.AppendLine("You are the IMPLEMENTER agent. Execute the following task based on the Planner's analysis.");
        sb.AppendLine();
        sb.AppendLine("### Original Task:");
        sb.AppendLine(taskPrompt);
        sb.AppendLine();
        sb.AppendLine("### Planner's Analysis:");
        sb.AppendLine(plannerAnalysis);
        sb.AppendLine();
        sb.AppendLine("### Your Task:");
        sb.AppendLine("1. Implement the changes according to the plan");
        sb.AppendLine("2. Handle edge cases identified by the Planner");
        sb.AppendLine("3. Add appropriate error handling");
        sb.AppendLine("4. Write or update tests if appropriate");
        sb.AppendLine();
        sb.AppendLine("Execute the implementation now.");

        return sb.ToString();
    }

    private string BuildTaskReviewerPrompt(string taskPrompt, string plannerAnalysis, string implementerOutput, List<string> filesModified)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Code Review");
        sb.AppendLine();
        sb.AppendLine("You are the REVIEWER agent. Review the implementation for correctness and quality.");
        sb.AppendLine();
        sb.AppendLine("### Original Task:");
        sb.AppendLine(taskPrompt);
        sb.AppendLine();
        sb.AppendLine("### Planner's Plan:");
        sb.AppendLine(plannerAnalysis.Length > 1000 ? plannerAnalysis[..1000] + "..." : plannerAnalysis);
        sb.AppendLine();
        sb.AppendLine("### Implementation Output:");
        sb.AppendLine(implementerOutput.Length > 2000 ? implementerOutput[..2000] + "..." : implementerOutput);
        sb.AppendLine();

        if (filesModified.Count > 0)
        {
            sb.AppendLine("### Files Modified:");
            foreach (var file in filesModified.Take(20))
            {
                sb.AppendLine($"  - {file}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("### Review Checklist:");
        sb.AppendLine("1. Does the implementation match the task requirements?");
        sb.AppendLine("2. Are edge cases handled properly?");
        sb.AppendLine("3. Is error handling appropriate?");
        sb.AppendLine("4. Are there any bugs or issues?");
        sb.AppendLine("5. Is the code clean and maintainable?");
        sb.AppendLine();
        sb.AppendLine("### Response Format:");
        sb.AppendLine("If approved: State 'APPROVED' and list what was done well.");
        sb.AppendLine("If issues found: List each issue with severity (CRITICAL/HIGH/MEDIUM/LOW) and how to fix.");

        return sb.ToString();
    }

    private string BuildFixPrompt(string taskPrompt, string previousOutput, string reviewerFeedback)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Address Review Feedback");
        sb.AppendLine();
        sb.AppendLine("You are the IMPLEMENTER agent. The Reviewer found issues with your implementation.");
        sb.AppendLine();
        sb.AppendLine("### Original Task:");
        sb.AppendLine(taskPrompt);
        sb.AppendLine();
        sb.AppendLine("### Reviewer Feedback:");
        sb.AppendLine(reviewerFeedback);
        sb.AppendLine();
        sb.AppendLine("### Your Task:");
        sb.AppendLine("Address ALL issues raised by the reviewer. Make the necessary fixes.");

        return sb.ToString();
    }

    private string BuildTaskChallengerPrompt(string taskPrompt, string implementation, string reviewerFeedback)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Challenge the Implementation");
        sb.AppendLine();
        sb.AppendLine("You are the CHALLENGER agent. Find edge cases and issues others may have missed.");
        sb.AppendLine();
        sb.AppendLine("### Task:");
        sb.AppendLine(taskPrompt);
        sb.AppendLine();
        sb.AppendLine("### Implementation (summary):");
        sb.AppendLine(implementation.Length > 1500 ? implementation[..1500] + "..." : implementation);
        sb.AppendLine();
        sb.AppendLine("### Questions to Consider:");
        sb.AppendLine("1. What happens with unexpected input?");
        sb.AppendLine("2. Are there race conditions or concurrency issues?");
        sb.AppendLine("3. How does this handle failure scenarios?");
        sb.AppendLine("4. Are there security implications?");
        sb.AppendLine("5. What about performance at scale?");
        sb.AppendLine();
        sb.AppendLine("List specific concerns as bullet points. Say 'NO_ISSUES' if everything looks good.");

        return sb.ToString();
    }

    private List<string> ExtractModifiedFiles(string output)
    {
        var files = new List<string>();
        // Look for common patterns in output indicating file modifications
        var patterns = new[]
        {
            @"(?:Created|Modified|Updated|Wrote|Saved)\s+[`']?([^`'\n]+\.\w+)[`']?",
            @"(?:File|Writing to)\s+[`']?([^`'\n]+\.\w+)[`']?",
            @"✓\s+([^`'\n]+\.\w+)"
        };

        foreach (var pattern in patterns)
        {
            var matches = Regex.Matches(output, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    var file = match.Groups[1].Value.Trim();
                    if (!files.Contains(file))
                        files.Add(file);
                }
            }
        }

        return files;
    }

    private List<string> ExtractReviewIssues(string reviewOutput)
    {
        var issues = new List<string>();

        // Look for severity markers
        var severityPattern = @"\[(CRITICAL|HIGH|MEDIUM)\].*?(?=\[(?:CRITICAL|HIGH|MEDIUM|LOW)|$)";
        var matches = Regex.Matches(reviewOutput, severityPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            issues.Add(match.Value.Trim());
        }

        // If no structured issues but also no APPROVED, consider it as having issues
        if (issues.Count == 0 && !reviewOutput.Contains("APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            // Look for bullet points or numbered items that might be issues
            var bulletPattern = @"^[\s]*[-•*]\s+(.+)$";
            var bulletMatches = Regex.Matches(reviewOutput, bulletPattern, RegexOptions.Multiline);
            foreach (Match match in bulletMatches.Take(5))
            {
                var item = match.Groups[1].Value.Trim();
                if (item.Length > 10 && !item.Contains("good", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(item);
                }
            }
        }

        return issues;
    }

    #endregion

    #region Verification Workflow

    /// <summary>
    /// Run a multi-agent verification workflow (replaces single-model verification)
    /// </summary>
    public async Task<WorkflowVerificationResult> RunVerificationWorkflowAsync(
        string completionContext,
        List<string>? changedFiles = null,
        CancellationToken ct = default)
    {
        var verificationConfig = _collaborationConfig.Verification ?? new VerificationWorkflowConfig();

        // Reset model rotation for fresh provider distribution
        ResetRoleRotation();

        _currentWorkflow = new CollaborationWorkflow
        {
            Type = WorkflowType.Verification,
            OriginalRequest = "Verify completion",
            ContextFiles = changedFiles ?? new List<string>(),
            Status = WorkflowStatus.InProgress,
            StartedAt = DateTime.UtcNow
        };
        OnWorkflowStarted?.Invoke(_currentWorkflow);

        try
        {
            var reviewerCount = verificationConfig.ReviewerCount;
            OnOutput?.Invoke($"▶ Starting verification workflow with {reviewerCount} reviewer(s)...");

            // Step 1: Run multiple reviewers IN PARALLEL
            var reviewTasks = new List<Task<WorkflowStep>>();
            for (int i = 0; i < reviewerCount; i++)
            {
                var reviewerIndex = i;
                reviewTasks.Add(RunAgentStepAsync(
                    AgentRole.Reviewer,
                    BuildVerificationPrompt(completionContext, changedFiles, reviewerIndex),
                    ct,
                    stepLabel: $"Verifier {i + 1}"));
            }

            OnOutput?.Invoke($"  Running {reviewerCount} verifiers in parallel...");
            var reviewSteps = await Task.WhenAll(reviewTasks);
            OnOutput?.Invoke($"  All verifiers complete");

            // Parse and consolidate findings
            var allFindings = new List<ReviewFinding>();
            var approvingReviewers = 0;
            foreach (var step in reviewSteps)
            {
                var findings = ParseReviewFindings(step.Response!, step.ModelName);
                allFindings.AddRange(findings);

                // Check if this reviewer approved (no critical/high findings)
                var hasBlockingIssues = findings.Any(f =>
                    f.Severity == ReviewSeverity.Critical ||
                    f.Severity == ReviewSeverity.High);

                if (!hasBlockingIssues)
                    approvingReviewers++;
            }

            var consolidatedFindings = ConsolidateFindings(allFindings);
            OnOutput?.Invoke($"  Found {consolidatedFindings.Count} unique issues ({approvingReviewers}/{reviewerCount} approved)");

            // Step 2: Optional challenger pass
            List<string> challengePoints = new();
            if (verificationConfig.EnableChallenger)
            {
                OnOutput?.Invoke("  Running challenger to find edge cases...");
                var challengerStep = await RunAgentStepAsync(
                    AgentRole.Challenger,
                    BuildVerificationChallengerPrompt(completionContext, consolidatedFindings),
                    ct,
                    stepLabel: "Verification Challenger");

                challengePoints = ExtractChallengePoints(challengerStep.Response!);
                if (challengePoints.Count > 0)
                {
                    OnOutput?.Invoke($"  Challenger raised {challengePoints.Count} point(s)");
                }
            }

            // Determine if verification passed
            var hasBlockingFindings = consolidatedFindings.Any(f =>
                f.Severity == ReviewSeverity.Critical ||
                f.Severity == ReviewSeverity.High);

            var passed = !hasBlockingFindings &&
                (!verificationConfig.RequireUnanimous || approvingReviewers == reviewerCount);

            // Extract required changes
            var requiredChanges = consolidatedFindings
                .Where(f => f.Severity <= ReviewSeverity.High)
                .Select(f => f.Suggestion ?? f.Description)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            requiredChanges.AddRange(challengePoints.Where(c =>
                c.Contains("must", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("should", StringComparison.OrdinalIgnoreCase)));

            var summary = GenerateVerificationSummary(passed, approvingReviewers, reviewerCount,
                consolidatedFindings, challengePoints);

            _currentWorkflow.FinalOutput = summary;
            _currentWorkflow.Status = passed ? WorkflowStatus.Completed : WorkflowStatus.Failed;
            _currentWorkflow.CompletedAt = DateTime.UtcNow;

            var result = new WorkflowVerificationResult
            {
                Passed = passed,
                ApprovingReviewers = approvingReviewers,
                TotalReviewers = reviewerCount,
                Findings = consolidatedFindings,
                RequiredChanges = requiredChanges,
                Summary = summary,
                ChallengePoints = challengePoints,
                Workflow = _currentWorkflow
            };

            OnOutput?.Invoke($"✓ Verification workflow: {(passed ? "PASSED" : "NEEDS WORK")} ({approvingReviewers}/{reviewerCount} approved)");
            OnWorkflowCompleted?.Invoke(_currentWorkflow);

            return result;
        }
        catch (Exception ex)
        {
            _currentWorkflow.Status = WorkflowStatus.Failed;
            _currentWorkflow.Error = ex.Message;
            _currentWorkflow.CompletedAt = DateTime.UtcNow;
            OnError?.Invoke($"Verification workflow failed: {ex.Message}");
            throw;
        }
    }

    private string BuildVerificationPrompt(string completionContext, List<string>? changedFiles, int reviewerIndex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Verification Review (Verifier #{reviewerIndex + 1})");
        sb.AppendLine();
        sb.AppendLine("The AI has indicated that the task is complete. Your job is to verify this claim.");
        sb.AppendLine();
        sb.AppendLine("### Context");
        sb.AppendLine(completionContext);
        sb.AppendLine();

        if (changedFiles?.Count > 0)
        {
            sb.AppendLine("### Changed Files");
            foreach (var file in changedFiles.Take(20))
            {
                sb.AppendLine($"  - {file}");
            }
            sb.AppendLine();
        }

        var focusAreas = _collaborationConfig.Verification?.FocusAreas ?? new List<ReviewFocus> { ReviewFocus.Full };
        sb.AppendLine("### Focus Areas");
        foreach (var focus in focusAreas)
        {
            sb.AppendLine($"  - {focus}");
        }
        sb.AppendLine();

        sb.AppendLine("### Your Task");
        sb.AppendLine("1. Review the changes and context");
        sb.AppendLine("2. Identify any issues that would prevent completion");
        sb.AppendLine("3. Check for edge cases, error handling, and tests");
        sb.AppendLine("4. Report findings in the standard format:");
        sb.AppendLine();
        sb.AppendLine("[SEVERITY] Category: Title");
        sb.AppendLine("Location: file:line");
        sb.AppendLine("Issue: Description");
        sb.AppendLine("Impact: Why it matters");
        sb.AppendLine("Suggestion: How to fix");
        sb.AppendLine();
        sb.AppendLine("Severities: CRITICAL (must fix), HIGH (should fix), MEDIUM, LOW, INFO");
        sb.AppendLine();
        sb.AppendLine("If you find NO blocking issues, state: VERIFICATION_APPROVED");

        return sb.ToString();
    }

    private string BuildVerificationChallengerPrompt(string completionContext, List<ReviewFinding> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Challenge the Completion Claim");
        sb.AppendLine();
        sb.AppendLine("Multiple reviewers have checked this work. Your job is to challenge their assessment.");
        sb.AppendLine();
        sb.AppendLine("### Context");
        sb.AppendLine(completionContext);
        sb.AppendLine();

        if (findings.Count > 0)
        {
            sb.AppendLine("### Findings from Reviewers");
            foreach (var finding in findings.Take(10))
            {
                sb.AppendLine($"- [{finding.Severity}] {finding.Title}: {finding.Description}");
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("### Reviewers found no issues");
            sb.AppendLine();
        }

        sb.AppendLine("### Your Task");
        sb.AppendLine("1. What edge cases might the reviewers have missed?");
        sb.AppendLine("2. Are there any integration concerns?");
        sb.AppendLine("3. Is error handling complete?");
        sb.AppendLine("4. Are the tests adequate?");
        sb.AppendLine();
        sb.AppendLine("List specific concerns as bullet points. Be constructive but thorough.");

        return sb.ToString();
    }

    private List<string> ExtractChallengePoints(string response)
    {
        var points = new List<string>();
        var pattern = @"[-*]\s*(.+?)(?:\n|$)";

        var matches = Regex.Matches(response, pattern);
        foreach (Match match in matches)
        {
            var point = match.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(point) &&
                point.Length > 10 &&
                !point.StartsWith("No ", StringComparison.OrdinalIgnoreCase))
            {
                points.Add(point);
            }
        }

        return points.Take(10).ToList();
    }

    private string GenerateVerificationSummary(
        bool passed,
        int approvingReviewers,
        int totalReviewers,
        List<ReviewFinding> findings,
        List<string> challengePoints)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Verification Summary");
        sb.AppendLine();
        sb.AppendLine($"**Result**: {(passed ? "✓ PASSED" : "✗ NEEDS WORK")}");
        sb.AppendLine($"**Approval**: {approvingReviewers}/{totalReviewers} reviewers approved");
        sb.AppendLine();

        if (findings.Count > 0)
        {
            var counts = findings.GroupBy(f => f.Severity).ToDictionary(g => g.Key, g => g.Count());
            sb.AppendLine("### Findings");
            sb.AppendLine($"- Critical: {counts.GetValueOrDefault(ReviewSeverity.Critical, 0)}");
            sb.AppendLine($"- High: {counts.GetValueOrDefault(ReviewSeverity.High, 0)}");
            sb.AppendLine($"- Medium: {counts.GetValueOrDefault(ReviewSeverity.Medium, 0)}");
            sb.AppendLine($"- Low/Info: {counts.GetValueOrDefault(ReviewSeverity.Low, 0) + counts.GetValueOrDefault(ReviewSeverity.Info, 0)}");
            sb.AppendLine();
        }

        if (challengePoints.Count > 0)
        {
            sb.AppendLine("### Challenger Points");
            foreach (var point in challengePoints.Take(5))
            {
                sb.AppendLine($"- {point}");
            }
        }

        return sb.ToString();
    }

    #endregion

    #region Consensus Workflow

    /// <summary>
    /// Run a consensus workflow to gather multiple opinions (parallel)
    /// </summary>
    public async Task<ConsensusResult> RunConsensusWorkflowAsync(
        string proposal,
        List<string>? contextFiles = null,
        CancellationToken ct = default)
    {
        // Reset model rotation for fresh provider distribution
        ResetRoleRotation();

        _currentWorkflow = new CollaborationWorkflow
        {
            Type = WorkflowType.Consensus,
            OriginalRequest = proposal,
            ContextFiles = contextFiles ?? new List<string>(),
            Status = WorkflowStatus.InProgress,
            StartedAt = DateTime.UtcNow
        };
        OnWorkflowStarted?.Invoke(_currentWorkflow);

        try
        {
            var participants = _collaborationConfig.Consensus?.Participants ?? GetDefaultParticipants();
            OnOutput?.Invoke($"▶ Starting consensus workflow with {participants.Count} participants...");

            // Gather opinions IN PARALLEL (blinded - each only sees original proposal)
            var opinionTasks = participants.Select((p, i) =>
                GatherOpinionAsync(proposal, p, contextFiles, i, ct)).ToList();

            OnOutput?.Invoke($"  Gathering {participants.Count} opinions in parallel...");
            var opinions = (await Task.WhenAll(opinionTasks)).ToList();
            OnOutput?.Invoke($"  All opinions collected");

            // Synthesize (always sequential - needs all opinions)
            string? synthesis = null;
            if (_collaborationConfig.Consensus?.EnableSynthesis == true)
            {
                OnOutput?.Invoke("  Synthesizing consensus...");
                var synthesisStep = await RunAgentStepAsync(
                    AgentRole.Synthesizer,
                    BuildConsensusSynthesisPrompt(proposal, opinions),
                    ct);
                synthesis = synthesisStep.Response;
            }

            _currentWorkflow.FinalOutput = synthesis;
            _currentWorkflow.Status = WorkflowStatus.Completed;
            _currentWorkflow.CompletedAt = DateTime.UtcNow;

            var result = new ConsensusResult
            {
                Proposal = proposal,
                Opinions = opinions,
                Synthesis = synthesis,
                Agreements = ExtractAgreements(opinions),
                Disagreements = ExtractDisagreements(opinions),
                Recommendations = ExtractRecommendations(synthesis),
                Workflow = _currentWorkflow
            };

            OnOutput?.Invoke($"✓ Consensus workflow complete ({_currentWorkflow.TotalDuration?.TotalSeconds:F1}s)");
            OnWorkflowCompleted?.Invoke(_currentWorkflow);

            return result;
        }
        catch (Exception ex)
        {
            _currentWorkflow.Status = WorkflowStatus.Failed;
            _currentWorkflow.Error = ex.Message;
            _currentWorkflow.CompletedAt = DateTime.UtcNow;
            OnError?.Invoke($"Consensus workflow failed: {ex.Message}");
            throw;
        }
    }

    private async Task<ConsensusOpinion> GatherOpinionAsync(
        string proposal,
        ConsensusParticipant participant,
        List<string>? contextFiles,
        int index,
        CancellationToken ct)
    {
        var role = GetRoleForStance(participant.Stance);
        var prompt = BuildConsensusPrompt(proposal, participant, contextFiles);

        var step = await RunAgentStepAsync(
            role,
            prompt,
            ct,
            model: participant.Model,
            stepLabel: participant.DisplayName);

        return new ConsensusOpinion
        {
            Model = participant.Model.DisplayName,
            Stance = participant.Stance,
            Analysis = step.Response!,
            KeyPoints = ExtractKeyPoints(step.Response!),
            Concerns = ExtractConcerns(step.Response!),
            Strengths = ExtractStrengths(step.Response!)
        };
    }

    #endregion

    #region Agent Execution

    private async Task<WorkflowStep> RunAgentStepAsync(
        AgentRole role,
        string prompt,
        CancellationToken ct,
        ModelSpec? model = null,
        bool useExpertModel = false,
        string? stepLabel = null)
    {
        // Acquire semaphore for parallel execution limiting
        await _agentSemaphore.WaitAsync(ct);

        var step = new WorkflowStep
        {
            StepNumber = _currentWorkflow!.Steps.Count + 1,
            Agent = role,
            Prompt = prompt,
            Status = WorkflowStepStatus.InProgress,
            StartedAt = DateTime.UtcNow
        };

        lock (_currentWorkflow.Steps)
        {
            _currentWorkflow.Steps.Add(step);
        }

        OnStepStarted?.Invoke(step);

        try
        {
            // Get model for this agent
            var agentModel = model ?? GetModelForRole(role, useExpertModel);
            var systemPrompt = GetSystemPrompt(role);
            step.ModelName = agentModel?.DisplayName ?? _config.Provider.ToString();

            // Build full prompt with system prompt
            var fullPrompt = BuildFullPrompt(systemPrompt, prompt);

            // Run agent using AIProcess
            var result = await RunAgentProcessAsync(agentModel, fullPrompt, ct);

            step.Response = result.Output;
            step.Duration = DateTime.UtcNow - step.StartedAt!.Value;
            step.CompletedAt = DateTime.UtcNow;
            step.Status = result.Success ? WorkflowStepStatus.Completed : WorkflowStepStatus.Failed;

            if (!result.Success)
            {
                step.Error = result.Error;
            }

            return step;
        }
        catch (Exception ex)
        {
            step.Error = ex.Message;
            step.Duration = DateTime.UtcNow - step.StartedAt!.Value;
            step.CompletedAt = DateTime.UtcNow;
            step.Status = WorkflowStepStatus.Failed;
            throw;
        }
        finally
        {
            OnStepCompleted?.Invoke(step);
            _agentSemaphore.Release();
        }
    }

    private async Task<AIResult> RunAgentProcessAsync(
        ModelSpec? model,
        string prompt,
        CancellationToken ct)
    {
        var providerConfig = model?.ToProviderConfig() ?? _config.ProviderConfig;
        var dynamicConfig = _config with { ProviderConfig = providerConfig };

        using var process = new AIProcess(dynamicConfig);

        var output = new StringBuilder();
        process.OnOutput += line => output.AppendLine(line);
        process.OnError += line => OnError?.Invoke($"[Agent] {line}");

        return await process.RunAsync(prompt, ct);
    }

    #endregion

    #region Prompt Building

    private string BuildFullPrompt(string systemPrompt, string userPrompt)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            sb.AppendLine("--- SYSTEM INSTRUCTIONS ---");
            sb.AppendLine(systemPrompt);
            sb.AppendLine();
        }

        sb.AppendLine("--- YOUR TASK ---");
        sb.AppendLine(userPrompt);

        return sb.ToString();
    }

    private string BuildPlannerPrompt(string featureRequest, List<string>? contextFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Create a detailed implementation plan for the following feature:");
        sb.AppendLine();
        sb.AppendLine(featureRequest);
        sb.AppendLine();

        if (contextFiles?.Count > 0)
        {
            sb.AppendLine("Context files to consider:");
            foreach (var file in contextFiles.Take(10))
            {
                sb.AppendLine($"  - {file}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Provide:");
        sb.AppendLine("1. Overview of the feature");
        sb.AppendLine("2. Prerequisites and dependencies");
        sb.AppendLine("3. Step-by-step implementation plan with complexity estimates");
        sb.AppendLine("4. Key technical decisions");
        sb.AppendLine("5. Potential risks and mitigations");
        sb.AppendLine("6. Success criteria");

        return sb.ToString();
    }

    private string BuildChallengerPrompt(string initialSpec, string originalRequest)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Review and challenge the following implementation plan:");
        sb.AppendLine();
        sb.AppendLine("ORIGINAL REQUEST:");
        sb.AppendLine(originalRequest);
        sb.AppendLine();
        sb.AppendLine("PROPOSED PLAN:");
        sb.AppendLine(initialSpec);
        sb.AppendLine();
        sb.AppendLine("Your task:");
        sb.AppendLine("1. Identify potential issues, risks, or oversights");
        sb.AppendLine("2. Question any assumptions that seem flawed");
        sb.AppendLine("3. Point out edge cases that might be missed");
        sb.AppendLine("4. Suggest alternatives where warranted");
        sb.AppendLine("5. Acknowledge what's good about the plan");
        sb.AppendLine();
        sb.AppendLine("Be constructive - offer solutions alongside problems.");

        return sb.ToString();
    }

    private string BuildSynthesizerPrompt(string originalRequest, string initialSpec, string? challenges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Synthesize a final implementation plan from the following:");
        sb.AppendLine();
        sb.AppendLine("ORIGINAL REQUEST:");
        sb.AppendLine(originalRequest);
        sb.AppendLine();
        sb.AppendLine("INITIAL PLAN:");
        sb.AppendLine(initialSpec);

        if (!string.IsNullOrEmpty(challenges))
        {
            sb.AppendLine();
            sb.AppendLine("CHALLENGES/FEEDBACK:");
            sb.AppendLine(challenges);
        }

        sb.AppendLine();
        sb.AppendLine("Create the final spec that:");
        sb.AppendLine("1. Incorporates valid concerns");
        sb.AppendLine("2. Resolves any conflicts");
        sb.AppendLine("3. Maintains the original intent");
        sb.AppendLine("4. Documents key decisions");
        sb.AppendLine();
        sb.AppendLine("Format the final plan with clear sections and actionable tasks.");

        return sb.ToString();
    }

    private string BuildReviewPrompt(List<string> files, string? commitRange, int reviewerIndex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Perform a thorough code review (Reviewer #{reviewerIndex + 1}).");
        sb.AppendLine();
        sb.AppendLine("FILES TO REVIEW:");
        foreach (var file in files.Take(20))
        {
            sb.AppendLine($"  - {file}");
        }
        if (files.Count > 20)
        {
            sb.AppendLine($"  ... and {files.Count - 20} more");
        }
        sb.AppendLine();

        var focusAreas = _collaborationConfig.Review?.FocusAreas ?? new List<ReviewFocus> { ReviewFocus.Full };
        sb.AppendLine("FOCUS AREAS:");
        foreach (var focus in focusAreas)
        {
            sb.AppendLine($"  - {focus}");
        }
        sb.AppendLine();

        sb.AppendLine("For each finding, use this format:");
        sb.AppendLine("[SEVERITY] Category: Title");
        sb.AppendLine("Location: file:line");
        sb.AppendLine("Issue: Description");
        sb.AppendLine("Impact: Why it matters");
        sb.AppendLine("Suggestion: How to fix");
        sb.AppendLine();
        sb.AppendLine("Severities: CRITICAL, HIGH, MEDIUM, LOW, INFO");

        return sb.ToString();
    }

    private string BuildValidationPrompt(List<ReviewFinding> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Validate the following CRITICAL findings from code review:");
        sb.AppendLine();

        var criticalFindings = findings.Where(f => f.Severity == ReviewSeverity.Critical).ToList();
        foreach (var finding in criticalFindings)
        {
            sb.AppendLine($"[{finding.Severity}] {finding.Category}: {finding.Title}");
            sb.AppendLine($"Location: {finding.Location}");
            sb.AppendLine($"Issue: {finding.Description}");
            sb.AppendLine();
        }

        sb.AppendLine("For each finding, verify if it's a genuine issue:");
        sb.AppendLine("- CONFIRMED: Issue is valid and critical");
        sb.AppendLine("- DOWNGRADE: Issue exists but not critical (suggest new severity)");
        sb.AppendLine("- FALSE_POSITIVE: Not actually an issue");

        return sb.ToString();
    }

    private string BuildConsensusPrompt(string proposal, ConsensusParticipant participant, List<string>? contextFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analyze the following proposal:");
        sb.AppendLine();
        sb.AppendLine(proposal);
        sb.AppendLine();

        if (contextFiles?.Count > 0)
        {
            sb.AppendLine("Context files:");
            foreach (var file in contextFiles.Take(5))
            {
                sb.AppendLine($"  - {file}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"YOUR STANCE: {participant.Stance.ToString().ToUpper()}");
        sb.AppendLine();

        switch (participant.Stance)
        {
            case ConsensusStance.For:
                sb.AppendLine("Focus on: Genuine strengths, opportunities, and why this approach works.");
                sb.AppendLine("But maintain integrity - don't defend genuinely flawed ideas.");
                break;
            case ConsensusStance.Against:
                sb.AppendLine("Focus on: Risks, issues, edge cases, and potential problems.");
                sb.AppendLine("But be fair - don't oppose sound proposals just for the sake of opposition.");
                break;
            case ConsensusStance.Neutral:
                sb.AppendLine("Provide balanced analysis. Accurately reflect reality.");
                sb.AppendLine("Don't create artificial 50/50 splits - take positions where warranted.");
                break;
        }

        sb.AppendLine();
        sb.AppendLine("Structure your response:");
        sb.AppendLine("1. Key Points (main observations)");
        sb.AppendLine("2. Concerns (if any)");
        sb.AppendLine("3. Strengths (if any)");
        sb.AppendLine("4. Recommendation");

        return sb.ToString();
    }

    private string BuildConsensusSynthesisPrompt(string proposal, List<ConsensusOpinion> opinions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Synthesize the following opinions into actionable recommendations:");
        sb.AppendLine();
        sb.AppendLine("PROPOSAL:");
        sb.AppendLine(proposal);
        sb.AppendLine();

        foreach (var opinion in opinions)
        {
            sb.AppendLine($"--- {opinion.Model} ({opinion.Stance}) ---");
            sb.AppendLine(opinion.Analysis);
            sb.AppendLine();
        }

        sb.AppendLine("Create a synthesis that:");
        sb.AppendLine("1. Identifies key AGREEMENTS across opinions");
        sb.AppendLine("2. Identifies key DISAGREEMENTS and their root causes");
        sb.AppendLine("3. Provides SPECIFIC, ACTIONABLE recommendations");
        sb.AppendLine("4. Highlights CRITICAL RISKS that need addressing");

        return sb.ToString();
    }

    #endregion

    #region Parsing and Extraction

    private List<ReviewFinding> ParseReviewFindings(string response, string reviewer)
    {
        var findings = new List<ReviewFinding>();
        var pattern = @"\[(\w+)\]\s*(\w+):\s*(.+?)\nLocation:\s*(.+?)\nIssue:\s*(.+?)(?:\nImpact:\s*(.+?))?(?:\nSuggestion:\s*(.+?))?(?=\n\[|\n---|\z)";

        var matches = Regex.Matches(response, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            if (Enum.TryParse<ReviewSeverity>(match.Groups[1].Value, true, out var severity))
            {
                var location = match.Groups[4].Value.Trim();
                var colonIndex = location.LastIndexOf(':');

                findings.Add(new ReviewFinding
                {
                    Severity = severity,
                    Category = match.Groups[2].Value.Trim(),
                    Title = match.Groups[3].Value.Trim(),
                    FilePath = colonIndex > 0 ? location[..colonIndex] : location,
                    LineNumber = colonIndex > 0 && int.TryParse(location[(colonIndex + 1)..], out var line) ? line : null,
                    Description = match.Groups[5].Value.Trim(),
                    Impact = match.Groups[6].Success ? match.Groups[6].Value.Trim() : null,
                    Suggestion = match.Groups[7].Success ? match.Groups[7].Value.Trim() : null,
                    FoundBy = reviewer
                });
            }
        }

        return findings;
    }

    private List<ReviewFinding> ConsolidateFindings(List<ReviewFinding> findings)
    {
        // Group by file + approximate line + category to find duplicates
        return findings
            .GroupBy(f => $"{f.FilePath}:{f.LineNumber / 5}:{f.Category}")
            .Select(g => g.OrderByDescending(f => f.Severity).First())
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.FilePath)
            .ToList();
    }

    private List<ReviewFinding> ValidateFindings(List<ReviewFinding> findings, string validationResponse)
    {
        // Parse validation response and update findings
        foreach (var finding in findings.Where(f => f.Severity == ReviewSeverity.Critical))
        {
            if (validationResponse.Contains($"{finding.Id}") || validationResponse.Contains(finding.Title))
            {
                if (validationResponse.Contains("FALSE_POSITIVE"))
                {
                    finding.ExpertValidated = false;
                }
                else
                {
                    finding.ExpertValidated = true;
                }
            }
        }
        return findings;
    }

    private string GenerateReviewSummary(List<ReviewFinding> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Code Review Summary");
        sb.AppendLine();

        var counts = findings.GroupBy(f => f.Severity).ToDictionary(g => g.Key, g => g.Count());
        sb.AppendLine($"- Critical: {counts.GetValueOrDefault(ReviewSeverity.Critical, 0)}");
        sb.AppendLine($"- High: {counts.GetValueOrDefault(ReviewSeverity.High, 0)}");
        sb.AppendLine($"- Medium: {counts.GetValueOrDefault(ReviewSeverity.Medium, 0)}");
        sb.AppendLine($"- Low: {counts.GetValueOrDefault(ReviewSeverity.Low, 0)}");

        var approved = !findings.Any(f => f.Severity <= ReviewSeverity.High);
        sb.AppendLine();
        sb.AppendLine($"**Verdict**: {(approved ? "APPROVED" : "NEEDS WORK")}");

        return sb.ToString();
    }

    private List<SpecTask> ExtractTasks(string spec)
    {
        var tasks = new List<SpecTask>();
        var pattern = @"(?:^|\n)\s*(\d+)\.\s*\*\*(.+?)\*\*\s*(?:\((.+?)\))?\s*\n([\s\S]+?)(?=\n\s*\d+\.\s*\*\*|\z)";

        var matches = Regex.Matches(spec, pattern);
        foreach (Match match in matches)
        {
            tasks.Add(new SpecTask
            {
                Order = int.TryParse(match.Groups[1].Value, out var order) ? order : tasks.Count + 1,
                Title = match.Groups[2].Value.Trim(),
                Complexity = match.Groups[3].Success ? match.Groups[3].Value.Trim() : "medium",
                Description = match.Groups[4].Value.Trim()
            });
        }

        return tasks;
    }

    private List<string> ExtractKeyPoints(string analysis) =>
        ExtractListItems(analysis, @"Key\s*Points?:?\s*\n([\s\S]+?)(?=\n(?:Concerns?|Strengths?|Recommendation)|$)");

    private List<string> ExtractConcerns(string analysis) =>
        ExtractListItems(analysis, @"Concerns?:?\s*\n([\s\S]+?)(?=\n(?:Strengths?|Recommendation)|$)");

    private List<string> ExtractStrengths(string analysis) =>
        ExtractListItems(analysis, @"Strengths?:?\s*\n([\s\S]+?)(?=\n(?:Recommendation)|$)");

    private List<string> ExtractAgreements(List<ConsensusOpinion> opinions)
    {
        // Find points mentioned by multiple opinions
        var allPoints = opinions.SelectMany(o => o.KeyPoints).ToList();
        return allPoints
            .GroupBy(p => p.ToLowerInvariant().GetHashCode() / 100) // Fuzzy grouping
            .Where(g => g.Count() > 1)
            .Select(g => g.First())
            .ToList();
    }

    private List<string> ExtractDisagreements(List<ConsensusOpinion> opinions)
    {
        var forPoints = opinions.Where(o => o.Stance == ConsensusStance.For).SelectMany(o => o.Strengths);
        var againstPoints = opinions.Where(o => o.Stance == ConsensusStance.Against).SelectMany(o => o.Concerns);
        // Disagreements are where For sees strength and Against sees concern
        return againstPoints.Take(5).ToList();
    }

    private List<string> ExtractRecommendations(string? synthesis)
    {
        if (string.IsNullOrEmpty(synthesis)) return new List<string>();
        return ExtractListItems(synthesis, @"[Rr]ecommendation[s]?:?\s*\n([\s\S]+?)(?=\n(?:##|\z))");
    }

    private List<string> ExtractListItems(string text, string sectionPattern)
    {
        var match = Regex.Match(text, sectionPattern);
        if (!match.Success) return new List<string>();

        var section = match.Groups[1].Value;
        return Regex.Matches(section, @"[-*]\s*(.+)")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    #endregion

    #region Helpers

    private void LoadSystemPrompts()
    {
        var promptsDir = Path.Combine(_config.TargetDirectory, _collaborationConfig.PromptsDirectory);

        foreach (AgentRole role in Enum.GetValues<AgentRole>())
        {
            var fileName = $"{role.ToString().ToLowerInvariant()}.md";
            var filePath = Path.Combine(promptsDir, fileName);

            if (File.Exists(filePath))
            {
                _systemPrompts[role] = File.ReadAllText(filePath);
            }
            else
            {
                _systemPrompts[role] = GetDefaultSystemPrompt(role);
            }
        }
    }

    private string GetSystemPrompt(AgentRole role)
    {
        var agentConfig = _collaborationConfig.GetAgentConfig(role);

        var basePrompt = _systemPrompts.GetValueOrDefault(role, GetDefaultSystemPrompt(role));

        if (!string.IsNullOrEmpty(agentConfig.AdditionalInstructions))
        {
            basePrompt += "\n\n" + agentConfig.AdditionalInstructions;
        }

        return basePrompt;
    }

    private string GetDefaultSystemPrompt(AgentRole role) => role switch
    {
        AgentRole.Planner => "You are a technical planner. Break down features into clear, actionable implementation steps.",
        AgentRole.Challenger => "You are a critical reviewer. Identify issues, question assumptions, but be constructive.",
        AgentRole.Advocate => "You are an advocate. Highlight strengths and opportunities while maintaining objectivity.",
        AgentRole.Reviewer => "You are a senior code reviewer. Find issues, suggest fixes, and ensure quality.",
        AgentRole.Synthesizer => "You synthesize multiple perspectives into clear, actionable recommendations.",
        AgentRole.Implementer => "You are a software developer. Write clean, working code.",
        _ => ""
    };

    /// <summary>
    /// Track role call counts for global round-robin rotation
    /// </summary>
    private readonly Dictionary<AgentRole, int> _roleCallCounts = new();

    private ModelSpec? GetModelForRole(AgentRole role, bool useExpert = false)
    {
        // Determine required tier for this role
        var requiredTier = useExpert ? ModelTier.Expert : GetTierForRole(role);

        // Get all available models for this role, filtered by tier
        var allModels = GetAllModelsForRole(role);
        var tieredModels = allModels.Where(m => m.EffectiveTier == requiredTier).ToList();

        // Fall back to all models if no models match the tier
        if (tieredModels.Count == 0)
            tieredModels = allModels;

        if (tieredModels.Count == 0)
            return null;

        // Get current rotation index for this tier+role combination
        var tierKey = $"{role}_{requiredTier}";
        if (!_tierCallCounts.TryGetValue(tierKey, out var callCount))
            callCount = 0;

        var modelIndex = callCount % tieredModels.Count;
        _tierCallCounts[tierKey] = callCount + 1;

        var selectedModel = tieredModels[modelIndex];
        OnOutput?.Invoke($"  [Model] Selected {selectedModel.Provider}:{selectedModel.DisplayName} ({selectedModel.EffectiveTier} tier) for {role}");

        return selectedModel;
    }

    /// <summary>
    /// Track tier+role call counts for rotation
    /// </summary>
    private readonly Dictionary<string, int> _tierCallCounts = new();

    /// <summary>
    /// Get the recommended tier for each agent role
    /// </summary>
    private static ModelTier GetTierForRole(AgentRole role) => role switch
    {
        // Expert tier: Roles that need deep understanding and complex reasoning
        AgentRole.Planner => ModelTier.Expert,
        AgentRole.Synthesizer => ModelTier.Expert,

        // Capable tier: Implementation and thorough review
        AgentRole.Implementer => ModelTier.Capable,
        AgentRole.Reviewer => ModelTier.Capable,
        AgentRole.Advocate => ModelTier.Capable,

        // Fast tier: Quick checks and challenges (volume over depth)
        AgentRole.Challenger => ModelTier.Fast,

        _ => ModelTier.Capable
    };

    /// <summary>
    /// Random generator for shuffling model order within providers
    /// </summary>
    private static readonly Random _random = new();

    /// <summary>
    /// Cache of interleaved models per role (regenerated per workflow run)
    /// </summary>
    private readonly Dictionary<AgentRole, List<ModelSpec>> _interleavedModelsCache = new();

    /// <summary>
    /// Get ALL available models for a role
    ///
    /// Priority:
    /// 1. Role-specific models from CollaborationConfig (if configured)
    /// 2. ALL available models from MultiModelConfig (global pool)
    ///
    /// Models are interleaved by PROVIDER first to distribute API usage:
    /// [Claude1, Gemini1, Codex1, Claude2, Gemini2, Codex2, ...]
    /// This prevents using up one provider's API limits before touching others
    /// </summary>
    private List<ModelSpec> GetAllModelsForRole(AgentRole role)
    {
        // Check cache first (built once per workflow run)
        if (_interleavedModelsCache.TryGetValue(role, out var cached))
            return cached;

        var models = new List<ModelSpec>();

        // First, try role-specific models from collaboration config
        var configs = _collaborationConfig.GetAgentConfigs(role);
        foreach (var config in configs)
        {
            // Add specific model if set
            if (config.Model != null)
                models.Add(config.Model);

            // Add all models from the pool
            if (config.ModelPool?.Count > 0)
                models.AddRange(config.ModelPool);
        }

        // If no role-specific models, use ALL available models from MultiModelConfig
        // This allows runtime tier-based selection from the global pool
        if (models.Count == 0 && _config.MultiModel?.Models.Count > 0)
        {
            models.AddRange(_config.MultiModel.Models);
            OnOutput?.Invoke($"  [Model] Using global model pool ({models.Count} models) for {role}");
        }

        // Deduplicate
        var uniqueModels = models.DistinctBy(m => m.DisplayName).ToList();

        // Group by provider, shuffle within each provider, then interleave
        var interleavedModels = InterleaveByProvider(uniqueModels);

        // Cache the interleaved list for this workflow run
        _interleavedModelsCache[role] = interleavedModels;

        return interleavedModels;
    }

    /// <summary>
    /// Interleave models by provider to spread API usage
    /// Returns: [Provider1-Model1, Provider2-Model1, Provider3-Model1, Provider1-Model2, ...]
    /// </summary>
    private static List<ModelSpec> InterleaveByProvider(List<ModelSpec> models)
    {
        if (models.Count <= 1)
            return models;

        // Group by provider
        var byProvider = models
            .GroupBy(m => m.Provider)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Shuffle models within each provider for variety
        foreach (var providerModels in byProvider.Values)
        {
            ShuffleList(providerModels);
        }

        // Shuffle provider order too
        var providers = byProvider.Keys.ToList();
        ShuffleList(providers);

        // Interleave: take one from each provider in round-robin fashion
        var result = new List<ModelSpec>();
        var indices = providers.ToDictionary(p => p, _ => 0);
        var totalModels = models.Count;

        while (result.Count < totalModels)
        {
            foreach (var provider in providers)
            {
                var providerModels = byProvider[provider];
                if (indices[provider] < providerModels.Count)
                {
                    result.Add(providerModels[indices[provider]]);
                    indices[provider]++;

                    if (result.Count >= totalModels)
                        break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Fisher-Yates shuffle for random order
    /// </summary>
    private static void ShuffleList<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Get the Nth model for a role (for explicit parallel execution)
    /// </summary>
    private ModelSpec? GetModelForRoleAt(AgentRole role, int index)
    {
        var allModels = GetAllModelsForRole(role);
        if (allModels.Count == 0)
            return null;

        // Wrap index if needed
        return allModels[index % allModels.Count];
    }

    /// <summary>
    /// Get the number of distinct models available for a role
    /// </summary>
    private int GetModelCountForRole(AgentRole role)
    {
        return GetAllModelsForRole(role).Count;
    }

    /// <summary>
    /// Get the number of agents configured for a role
    /// </summary>
    private int GetAgentCountForRole(AgentRole role)
    {
        return _collaborationConfig.GetAgentCount(role);
    }

    /// <summary>
    /// Reset role rotation counters and caches (call at start of new workflow)
    /// This ensures a fresh shuffle of providers/models for each workflow run
    /// </summary>
    private void ResetRoleRotation()
    {
        _roleCallCounts.Clear();
        _tierCallCounts.Clear();
        _interleavedModelsCache.Clear();
    }

    private AgentRole GetRoleForStance(ConsensusStance stance) => stance switch
    {
        ConsensusStance.For => AgentRole.Advocate,
        ConsensusStance.Against => AgentRole.Challenger,
        ConsensusStance.Neutral => AgentRole.Synthesizer,
        _ => AgentRole.Synthesizer
    };

    private List<ConsensusParticipant> GetDefaultParticipants()
    {
        // Default: use same model with different stances
        return new List<ConsensusParticipant>
        {
            new() { Stance = ConsensusStance.For, Label = "Advocate" },
            new() { Stance = ConsensusStance.Against, Label = "Challenger" },
            new() { Stance = ConsensusStance.Neutral, Label = "Neutral" }
        };
    }

    #endregion

    /// <summary>
    /// Reset model rotation for a new session (call at start of loop, not per iteration)
    /// This shuffles the provider order and resets all counters
    /// </summary>
    public void ResetForNewSession()
    {
        ResetRoleRotation();
        OnOutput?.Invoke("[Collaboration] Model rotation reset for new session");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _agentSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
