using RalphController.Models;
using System.Text.RegularExpressions;

namespace RalphController;

/// <summary>
/// Shared helper for marking completed tasks in implementation_plan.md.
/// Used by both TeamController and TeamOrchestrator paths.
/// </summary>
public static class PlanUpdater
{
    /// <summary>
    /// Mark completed tasks in the plan file. Returns a verification result.
    /// Uses three matching strategies in priority order:
    /// 1. SourceLine exact match (most reliable for FromPlan tasks)
    /// 2. Title/description regex match
    /// 3. Keyword fallback (for AI-decomposed tasks with differing titles)
    /// </summary>
    public static TeamVerificationResult MarkCompletedTasks(
        string planPath,
        IReadOnlyList<AgentTask> allTasks,
        Action<string>? onOutput = null)
    {
        var result = new TeamVerificationResult();

        if (!File.Exists(planPath))
        {
            result.Summary = "No implementation plan file found";
            result.AllTasksComplete = true;
            return result;
        }

        try
        {
            var planContent = File.ReadAllText(planPath);
            var completedTasks = allTasks
                .Where(t => t.Status == Models.TaskStatus.Completed)
                .ToList();
            var failedTasks = allTasks
                .Where(t => t.Status == Models.TaskStatus.Failed)
                .ToList();

            var tasksMarked = 0;
            var matched = new HashSet<string>(); // task IDs already matched

            // Pass 1: SourceLine exact match (most reliable)
            foreach (var task in completedTasks)
            {
                if (string.IsNullOrEmpty(task.SourceLine)) continue;

                var escapedLine = Regex.Escape(task.SourceLine.Trim());
                // Match the original line with any checkbox state: [ ], [?], [!]
                var pattern = @"^(\s*-\s*)\[\s*[?\!]?\s*\]\s*" + ExtractDescriptionFromSourceLine(task.SourceLine);
                var match = Regex.Match(planContent, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    planContent = planContent[..match.Index]
                        + $"{match.Groups[1].Value}[x] {match.Groups[2].Value}"
                        + planContent[(match.Index + match.Length)..];
                    tasksMarked++;
                    matched.Add(task.TaskId);
                }
            }

            // Pass 2: Title/description regex match
            foreach (var task in completedTasks)
            {
                if (matched.Contains(task.TaskId)) continue;

                var patterns = new List<string>();
                if (!string.IsNullOrEmpty(task.Title))
                    patterns.Add(Regex.Escape(task.Title.Trim()));
                if (!string.IsNullOrEmpty(task.Description) && task.Description != task.Title)
                {
                    var firstLine = task.Description.Split('\n')[0].Trim();
                    if (firstLine.Length > 10)
                        patterns.Add(Regex.Escape(firstLine));
                }

                foreach (var pat in patterns)
                {
                    // Match "- [ ]", "- [?]", "- [!]" with flexible whitespace
                    var checkboxPattern = $@"^(\s*-\s*)\[\s*[?\!]?\s*\]\s*({pat})";
                    var match = Regex.Match(planContent, checkboxPattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        planContent = planContent[..match.Index]
                            + $"{match.Groups[1].Value}[x] {match.Groups[2].Value}"
                            + planContent[(match.Index + match.Length)..];
                        tasksMarked++;
                        matched.Add(task.TaskId);
                        break;
                    }
                }
            }

            // Pass 3: Keyword fallback for AI-decomposed tasks
            foreach (var task in completedTasks)
            {
                if (matched.Contains(task.TaskId)) continue;

                var title = task.Title ?? task.Description;
                var keywords = title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length >= 4)
                    .Select(w => w.Trim('.', ',', ':', ';', '(', ')').ToLower())
                    .Where(w => w.Length >= 4)
                    .Distinct()
                    .ToList();

                if (keywords.Count < 2) continue;

                // Find unchecked plan lines that contain 2+ keywords
                var linePattern = @"^(\s*-\s*)\[\s*[?\!]?\s*\]\s*(.+)$";
                var lineMatches = Regex.Matches(planContent, linePattern, RegexOptions.Multiline);

                foreach (Match lineMatch in lineMatches)
                {
                    var lineText = lineMatch.Groups[2].Value.ToLower();
                    var hits = keywords.Count(k => lineText.Contains(k));
                    if (hits >= 2)
                    {
                        planContent = planContent[..lineMatch.Index]
                            + $"{lineMatch.Groups[1].Value}[x] {lineMatch.Groups[2].Value}"
                            + planContent[(lineMatch.Index + lineMatch.Length)..];
                        tasksMarked++;
                        matched.Add(task.TaskId);
                        break;
                    }
                }
            }

            // Write back
            if (tasksMarked > 0)
            {
                File.WriteAllText(planPath, planContent);
                onOutput?.Invoke($"Updated {planPath}: marked {tasksMarked} tasks as complete");
            }

            // Build result
            result.TasksMarked = tasksMarked;
            result.AllTasksComplete = failedTasks.Count == 0 && completedTasks.Count > 0;

            foreach (var failed in failedTasks)
            {
                result.IncompleteTasks.Add($"FAILED: {failed.Title ?? failed.Description}");
            }

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

    /// <summary>
    /// Append audit findings to the implementation plan as a new section.
    /// Each finding becomes a checkbox item so subsequent decomposition can pick them up.
    /// </summary>
    public static void AppendAuditFindings(
        string planPath,
        List<Models.AuditFinding> findings,
        int auditRound,
        Action<string>? onOutput = null)
    {
        if (findings.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"## Audit Findings (Round {auditRound})");
        sb.AppendLine();
        foreach (var finding in findings)
        {
            var severity = !string.IsNullOrEmpty(finding.Severity) ? $"[{finding.Severity}] " : "";
            sb.AppendLine($"- [ ] {severity}{finding.Title}: {finding.Description}");
        }

        File.AppendAllText(planPath, sb.ToString());
        onOutput?.Invoke($"Appended {findings.Count} audit findings (round {auditRound}) to {planPath}");
    }

    /// <summary>
    /// Extract the description portion of a SourceLine (after the checkbox marker)
    /// and return it as a regex pattern with a capture group.
    /// </summary>
    private static string ExtractDescriptionFromSourceLine(string sourceLine)
    {
        // Strip leading "- [ ] ", "- [?] ", "- [!] " etc.
        var stripped = sourceLine.Trim();
        var descMatch = Regex.Match(stripped, @"^-\s*\[\s*[?\!x]?\s*\]\s*(.+)$");
        if (descMatch.Success)
        {
            return "(" + Regex.Escape(descMatch.Groups[1].Value.Trim()) + ")";
        }
        // Fallback: use entire line escaped
        return "(" + Regex.Escape(stripped) + ")";
    }
}
