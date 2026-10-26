using System.Diagnostics;

namespace GodotMcp.TestSupport;

/// <summary>Runs git for test setup and assertions, isolated from any git hook the tests themselves run under.</summary>
public static class Git
{
    private static readonly string[] InheritedGitVariables = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR"];

    /// <summary>
    /// Where each test git leaves a trace, outside the swept test folder; a git past the threshold keeps its trace for diagnosing the stall.
    /// </summary>
    private static readonly string SlowGitFolder = Path.Combine(Path.GetTempPath(), "godot-mcp-slow-git");

    /// <summary>A test git at least this slow is a stall, so its trace is kept and named on stderr.</summary>
    private static readonly TimeSpan SlowGitThreshold = TimeSpan.FromSeconds(5);

    /// <summary>Runs git in <paramref name="directory"/> and returns its stdout; throws when git fails.</summary>
    public static string Run(string directory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = directory,
            // Never inherit the test runner's stdin, which a git waiting for input would hang on.
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

        Directory.CreateDirectory(SlowGitFolder);
        string trace = Path.Combine(SlowGitFolder, $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}.trace");
        startInfo.Environment["GIT_TRACE2_PERF"] = trace;

        var stopwatch = Stopwatch.StartNew();
        using Process process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        stopwatch.Stop();
        string description = $"git {string.Join(' ', arguments)} in {directory}";
        RecordOrDiscardTrace(trace, stopwatch.Elapsed, description);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{description} exited {process.ExitCode}: {error.Result}");
        }

        return output;
    }

    /// <summary>Keeps a slow git's trace and names it; deletes a fast one's.</summary>
    private static void RecordOrDiscardTrace(string tracePath, TimeSpan elapsed, string description)
    {
        if (elapsed < SlowGitThreshold)
        {
            DeleteTrace(tracePath);
            return;
        }

        string line = $"{description} took {elapsed.TotalSeconds:F1} s; trace {tracePath}";
        File.WriteAllText(Path.ChangeExtension(tracePath, ".txt"), line);
        Console.Error.WriteLine(line);
    }

    /// <summary>Deletes a fast git's trace; it is only diagnostics, so a delete that fails is ignored.</summary>
    private static void DeleteTrace(string tracePath)
    {
        try
        {
            File.Delete(tracePath);
        }
        catch (IOException)
        {
            // The trace is only diagnostics: a delete another handle blocks changes nothing here.
        }
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
