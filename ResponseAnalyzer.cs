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

    /// <summary>Keywords that indicate completion</summary>
    private static readonly string[] CompletionKeywords =
    [
        "all tasks complete",
        "project complete",
        "implementation complete",
        "all items done",
        "nothing left to do",
        "no remaining tasks",
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
        var output = result.Output ?? "";
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

        // Exit if high confidence
        if (analysis.ConfidenceScore >= 80)
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

        if (analysis.HasCompletionSignal)
            score += 40;

        if (analysis.RalphStatus?.Status == "COMPLETE")
            score += 30;

        if (analysis.RalphStatus?.ExitSignal == true)
            score += 20;

        if (_completionSignalCount >= 2)
            score += 10;

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
}

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
