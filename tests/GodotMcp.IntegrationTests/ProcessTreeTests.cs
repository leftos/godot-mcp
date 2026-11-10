using System.Diagnostics;
using GodotMcp.IntegrationTests.Fixtures;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// <see cref="ProcessTree.KillAndWaitAsync"/> against a console wrapper whose child outlives it, as Godot_console.exe's
/// Godot.exe does: the wait covers the child, not only the process the test started.
/// </summary>
public sealed class ProcessTreeTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task KillAndWaitAsyncWaitsOnTheChildOfAConsoleWrapper()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // 600 pings outlast the hang guard, so a helper that never kills the child hangs at the guard instead of passing.
        // No CreateNoWindow: it gives cmd.exe a console of its own, whose conhost.exe child would join the tree beside PING.EXE.
        ProcessStartInfo startInfo = new("cmd.exe") { UseShellExecute = false };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping -n 600 127.0.0.1 > nul");
        using Process cmd = Process.Start(startInfo)!;
        int childPid = 0;

        bool childStarted = await Poll.UntilAsync(() => (childPid = PingChildOf(cmd.Id)) != 0, HangGuard, cancellation);
        Assert.True(childStarted, $"no PING.EXE child of cmd.exe (pid {cmd.Id}) appeared within {HangGuard.TotalSeconds:0} s");
        using var child = Process.GetProcessById(childPid);
        // Reading Handle keeps the process handle on the object, so HasExited below reads this process, never a reused pid.
        _ = child.Handle;
        IReadOnlyList<int> waited = await ProcessTree.KillAndWaitAsync(cmd, HangGuard);

        Assert.Equal([cmd.Id, childPid], waited);
        Assert.True(child.HasExited, $"PING.EXE (pid {childPid}) still runs after KillAndWaitAsync returned");
    }

    [Fact]
    public void WalkSkipsTheChildrenOfARowThatStartedBeforeItsParent()
    {
        DateTime rootStart = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
        // 2 names the root as its parent by a reused pid (it started earlier); 3, its child, started after the root; 4 is
        // the root's real child.
        Dictionary<int, DateTime> starts = new()
        {
            [2] = rootStart.AddHours(-1),
            [3] = rootStart.AddMinutes(1),
            [4] = rootStart.AddMinutes(2),
        };
        CaptureWindows.ProcessRow[] rows = [new(2, 1, "old.exe"), new(3, 2, "late.exe"), new(4, 1, "child.exe")];

        List<int> kept = ProcessTree.Walk(1, rootStart, rows, pid => starts.TryGetValue(pid, out DateTime start) ? start : null);

        Assert.Equal([4], kept);
    }

    private static int PingChildOf(int parentId) =>
        CaptureWindows
            .Processes()
            .Find(row => row.ParentId == parentId && string.Equals(row.Image, "PING.EXE", StringComparison.OrdinalIgnoreCase))
            ?.Id
        ?? 0;
}
