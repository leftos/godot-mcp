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
    private static readonly TimeSpan ChildAppearWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

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
    public async Task TheListingFindsAChildRunningAndStillFindsItOnceItsParentHasExited()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using Process parent = StartParentWithPing();
        int? pingId = null;
        try
        {
            string? whileRunning = await WaitUntilListedAsync(parent, IsPing);
            Assert.NotNull(whileRunning);
            pingId = PingId(whileRunning);
            parent.Kill(entireProcessTree: false);
            Assert.True(await ProcessExit.WaitUntilGoneAsync(parent, ExitWait));

            IReadOnlyList<string> afterExit = LeftBehind.Find(parent, NullLogger.Instance);

            // cmd.exe's console host is its child too, and may still be ending; only the ping matters here.
            Assert.Contains(afterExit, described => string.Equals(described, $"PING.EXE (pid {pingId})", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await EndAsync(parent, pingId);
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

    private static Process StartParentWithPing() =>
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;

    private static bool IsPing(string described) => described.StartsWith("PING.EXE (pid ", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first listed child <paramref name="match"/> accepts, or null when none appears within <see cref="ChildAppearWait"/>.</summary>
    private static async Task<string?> WaitUntilListedAsync(Process parent, Func<string, bool> match)
    {
        var waited = Stopwatch.StartNew();
        string? found = LeftBehind.Find(parent, NullLogger.Instance).FirstOrDefault(match);
        while (found is null && waited.Elapsed < ChildAppearWait)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
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

    private static async Task EndAsync(Process parent, int? pingId)
    {
        if (!parent.HasExited)
        {
            parent.Kill(entireProcessTree: true);
            await ProcessExit.WaitUntilGoneAsync(parent, ExitWait);
        }

        if (pingId is not int id)
        {
            return;
        }

        try
        {
            using var ping = Process.GetProcessById(id);
            ping.Kill();
            await ProcessExit.WaitUntilGoneAsync(ping, ExitWait);
        }
        catch (ArgumentException)
        {
            // The ping has already exited.
        }
    }

    [GeneratedRegex(@"\(pid (\d+)\)")]
    private static partial Regex PidPattern();
}
