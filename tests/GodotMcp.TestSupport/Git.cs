using System.Diagnostics;

namespace GodotMcp.TestSupport;

/// <summary>Runs git for test setup and assertions, isolated from any git hook the tests themselves run under.</summary>
public static class Git
{
    private static readonly string[] InheritedGitVariables = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR"];

    /// <summary>Runs git in <paramref name="directory"/> and returns its stdout; throws when git fails.</summary>
    public static string Run(string directory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = directory,
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

        using Process process = Process.Start(startInfo)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} in {directory} exited {process.ExitCode}: {error.Result}");
        }

        return output;
    }

    /// <summary>Creates a repository in <paramref name="directory"/> and commits everything in it.</summary>
    public static void InitAndCommitAll(string directory)
    {
        Run(directory, "init", "--quiet", "--initial-branch=main");
        CommitAll(directory);
    }

    public static void CommitAll(string directory)
    {
        Run(directory, "add", "--all");
        Run(
            directory,
            "-c",
            "user.name=godot-mcp tests",
            "-c",
            "user.email=tests@godot-mcp.invalid",
            "commit",
            "--quiet",
            "--allow-empty",
            "-m",
            "fixture"
        );
    }

    /// <summary><c>git status --porcelain</c> with every untracked file listed.</summary>
    public static string Status(string directory) => Run(directory, "status", "--porcelain", "--untracked-files=all");
}
