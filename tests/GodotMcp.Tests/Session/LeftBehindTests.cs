using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>
/// How leftRunning names a process, which listed processes count as a parent's children, and the real Toolhelp listing on a
/// cmd.exe standing in for the game, with its ping child, before and after the cmd.exe itself has exited.
/// </summary>
public sealed partial class LeftBehindTests
{
    private const string WindowsOnly = "The Toolhelp listing is Windows only; elsewhere nothing is listed.";
    private const int Rounds = 20;

    // Hang guards: a passing run never waits them out, so they are sized past the longest stall a busy machine gives a child.
    private static readonly TimeSpan ChildAppearWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ExitWait = TimeSpan.FromMinutes(2);

    [Fact]
    public void AProcessIsNamedByItsImageAndItsId() =>
        Assert.Equal("ping.exe (pid 1234)", LeftBehind.Describe(new LeftBehind.ListedProcess(1234, 1, "ping.exe")));

    [Fact]
    public void OnlyTheProcessesWhoseParentIsTheGameAreItsChildren()
    {
        LeftBehind.ListedProcess[] listed =
        [
            new(10, 1, "game.exe"),
            new(11, 10, "ping.exe"),
            new(12, 11, "grandchild.exe"),
            new(13, 10, "cmd.exe"),
            new(14, 99, "other.exe"),
        ];

        int[] children = [.. LeftBehind.ChildrenOf(listed, 10).Select(process => process.Id)];

        Assert.Equal([11, 13], children);
        Assert.Empty(LeftBehind.ChildrenOf([], 10));
    }

    [Fact]
    public async Task AChildOrphanedAsSoonAsItIsListedIsStillListedAfterItsParentsExit()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        for (int round = 0; round < Rounds; round++)
        {
            await OneOrphanedPingAsync();
        }
    }

    [Fact]
    public async Task AProcessThatLeftNothingRunningListsNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using Process lone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        await lone.WaitForExitAsync(TestContext.Current.CancellationToken);

        // Its console host ends a moment after it.
        IReadOnlyList<string> listed = await ListOnceChildrenEndAsync(lone);

        Assert.Empty(listed);
    }

    // One round: a child listed the instant it appears, orphaned, and still listed after its parent is gone.
    private static async Task OneOrphanedPingAsync()
    {
        using Process parent = await StartParentWithRunningPingAsync();
        Process? ping = null;
        try
        {
            string? whileRunning = await WaitUntilListedAsync(parent, IsPing);
            Assert.NotNull(whileRunning);
            int pingId = PingId(whileRunning);
            ping = Process.GetProcessById(pingId);
            parent.Kill(entireProcessTree: false);
            Assert.True(await ProcessExit.WaitUntilGoneAsync(parent, ExitWait));

            IReadOnlyList<string> afterExit = LeftBehind.Find(parent, NullLogger.Instance);

            // cmd.exe's console host is its child too, and may still be ending; only the ping matters here.
            string expected = $"PING.EXE (pid {pingId})";
            bool listed = afterExit.Any(described => string.Equals(described, expected, StringComparison.OrdinalIgnoreCase));
            if (!listed)
            {
                Assert.Fail(ping.HasExited ? "the ping exited before the listing" : "the live ping was not listed");
            }
        }
        finally
        {
            await EndAsync(parent, ping);
            ping?.Dispose();
        }
    }

    // The ping outlives any test run, about ten minutes, so only EndAsync ends it: one that ended on its own could leave the
    // listing after its parent's exit with nothing to find. Its first line is waited for, since a process is listed from its
    // creation and a ping whose parent dies in its first few milliseconds dies with it.
    private static async Task<Process> StartParentWithRunningPingAsync()
    {
        var start = new ProcessStartInfo("cmd.exe", "/c ping -n 600 127.0.0.1")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        Process parent = Process.Start(start)!;
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(ChildAppearWait);
        string? line = null;
        bool printedNothing = false;
        try
        {
            do
            {
                line = await parent.StandardOutput.ReadLineAsync(guard.Token);
            } while (line is not null && line.Length == 0);
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        {
            printedNothing = true;
        }

        if (printedNothing)
        {
            await EndFailedStartAsync(parent);
            Assert.Fail($"the ping printed nothing within {ChildAppearWait}");
        }
        else if (line is null)
        {
            await EndFailedStartAsync(parent);
            Assert.Fail("the ping ended before printing its first line");
        }

        // The ping writes into the pipe this process holds; the rest of its output is read so it never blocks on a full one.
        _ = parent.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        return parent;
    }

    // A start that failed never returns its parent to the caller, so its tree is ended here rather than left running.
    private static async Task EndFailedStartAsync(Process parent)
    {
        if (!parent.HasExited)
        {
            parent.Kill(entireProcessTree: true);
            await ProcessExit.WaitUntilGoneAsync(parent, ExitWait);
        }

        parent.Dispose();
    }

    private static bool IsPing(string described) => described.StartsWith("PING.EXE (pid ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first listed child <paramref name="match"/> accepts, or null when none appears within <see cref="ChildAppearWait"/>
    /// or the parent exits first.
    /// </summary>
    private static async Task<string?> WaitUntilListedAsync(Process parent, Func<string, bool> match)
    {
        var waited = Stopwatch.StartNew();
        string? found = LeftBehind.Find(parent, NullLogger.Instance).FirstOrDefault(match);
        while (found is null && !parent.HasExited && waited.Elapsed < ChildAppearWait)
        {
            found = LeftBehind.Find(parent, NullLogger.Instance).FirstOrDefault(match);
        }

        return found;
    }

    /// <summary>The listing once it is empty, or as it stands after <see cref="ExitWait"/>.</summary>
    private static async Task<IReadOnlyList<string>> ListOnceChildrenEndAsync(Process parent)
    {
        var waited = Stopwatch.StartNew();
        IReadOnlyList<string> listed = LeftBehind.Find(parent, NullLogger.Instance);
        while (listed.Count > 0 && waited.Elapsed < ExitWait)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            listed = LeftBehind.Find(parent, NullLogger.Instance);
        }

        return listed;
    }

    private static int PingId(string described)
    {
        Match match = PidPattern().Match(described);
        Assert.True(match.Success, $"no pid in \"{described}\"");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static async Task EndAsync(Process parent, Process? ping)
    {
        if (!parent.HasExited)
        {
            parent.Kill(entireProcessTree: true);
            await ProcessExit.WaitUntilGoneAsync(parent, ExitWait);
        }

        // The held handle, never the pid again: a process id can be reused.
        if (ping is null || ping.HasExited)
        {
            return;
        }

        ping.Kill();
        await ProcessExit.WaitUntilGoneAsync(ping, ExitWait);
    }

    [GeneratedRegex(@"\(pid (\d+)\)")]
    private static partial Regex PidPattern();
}
