using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

internal enum GitExcludeOutcome
{
    Added,
    AlreadyPresent,
    NotARepository,
}

/// <summary>
/// Hides a file the server writes into a project from git, through the repository's <c>info/exclude</c> (shared by
/// every worktree), so it never shows in <c>git status</c> and never enters a commit.
/// </summary>
internal static class GitExclude
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Set when the server itself runs under a git hook; they would point git at another repository than the project's.
    private static readonly string[] InheritedGitVariables = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR"];

    /// <summary>Adds <c>/&lt;path of fileName from the top level&gt;</c> to the exclude file unless it is there already.</summary>
    /// <param name="projectDir">The folder that holds the file, in any form: an 8.3 short name reaches the same pattern.</param>
    /// <param name="fileName">The file's name inside <paramref name="projectDir"/>.</param>
    /// <param name="logger">Where a missing repository or a missing git is reported.</param>
    public static GitExcludeOutcome Ensure(string projectDir, string fileName, ILogger logger)
    {
        string? excludeGitPath = RunGit(projectDir, logger, "rev-parse", "--git-path", "info/exclude");
        // git reports the folder's path from the top level itself ("game/", or empty at the top), with the long names
        // however the folder was reached, so no path is compared here against one git resolved.
        string? prefix = excludeGitPath is null ? null : RunGit(projectDir, logger, "rev-parse", "--show-prefix");
        if (excludeGitPath is null || prefix is null)
        {
            Log.NotAGitRepository(logger, projectDir, fileName);
            return GitExcludeOutcome.NotARepository;
        }

        string excludePath = Path.GetFullPath(Path.Combine(projectDir, excludeGitPath));
        return AppendOnce(excludePath, "/" + EscapeGlob(prefix + fileName));
    }

    private static GitExcludeOutcome AppendOnce(string excludePath, string pattern)
    {
        string existing = File.Exists(excludePath) ? File.ReadAllText(excludePath) : string.Empty;
        if (existing.Split('\n').Any(line => line.TrimEnd('\r') == pattern))
        {
            return GitExcludeOutcome.AlreadyPresent;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
        string separator = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : "\n";
        File.AppendAllText(excludePath, $"{separator}{pattern}\n", Utf8NoBom);
        return GitExcludeOutcome.Added;
    }

    private static string EscapeGlob(string path)
    {
        StringBuilder escaped = new(path.Length);
        foreach (char c in path)
        {
            if (c is '*' or '?' or '[')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    private static string? RunGit(string workingDirectory, ILogger logger, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = workingDirectory,
            // Never inherit the server's stdin, the MCP pipe (see GodotCommandLine.CreateStartInfo).
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (string name in InheritedGitVariables)
        {
            startInfo.Environment.Remove(name);
        }

        try
        {
            using Process process = Process.Start(startInfo)!;
            process.StandardInput.Close();
            // stderr is drained so a chatty git never blocks on a full pipe; outside a repository it only says so.
            Task<string> drainError = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            drainError.Wait();
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Win32Exception e)
        {
            Log.GitUnavailable(logger, e, workingDirectory);
            return null;
        }
    }
}
