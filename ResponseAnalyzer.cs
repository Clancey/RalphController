using System.Globalization;
using System.Text.RegularExpressions;

namespace RalphController;

/// <summary>
/// Analyzes AI responses to detect completion signals, test-only loops,
/// and track progress through iterations.
/// </summary>
public class ResponseAnalyzer
{
    private readonly List<AnalysisResult> _history = new();
    private int _testOnlyLoopCount;
    private int _completionSignalCount;

    /// <summary>Number of consecutive test-only loops to trigger exit</summary>
    public int TestOnlyLoopThreshold { get; set; } = 3;

    /// <summary>Number of completion signals to confirm exit</summary>
    public int CompletionSignalThreshold { get; set; } = 2;

    /// <summary>Keywords that indicate completion - these should be specific project-level completion signals</summary>
    private static readonly string[] CompletionKeywords =
    [
        // Explicit completion phrases
        "all tasks complete",
        "all tasks have been completed",
        "all tasks are complete",
        "project is complete",
        "implementation is complete",
        "all items done",
        "nothing left to do",
        // Task tracking signals
        "no remaining tasks",
        "no more tasks",
        "no remaining unchecked",
        "no unchecked tasks",
        "no incomplete tasks",
        "all tasks.*checked",
        "all items.*checked",
        // Prompts for new work (indicates current work is done)
        "please add a new task",
        "add a new task to",
        "add new tasks",
        "waiting for.*new.*task",
        "ready for.*next.*task",
        // RALPH status signals
        "EXIT_SIGNAL: true",
        "RALPH_STATUS.*COMPLETE"
    ];

    /// <summary>Keywords that indicate test-only activity</summary>
    private static readonly string[] TestKeywords =
    [
        "npm test",
        "dotnet test",
        "pytest",
        "jest",
        "running tests",
        "test passed",
        "test failed",
        "all tests pass"
    ];

    /// <summary>Keywords that indicate implementation activity</summary>
    private static readonly string[] ImplementationKeywords =
    [
        "created",
        "implemented",
        "added",
        "modified",
        "updated",
        "wrote",
        "built",
        "fixed",
        "refactored"
    ];

    /// <summary>
    /// Analyze an AI response
    /// </summary>
    public AnalysisResult Analyze(AIResult result)
    {
        var output = CombineOutput(result);
        var analysis = new AnalysisResult
        {
            Timestamp = DateTime.UtcNow,
            Success = result.Success,
            OutputLength = output.Length
        };

        // Check for RALPH_STATUS block
        analysis.RalphStatus = ExtractRalphStatus(output);

        // Detect completion signals
        analysis.HasCompletionSignal = DetectCompletionSignal(output);
        if (analysis.HasCompletionSignal)
        {
            _completionSignalCount++;
        }
        else
        {
            _completionSignalCount = 0;
        }

        // Detect test-only loop
        analysis.IsTestOnlyLoop = DetectTestOnlyLoop(output);
        if (analysis.IsTestOnlyLoop)
        {
            _testOnlyLoopCount++;
        }
        else
        {
            _testOnlyLoopCount = 0;
        }

        var rateLimitInfo = TryDetectRateLimit(output);
        if (rateLimitInfo is not null)
        {
            analysis.IsRateLimited = true;
            analysis.RateLimitResetAt = rateLimitInfo.ResetAt;
            analysis.RateLimitMessage = rateLimitInfo.Message;
        }

        // Calculate confidence score
        analysis.ConfidenceScore = CalculateConfidence(analysis);

        // Determine if we should exit
        analysis.ShouldExit = ShouldExit(analysis);
        analysis.ExitReason = GetExitReason(analysis);

        _history.Add(analysis);
        return analysis;
    }

    /// <summary>
    /// Check if the loop should exit based on accumulated signals
    /// </summary>
    public bool ShouldExit(AnalysisResult analysis)
    {
        // Exit if completion signal threshold reached
        if (_completionSignalCount >= CompletionSignalThreshold)
            return true;

        // Exit if stuck in test-only loops
        if (_testOnlyLoopCount >= TestOnlyLoopThreshold)
            return true;

        // Exit if RALPH_STATUS indicates complete
        if (analysis.RalphStatus?.Status == "COMPLETE" &&
            analysis.RalphStatus?.ExitSignal == true)
            return true;

        // Exit if high confidence (need explicit signals, not just keywords)
        if (analysis.ConfidenceScore >= 90)
            return true;

        return false;
    }

