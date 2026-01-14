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

    private ModelSpec? GetModelForRole(AgentRole role, bool useExpert = false)
    {
        var agentConfig = _collaborationConfig.GetAgentConfig(role);
        return agentConfig.GetNextModel() ?? agentConfig.Model;
    }

    /// <summary>
    /// Get the Nth model for a role (for parallel execution with multiple agents)
    /// </summary>
    private ModelSpec? GetModelForRoleAt(AgentRole role, int index)
    {
        var configs = _collaborationConfig.GetAgentConfigs(role);
        if (index < configs.Count)
        {
            var config = configs[index];
            return config.Model ?? config.GetNextModel();
        }
        // Fall back to round-robin from first config's pool
        var firstConfig = configs.FirstOrDefault();
        return firstConfig?.GetNextModel() ?? firstConfig?.Model;
    }

    /// <summary>
    /// Get the number of agents configured for a role
    /// </summary>
    private int GetAgentCountForRole(AgentRole role)
    {
        return _collaborationConfig.GetAgentCount(role);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _agentSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
