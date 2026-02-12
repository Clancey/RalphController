using System.Diagnostics;
using RalphController.Models;

namespace RalphController.Git;

/// <summary>
/// Manages git worktrees for parallel agent isolation
/// </summary>
public class GitWorktreeManager : IDisposable
{
    private readonly string _repositoryRoot;
    private readonly Dictionary<string, string> _worktrees = new();
    private bool _disposed;

    /// <summary>
    /// The root directory of the git repository.
    /// </summary>
    public string RepositoryRoot => _repositoryRoot;

    public GitWorktreeManager(string repositoryRoot)
    {
        _repositoryRoot = repositoryRoot;
    }

    /// <summary>
    /// Get the current branch name
    /// </summary>
    public async Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunGitCommandAsync(_repositoryRoot,
            "rev-parse --abbrev-ref HEAD",
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return result.Output.Trim();
        }

        return "main";
    }

    /// <summary>
    /// Create a new worktree from a source branch
    /// </summary>
    public async Task<bool> CreateWorktreeAsync(
        string worktreePath,
        string branchName,
        string sourceBranch,
        CancellationToken cancellationToken = default)
    {
        var parentDir = Path.GetDirectoryName(worktreePath);
        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"worktree add \"{worktreePath}\" -b {branchName} {sourceBranch}",
            WorkingDirectory = _repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        if (process == null) return false;

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode == 0)
        {
            _worktrees[worktreePath] = branchName;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Commit changes in worktree
    /// </summary>
    public async Task<bool> CommitWorktreeAsync(
        string worktreePath,
        string message,
        CancellationToken cancellationToken = default)
    {
        await RunGitCommandAsync(worktreePath, "add -A", cancellationToken);

        var statusResult = await RunGitCommandAsync(worktreePath,
            "status --porcelain",
            cancellationToken);

        if (string.IsNullOrWhiteSpace(statusResult.Output))
        {
            return true;
        }

        var result = await RunGitCommandAsync(worktreePath,
            $"commit -m \"{message.Replace("\"", "\\\"")}\"",
            cancellationToken);

        return result.ExitCode == 0;
    }

    /// <summary>
    /// Get list of modified files in worktree
    /// </summary>
    public async Task<List<string>> GetModifiedFilesAsync(
        string worktreePath,
        CancellationToken cancellationToken = default)
    {
        var result = await RunGitCommandAsync(worktreePath,
            "diff --name-only HEAD",
            cancellationToken);

        if (result.ExitCode != 0)
            return new List<string>();

        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrEmpty(f))
            .ToList();
    }

    /// <summary>
    /// Remove a worktree
    /// </summary>
    public async Task<bool> RemoveWorktreeAsync(
        string worktreePath,
        CancellationToken cancellationToken = default)
    {
        await PruneAsync(cancellationToken);

        var result = await RunGitCommandAsync(_repositoryRoot,
            $"worktree remove \"{worktreePath}\"",
            cancellationToken);

        _worktrees.Remove(worktreePath);

        if (Directory.Exists(worktreePath))
        {
            try
            {
                Directory.Delete(worktreePath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        return result.ExitCode == 0;
    }

    /// <summary>
    /// Prune stale worktrees
    /// </summary>
    public async Task PruneAsync(CancellationToken cancellationToken = default)
    {
        await RunGitCommandAsync(_repositoryRoot, "worktree prune", cancellationToken);
    }

    /// <summary>
    /// Run a git command in the specified working directory.
    /// </summary>
    public async Task<GitCommandResult> RunGitCommandAsync(
        string workingDirectory,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            return new GitCommandResult { ExitCode = -1, Output = "", Error = "Failed to start git" };
        }

        // Read stdout/stderr concurrently BEFORE waiting for exit to avoid deadlock
        // (process can block if pipe buffer fills while we wait for exit)
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new GitCommandResult
        {
            ExitCode = process.ExitCode,
            Output = await outputTask,
            Error = await errorTask
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { PruneAsync().GetAwaiter().GetResult(); }
        catch { /* Don't crash during dispose */ }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Result of a merge operation
/// </summary>
public class MergeResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<GitConflict>? Conflicts { get; set; }
    public List<string>? MergedFiles { get; set; }
    public string? MergeCommitSha { get; set; }
}

/// <summary>
/// Information about a worktree
/// </summary>
public class WorktreeInfo
{
    public required string Path { get; init; }
    public string? Branch { get; set; }
    public string? Commit { get; set; }
}

/// <summary>
/// A git conflict
/// </summary>
public class GitConflict
{
    public required string FilePath { get; init; }
    public required string FullPath { get; init; }
    public string? OurContent { get; set; }
    public string? TheirContent { get; set; }
    public string? BaseContent { get; set; }
}

/// <summary>
/// Result of a git command
/// </summary>
public class GitCommandResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
}