    /// <summary>
    /// Reset analyzer state
    /// </summary>
    public void Reset()
    {
        _history.Clear();
        _testOnlyLoopCount = 0;
        _completionSignalCount = 0;
    }

    private bool DetectCompletionSignal(string output)
    {
        foreach (var keyword in CompletionKeywords)
        {
            if (Regex.IsMatch(output, keyword, RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }

    private bool DetectTestOnlyLoop(string output)
    {
        var testCount = 0;
        var implCount = 0;

        foreach (var keyword in TestKeywords)
        {
            testCount += Regex.Matches(output, keyword, RegexOptions.IgnoreCase).Count;
        }

        foreach (var keyword in ImplementationKeywords)
        {
            implCount += Regex.Matches(output, keyword, RegexOptions.IgnoreCase).Count;
        }

        // Test-only if tests dominate and no implementation
        return testCount > 3 && implCount == 0;
    }

    private int CalculateConfidence(AnalysisResult analysis)
    {
        var score = 0;

        // Single completion signal is not enough - need multiple signals or explicit status
        if (analysis.HasCompletionSignal)
            score += 25;

        if (analysis.RalphStatus?.Status == "COMPLETE")
            score += 40;

        if (analysis.RalphStatus?.ExitSignal == true)
            score += 30;

        // Multiple consecutive completion signals is a strong indicator
        if (_completionSignalCount >= 2)
            score += 25;

        return Math.Min(score, 100);
    }

    private string? GetExitReason(AnalysisResult analysis)
    {
        if (_completionSignalCount >= CompletionSignalThreshold)
            return $"Completion signal detected {_completionSignalCount} times";

        if (_testOnlyLoopCount >= TestOnlyLoopThreshold)
            return $"Test-only loop detected {_testOnlyLoopCount} consecutive times";

        if (analysis.RalphStatus?.ExitSignal == true)
            return "RALPH_STATUS EXIT_SIGNAL received";

        if (analysis.ConfidenceScore >= 80)
            return $"High confidence score: {analysis.ConfidenceScore}";

        return null;
    }

    private RalphStatus? ExtractRalphStatus(string output)
    {
        // Look for ---RALPH_STATUS--- block
        var match = Regex.Match(output,
            @"---RALPH_STATUS---\s*(.*?)\s*---END_STATUS---",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        if (!match.Success)
            return null;

        var statusBlock = match.Groups[1].Value;
        var status = new RalphStatus();

        // Parse status fields
        var statusMatch = Regex.Match(statusBlock, @"STATUS:\s*(\w+)", RegexOptions.IgnoreCase);
        if (statusMatch.Success)
            status.Status = statusMatch.Groups[1].Value.ToUpper();

        var exitMatch = Regex.Match(statusBlock, @"EXIT_SIGNAL:\s*(true|false)", RegexOptions.IgnoreCase);
        if (exitMatch.Success)
            status.ExitSignal = exitMatch.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);

        var tasksMatch = Regex.Match(statusBlock, @"TASKS_COMPLETED:\s*(\d+)", RegexOptions.IgnoreCase);
        if (tasksMatch.Success)
            status.TasksCompleted = int.Parse(tasksMatch.Groups[1].Value);

        var filesMatch = Regex.Match(statusBlock, @"FILES_MODIFIED:\s*(\d+)", RegexOptions.IgnoreCase);
        if (filesMatch.Success)
            status.FilesModified = int.Parse(filesMatch.Groups[1].Value);

        var testsMatch = Regex.Match(statusBlock, @"TESTS_PASSED:\s*(true|false|\d+)", RegexOptions.IgnoreCase);
        if (testsMatch.Success)
        {
            var val = testsMatch.Groups[1].Value;
            status.TestsPassed = val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                 (int.TryParse(val, out var n) && n > 0);
        }

        var nextMatch = Regex.Match(statusBlock, @"NEXT_STEP:\s*(.+?)(?:\n|$)", RegexOptions.IgnoreCase);
        if (nextMatch.Success)
            status.NextStep = nextMatch.Groups[1].Value.Trim();

        return status;
    }

    private static string CombineOutput(AIResult result)
    {
        var output = result.Output ?? "";
        var error = result.Error ?? "";
        if (string.IsNullOrWhiteSpace(error))
            return output;
        return $"{output}\n{error}";
    }

    public static ProviderRateLimitInfo? TryDetectRateLimit(AIResult result)
    {
        return TryDetectRateLimit(CombineOutput(result));
    }

    private static ProviderRateLimitInfo? TryDetectRateLimit(string output)
    {
        if (!RateLimitRegex.IsMatch(output))
            return null;

        var resetAt = TryParseResetAt(output);
        var message = ExtractRateLimitLine(output);
        return new ProviderRateLimitInfo(resetAt, message);
    }

    private static readonly Regex RateLimitRegex = new(
        @"\b(hit|reached)\s+(your\s+)?limit\b|\brate\s+limit\b|\bquota\s+exceeded\b|\b(have\s+)?no\s+quota\b|\b402\b.*quota",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ResetAtRegex = new(
        @"resets\s+(?<time>\d{1,2}(?::\d{2})?\s*(am|pm)|\d{1,2}:\d{2})\s*(\((?<tz>[^)]+)\))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ResetInRegex = new(
        @"resets\s+in\s+(?<value>\d+)\s*(?<unit>hours?|hrs?|hr|h|minutes?|mins?|min|m)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static DateTimeOffset? TryParseResetAt(string output)
    {
        var inMatch = ResetInRegex.Match(output);
        if (inMatch.Success)
        {
            if (int.TryParse(inMatch.Groups["value"].Value, out var value))
            {
                var unit = inMatch.Groups["unit"].Value.ToLowerInvariant();
                var delta = unit.StartsWith("h")
                    ? TimeSpan.FromHours(value)
                    : TimeSpan.FromMinutes(value);
                return DateTimeOffset.UtcNow.Add(delta);
            }
        }

        var match = ResetAtRegex.Match(output);
        if (!match.Success)
            return null;

        var timeText = match.Groups["time"].Value.Trim();
        if (!TryParseTimeOfDay(timeText, out var timeOfDay))
            return null;

        var tzId = match.Groups["tz"].Success
            ? match.Groups["tz"].Value.Trim()
            : TimeZoneInfo.Local.Id;

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch
        {
            tz = TimeZoneInfo.Local;
        }

        var nowInTz = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var localDateTime = nowInTz.Date + timeOfDay;
        var offset = tz.GetUtcOffset(localDateTime);
        var candidate = new DateTimeOffset(localDateTime, offset);

        if (candidate <= nowInTz)
            candidate = candidate.AddDays(1);

        return candidate;
    }

    private static bool TryParseTimeOfDay(string input, out TimeSpan timeOfDay)
    {
        var formats = new[]
        {
            "h tt", "htt", "h:mm tt", "hh:mm tt",
            "H:mm", "HH:mm"
        };

        if (DateTime.TryParseExact(input, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var dt))
        {
            timeOfDay = dt.TimeOfDay;
            return true;
        }

        timeOfDay = default;
        return false;
    }

    private static string? ExtractRateLimitLine(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            if (RateLimitRegex.IsMatch(line))
                return line.Trim();
        }

        return null;
    }
}

/// <summary>
/// Result of analyzing an AI response
/// </summary>
public class AnalysisResult
{
    public DateTime Timestamp { get; set; }
    public bool Success { get; set; }
    public int OutputLength { get; set; }
    public bool HasCompletionSignal { get; set; }
    public bool IsTestOnlyLoop { get; set; }
    public int ConfidenceScore { get; set; }
    public bool ShouldExit { get; set; }
    public string? ExitReason { get; set; }
    public RalphStatus? RalphStatus { get; set; }
    public bool IsRateLimited { get; set; }
    public DateTimeOffset? RateLimitResetAt { get; set; }
    public string? RateLimitMessage { get; set; }
}

public record ProviderRateLimitInfo(DateTimeOffset? ResetAt, string? Message);

/// <summary>
/// Parsed RALPH_STATUS block from AI output
/// </summary>
public class RalphStatus
{
    public string? Status { get; set; }  // IN_PROGRESS, COMPLETE, BLOCKED
    public bool? ExitSignal { get; set; }
    public int? TasksCompleted { get; set; }
    public int? FilesModified { get; set; }
    public bool? TestsPassed { get; set; }
    public string? NextStep { get; set; }
}

/// <summary>
/// Result of final verification analysis
/// </summary>
public class VerificationResult
{
    public bool AllTasksComplete { get; set; }
    public List<string> CompletedTasks { get; set; } = new();
    public List<string> IncompleteTasks { get; set; } = new();
    public string? Summary { get; set; }
}

/// <summary>
/// Provides verification prompts and parsing
/// </summary>
public static class FinalVerification
{
    /// <summary>
    /// Get the verification prompt to inject after completion is detected
    /// </summary>
    public static string GetVerificationPrompt(string? implementationPlanPath = null)
    {
        var planSection = "";
        if (!string.IsNullOrEmpty(implementationPlanPath) && File.Exists(implementationPlanPath))
        {
            try
            {
                var planContent = File.ReadAllText(implementationPlanPath);
                planSection = $@"

## Implementation Plan to Verify:
{planContent}
";
            }
            catch { /* Ignore if can't read */ }
        }

        return $@"
---FINAL_VERIFICATION_REQUEST---

You indicated that the task is complete. Before we finish, please perform a final verification:

1. Review each item in the implementation plan or task list
2. For EACH item, verify it has been fully implemented:
   - Check that the code exists and is correct
   - Verify tests pass (if applicable)
   - Confirm the feature works as expected

3. Report your findings in this EXACT format:

---VERIFICATION_RESULT---
OVERALL_STATUS: [COMPLETE or INCOMPLETE]

COMPLETED_TASKS:
- [Task 1 that is done]
- [Task 2 that is done]

INCOMPLETE_TASKS:
- [Task that still needs work]: [What's missing]
- [Another incomplete task]: [What's missing]

SUMMARY: [Brief summary of verification findings]
---END_VERIFICATION---

If any tasks are INCOMPLETE, continue working on them. Only report COMPLETE if ALL tasks are truly finished.
{planSection}
";
    }

    /// <summary>
    /// Parse the verification result from AI output
    /// </summary>
    public static VerificationResult? ParseVerificationResult(string output)
    {
        // Look for ---VERIFICATION_RESULT--- block
        var match = Regex.Match(output,
            @"---VERIFICATION_RESULT---\s*(.*?)\s*---END_VERIFICATION---",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            // Try alternative patterns
            match = Regex.Match(output,
                @"OVERALL_STATUS:\s*(COMPLETE|INCOMPLETE)",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return null;
        }

        var content = match.Groups[1].Value;
        var result = new VerificationResult();

        // Parse overall status
        var statusMatch = Regex.Match(content, @"OVERALL_STATUS:\s*(COMPLETE|INCOMPLETE)", RegexOptions.IgnoreCase);
        if (statusMatch.Success)
        {
            result.AllTasksComplete = statusMatch.Groups[1].Value.Equals("COMPLETE", StringComparison.OrdinalIgnoreCase);
        }

        // Parse completed tasks
        var completedMatch = Regex.Match(content, @"COMPLETED_TASKS:\s*((?:- .+\n?)+)", RegexOptions.IgnoreCase);
        if (completedMatch.Success)
        {
            var tasks = completedMatch.Groups[1].Value;
            foreach (Match taskMatch in Regex.Matches(tasks, @"- (.+?)(?:\n|$)"))
            {
                var task = taskMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(task))
                    result.CompletedTasks.Add(task);
            }
        }

        // Parse incomplete tasks
        var incompleteMatch = Regex.Match(content, @"INCOMPLETE_TASKS:\s*((?:- .+\n?)+)", RegexOptions.IgnoreCase);
        if (incompleteMatch.Success)
        {
            var tasks = incompleteMatch.Groups[1].Value;
            foreach (Match taskMatch in Regex.Matches(tasks, @"- (.+?)(?:\n|$)"))
            {
                var task = taskMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(task) && !task.Equals("None", StringComparison.OrdinalIgnoreCase))
                    result.IncompleteTasks.Add(task);
            }
        }

        // Parse summary
        var summaryMatch = Regex.Match(content, @"SUMMARY:\s*(.+?)(?:\n|$)", RegexOptions.IgnoreCase);
        if (summaryMatch.Success)
        {
            result.Summary = summaryMatch.Groups[1].Value.Trim();
        }

        // If no explicit incomplete tasks but status is incomplete, mark as incomplete
        if (!result.AllTasksComplete && result.IncompleteTasks.Count == 0)
        {
            result.IncompleteTasks.Add("Verification indicated incomplete status");
        }

        return result;
    }

    /// <summary>
    /// Check if output contains a verification request
    /// </summary>
    public static bool IsVerificationRequest(string output)
    {
        return output.Contains("---FINAL_VERIFICATION_REQUEST---", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if output contains a verification result
    /// </summary>
    public static bool HasVerificationResult(string output)
    {
        return Regex.IsMatch(output, @"OVERALL_STATUS:\s*(COMPLETE|INCOMPLETE)", RegexOptions.IgnoreCase);
    }
}
