using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>Runs git for the server, on its own closed stdin and isolated from any git hook the server runs under.</summary>
internal static class GitRunner
{
    // Set when the server itself runs under a git hook; they would point git at another repository than the project's.
    private static readonly string[] InheritedGitVariables = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR"];

    /// <summary>Runs git in <paramref name="workingDirectory"/>; its stdout, untrimmed, or null when git fails or cannot start.</summary>
    /// <param name="workingDirectory">Where git runs.</param>
    /// <param name="logger">Where a missing git is reported.</param>
    /// <param name="arguments">git's arguments.</param>
    public static string? Run(string workingDirectory, ILogger logger, params string[] arguments)
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
            return process.ExitCode == 0 ? output : null;
        }
        catch (Win32Exception e)
        {
            Log.GitUnavailable(logger, e, workingDirectory);
            return null;
        }
    }
}
