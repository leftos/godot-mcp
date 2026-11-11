using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Win32.SafeHandles;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A process session's end: it is live until every process of its job is gone, not merely exited, and its output has drained;
/// a stop, a ready refusal and a cancelled start wait for that end, and a stop or refusal names in its warning a process still
/// held when the end's deadline expires. The job's child is held by the test's own debugger, and the clock is fake and
/// advances only where a test says so.
/// </summary>
public sealed partial class ProcessSessionEndTests
{
    private const string Session = "held";
    private const string Held = "The child is held through the Win32 debug API, and the program is pwsh starting ping.";

    // A ready line the program never prints, on a timeout no passing run reaches.
    private static readonly ReadyProfile NeverReady = new("never printed", 600_000) { Regex = NeverPrinted() };

    [Fact]
    public async Task AProcessThatExitsStaysLiveWhileAJobChildIsUnsignalled()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Held);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using LoadClock clock = new(new FakeTimeProvider(), new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        using EventWaitHandle exit = NewExitEvent(out string name);
        (GodotSession session, Task start) = await BeginAsync(harness, name, null, cancellation);
        await start;
        (Process main, Process child) = await OpenHeldAsync(session);
        using (main)
        using (child)
        {
            using ExitHold hold = await ExitHold.AttachAsync(child, RegistryHarness.HangGuard);
            try
            {
                exit.Set();
                await RegistryHarness.WaitUntilAsync(() => main.HasExited);

                Assert.False(child.WaitForExit(0));
                Assert.True(IsListedLive(harness), "the session was listed stopped while a process of its job was not yet gone");

                await hold.Killed.WaitAsync(RegistryHarness.HangGuard, cancellation);
                Assert.True(IsListedLive(harness), "the session was listed stopped while its killed child was held");

                hold.Release();
                // The output is read at the moment the session is first seen not live, so a line still in flight then is missed.
                bool drained = false;
                await RegistryHarness.WaitUntilAsync(() =>
                {
                    if (IsListedLive(harness))
                    {
                        return false;
                    }

                    drained = session.GetRawOutput().Stdout.Lines.Contains("last");
                    return true;
                });
                Assert.True(drained, "the session was listed stopped before its program's last line was read");
                Assert.True(child.WaitForExit(0) && main.WaitForExit(0));
            }
            finally
            {
                EndHeld(exit, child);
            }
        }
    }

    [Fact]
    public async Task AStopWaitsForAHeldChild()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Held);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using LoadClock clock = new(new FakeTimeProvider(), new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        using EventWaitHandle exit = NewExitEvent(out string name);
        (GodotSession session, Task start) = await BeginAsync(harness, name, null, cancellation);
        await start;
        (Process main, Process child) = await OpenHeldAsync(session);
        using (main)
        using (child)
        {
            using ExitHold hold = await ExitHold.AttachAsync(child, RegistryHarness.HangGuard);
            try
            {
                Task<string> stop = new ProjectTools(harness.Sessions).StopProjectAsync(Session, cancellationToken: cancellation);
                await hold.Killed.WaitAsync(RegistryHarness.HangGuard, cancellation);

                Assert.False(stop.IsCompleted, "the stop returned while its killed child was held");
                Assert.True(IsListedLive(harness), "the session was listed stopped while its killed child was held");

                hold.Release();
                JsonObject stopped = JsonNode.Parse(await stop.WaitAsync(RegistryHarness.HangGuard, cancellation))!.AsObject();

                Assert.False(stopped["alreadyExited"]!.GetValue<bool>());
                Assert.True(stopped["killed"]!.GetValue<bool>());
                Assert.Null(stopped["warning"]);
                Assert.False(IsListedLive(harness));
                Assert.True(child.WaitForExit(0) && main.WaitForExit(0));
            }
            finally
            {
                EndHeld(exit, child);
            }
        }
    }

    [Fact]
    public async Task AChildHeldPastTheBudgetIsNamedInTheStopsWarning()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Held);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        using EventWaitHandle exit = NewExitEvent(out string name);
        (GodotSession session, Task start) = await BeginAsync(harness, name, null, cancellation);
        await start;
        (Process main, Process child) = await OpenHeldAsync(session);
        using (main)
        using (child)
        {
            using ExitHold hold = await ExitHold.AttachAsync(child, RegistryHarness.HangGuard);
            try
            {
                Task<string> stop = new ProjectTools(harness.Sessions).StopProjectAsync(Session, cancellationToken: cancellation);
                await hold.Killed.WaitAsync(RegistryHarness.HangGuard, cancellation);
                // The killed program can take seconds to be signalled on a busy machine; the deadline is on the fake clock, so this
                // real-time wait spends none of it.
                Assert.True(await Task.Run(() => main.WaitForExit(RegistryHarness.HangGuard), cancellation));
                await RegistryHarness.AdvanceUntilDoneAsync(time, stop, cancellation);
                JsonObject stopped = JsonNode.Parse(await stop)!.AsObject();
                bool childGone = child.WaitForExit(0);
                bool live = IsListedLive(harness);
                hold.Release();

                Assert.False(childGone);
                Assert.False(live, "the session was listed live after its end's deadline expired");
                AssertWarningNames(stopped["warning"]!.GetValue<string>(), child.Id, main.Id);
            }
            finally
            {
                EndHeld(exit, child);
            }
        }
    }

    [Fact]
    public async Task AStopDuringANaturalExitsEndReportsAlreadyExited()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Held);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using LoadClock clock = new(new FakeTimeProvider(), new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        using EventWaitHandle exit = NewExitEvent(out string name);
        (GodotSession session, Task start) = await BeginAsync(harness, name, null, cancellation);
        await start;
        (Process main, Process child) = await OpenHeldAsync(session);
        using (main)
        using (child)
        {
            using ExitHold hold = await ExitHold.AttachAsync(child, RegistryHarness.HangGuard);
            try
            {
                exit.Set();
                await RegistryHarness.WaitUntilAsync(() => main.HasExited);
                Task<string> stop = new ProjectTools(harness.Sessions).StopProjectAsync(Session, cancellationToken: cancellation);
                await hold.Killed.WaitAsync(RegistryHarness.HangGuard, cancellation);
                hold.Release();
                JsonObject stopped = JsonNode.Parse(await stop.WaitAsync(RegistryHarness.HangGuard, cancellation))!.AsObject();

                Assert.True(stopped["alreadyExited"]!.GetValue<bool>());
                Assert.False(stopped["killed"]!.GetValue<bool>());
                Assert.Equal(7, stopped["exitCode"]!.GetValue<int>());
            }
            finally
            {
                EndHeld(exit, child);
            }
        }
    }

    [Fact]
    public async Task ACancelledReadyWaitReturnsOnlyOnceItsTreeIsGone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Held);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using LoadClock clock = new(new FakeTimeProvider(), new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        using EventWaitHandle exit = NewExitEvent(out string name);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        (GodotSession session, Task start) = await BeginAsync(harness, name, NeverReady, cancel.Token);
        (Process main, Process child) = await OpenHeldAsync(session);
        using (main)
        using (child)
        {
            using ExitHold hold = await ExitHold.AttachAsync(child, RegistryHarness.HangGuard);
            try
            {
                await cancel.CancelAsync();
                // A start that returned without ending its tree never kills the child, so the wait ends on whichever comes first.
                await Task.WhenAny(start, hold.Killed).WaitAsync(RegistryHarness.HangGuard, cancellation);

                Assert.False(start.IsCompleted, "the cancelled start returned while its killed child was held");

                hold.Release();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(RegistryHarness.HangGuard, cancellation));
                Assert.True(child.WaitForExit(0) && main.WaitForExit(0));
            }
            finally
            {
                EndHeld(exit, child);
                Abandon(harness, session);
            }
        }
    }

    [Fact]
    public async Task AReadyRefusalNamesAChildHeldPastTheBudgetInAParagraphOfItsOwn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Held);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        using EventWaitHandle exit = NewExitEvent(out string name);
        (GodotSession session, Task start) = await BeginAsync(harness, name, NeverReady, cancellation);
        (Process main, Process child) = await OpenHeldAsync(session);
        using (main)
        using (child)
        {
            using ExitHold hold = await ExitHold.AttachAsync(child, RegistryHarness.HangGuard);
            try
            {
                exit.Set();
                await hold.Killed.WaitAsync(RegistryHarness.HangGuard, cancellation);
                Assert.True(await Task.Run(() => main.WaitForExit(RegistryHarness.HangGuard), cancellation));
                await RegistryHarness.AdvanceUntilDoneAsync(time, start, cancellation);
                SessionException refused = await Assert.ThrowsAsync<SessionException>(() => start);
                hold.Release();

                Match paragraphs = RefusalWithWarning().Match(refused.Message);
                Assert.True(paragraphs.Success, $"the refusal does not end with the warning in a paragraph of its own: {refused.Message}");
                AssertWarningNames(paragraphs.Groups["warning"].Value, child.Id, main.Id);
            }
            finally
            {
                EndHeld(exit, child);
                Abandon(harness, session);
            }
        }
    }

    [Fact]
    public async Task DisposingTheSessionEndsItsTreeWithoutWaitingForIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Held);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using LoadClock clock = new(new FakeTimeProvider(), new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        using EventWaitHandle exit = NewExitEvent(out string name);
        (GodotSession session, Task start) = await BeginAsync(harness, name, null, cancellation);
        await start;
        (Process main, Process child) = await OpenHeldAsync(session);
        using (main)
        using (child)
        {
            using ExitHold hold = await ExitHold.AttachAsync(child, RegistryHarness.HangGuard);
            try
            {
                harness.Sessions.Forget(session);
                // A dispose that waited for the end would wait on the held child, so only the hang guard would end this wait.
                await Task.Run(session.Dispose, cancellation).WaitAsync(RegistryHarness.HangGuard, cancellation);
                await hold.Killed.WaitAsync(RegistryHarness.HangGuard, cancellation);

                Assert.False(child.WaitForExit(0));

                hold.Release();
                Assert.True(await Task.Run(() => child.WaitForExit(RegistryHarness.HangGuard), cancellation));
            }
            finally
            {
                EndHeld(exit, child);
            }
        }
    }

    [Fact]
    public async Task TheProgramsStdoutCarriesOnlyItsOwnLines()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Held);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using LoadClock clock = new(new FakeTimeProvider(), new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        using EventWaitHandle exit = NewExitEvent(out string name);
        (GodotSession session, Task start) = await BeginAsync(harness, name, null, cancellation);
        await start;
        (Process main, Process child) = await OpenHeldAsync(session);
        using (main)
        using (child)
        {
            try
            {
                // The child has written its first output, wherever its stdout goes.
                await RegistryHarness.WaitUntilAsync(() => WriteOperations(child) > 0);
                exit.Set();
                await RegistryHarness.WaitUntilAsync(() => !IsListedLive(harness));

                string[] foreign = [.. session.GetRawOutput().Stdout.Lines.Where(line => !ChildLine().IsMatch(line) && line != "last")];
                string first = foreign.FirstOrDefault() ?? "";
                Assert.True(foreign.Length == 0, $"the program's stdout carries {foreign.Length} line(s) it did not print, first «{first}»");
            }
            finally
            {
                EndHeld(exit, child);
            }
        }
    }

    [Fact]
    public void AProcessOutsideTheJobIsNotKept()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Job objects are Windows'; the stand-ins are ping -n.");
        Process inside = StartPing();
        try
        {
            Process outside = StartPing();
            try
            {
                using var tree = OwnWork.TreeCpu.Adopt(inside, NullLogger.Instance, join: true, killOnClose: true);
                using (SafeProcessHandle? member = tree.OpenMember(inside.Id))
                using (SafeProcessHandle? stranger = tree.OpenMember(outside.Id))
                {
                    Assert.NotNull(member);
                    Assert.Null(stranger);
                }

                OwnWork.EndedJob ended = tree.EndJob(NullLogger.Instance, @"X:\probe");
                try
                {
                    Assert.Contains(inside.Id, ended.Kept.Select(process => process.Pid));
                    Assert.Empty(ended.Unopened);
                    Assert.True(inside.WaitForExit(RegistryHarness.HangGuard));
                    Assert.False(outside.WaitForExit(0));
                    Assert.Null(tree.OpenMember(inside.Id));
                }
                finally
                {
                    ended.DisposeHandles();
                }
            }
            finally
            {
                EndPing(outside);
            }
        }
        finally
        {
            EndPing(inside);
        }
    }

    [Fact]
    public void TheWarningNamesOneProcessOrSeveral()
    {
        const string After = "5 s of load-adjusted time (wall 5 s, machine free 100% on average)";

        Assert.Equal(
            "The ended process (pid 41) was still running after 5 s of load-adjusted time (wall 5 s, machine free 100% on average); "
                + "the files it holds stay in use until it is gone.",
            ProcessEnd.Warning([41], After)
        );
        Assert.Equal(
            "The ended processes (pids 41, 42) were still running after 5 s of load-adjusted time (wall 5 s, machine free 100% on "
                + "average); the files they hold stay in use until they are gone.",
            ProcessEnd.Warning([41, 42], After)
        );
    }

    /// <summary>
    /// Asserts that <paramref name="warning"/> is the end's warning after its 5 s budget, in either form, naming
    /// <paramref name="named"/> and not <paramref name="gone"/>: another member of the job, such as the program's console host,
    /// may still be ending at the deadline on a busy machine.
    /// </summary>
    private static void AssertWarningNames(string warning, int named, int gone)
    {
        Match match = WarningText().Match(warning);
        Assert.True(match.Success, $"not the end's warning: {warning}");
        int[] pids = [.. match.Groups["pids"].Value.Split(", ").Select(pid => int.Parse(pid, CultureInfo.InvariantCulture))];
        Assert.Contains(named, pids);
        Assert.DoesNotContain(gone, pids);
    }

    /// <summary>A named event private to the test, which the program waits on before it prints "last" and exits with code 7.</summary>
    private static EventWaitHandle NewExitEvent(out string name)
    {
        name = $"Local\\godot-mcp-tests-{Guid.NewGuid():N}";
        return new EventWaitHandle(false, EventResetMode.ManualReset, name);
    }

    /// <summary>
    /// Starts the process session's program: pwsh starting ping as its job's child with ping's stdout discarded (so the session's
    /// stdout carries only the program's own lines), printing the child's pid, then waiting on the event
    /// <paramref name="eventName"/>, printing "last" and exiting with code 7. Returns the session and its start, which waits for
    /// <paramref name="ready"/> when one is given.
    /// </summary>
    private static async Task<(GodotSession Session, Task Start)> BeginAsync(
        RegistryHarness harness,
        string eventName,
        ReadyProfile? ready,
        CancellationToken cancellation
    )
    {
        string script =
            "$p = Start-Process ping -ArgumentList '-n','600','127.0.0.1' -NoNewWindow -RedirectStandardOutput NUL -PassThru; "
            + "\"child=$($p.Id)\"; "
            + $"$e = [System.Threading.EventWaitHandle]::OpenExisting('{eventName}'); "
            + "$null = $e.WaitOne(); Write-Output 'last'; exit 7";
        string[] command = ["pwsh", "-NoProfile", "-NonInteractive", "-Command", script];
        GodotSession session = await harness.Sessions.ReserveProcessAsync(Session, harness.Path, defaultName: false);
        ProcessProfile profile = new(command, null, new Dictionary<string, string>(), ready, null);
        Task start = session.StartProcess(Session, profile, command, ready, cancellation);
        return (session, start);
    }

    /// <summary>The session's program and, once it has printed its pid, its child, each with its handle open.</summary>
    private static async Task<(Process Main, Process Child)> OpenHeldAsync(GodotSession session)
    {
        var main = Process.GetProcessById(session.ProcessId!.Value);
        _ = main.Handle;
        int? childPid = null;
        await RegistryHarness.WaitUntilAsync(() => (childPid = ChildPid(session)) is not null);
        var child = Process.GetProcessById(childPid!.Value);
        _ = child.Handle;
        return (main, child);
    }

    /// <summary>The child's pid the program printed; null until it has.</summary>
    private static int? ChildPid(GodotSession session)
    {
        foreach (string line in session.GetRawOutput().Stdout.Lines)
        {
            if (ChildLine().Match(line) is { Success: true } match)
            {
                return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static bool IsListedLive(RegistryHarness harness) =>
        harness.Sessions.List(includeStopped: true).Any(session => session.Name == Session && session.Live);

    private static ulong WriteOperations(Process process) =>
        GetProcessIoCounters(process.Handle, out IoCounters counters) ? counters.WriteOperationCount : 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount,
            WriteOperationCount,
            OtherOperationCount,
            ReadTransferCount,
            WriteTransferCount,
            OtherTransferCount;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(nint process, out IoCounters counters);

    /// <summary>Lets the program exit and kills the child if a failed test left it running, so its hold can end.</summary>
    private static void EndHeld(EventWaitHandle exit, Process child)
    {
        exit.Set();
        try
        {
            child.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone or already ending.
        }
    }

    /// <summary>
    /// Lets go of a session whose start failed, as run_process does: a start refused or cancelled leaves it reserved, holding its
    /// log open in the harness's folder.
    /// </summary>
    private static void Abandon(RegistryHarness harness, GodotSession session)
    {
        harness.Sessions.Forget(session);
        session.Dispose();
    }

    /// <summary>ping.exe itself, which starts no process of its own, so killing it leaves nothing behind.</summary>
    private static Process StartPing() =>
        Process.Start(
            new ProcessStartInfo("ping", "-n 60 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            }
        )!;

    /// <summary>Kills a ping stand-in that still runs and disposes it.</summary>
    private static void EndPing(Process ping)
    {
        try
        {
            ping.Kill();
            ping.WaitForExit(RegistryHarness.HangGuard);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        finally
        {
            ping.Dispose();
        }
    }

    [GeneratedRegex(@"^child=(\d+)$")]
    private static partial Regex ChildLine();

    [GeneratedRegex("never printed")]
    private static partial Regex NeverPrinted();

    [GeneratedRegex(
        @"\AThe ended process(?:es)? \(pids? (?<pids>\d+(?:, \d+)*)\) (?:was|were) still running after 5 s of load-adjusted time "
            + @"\(wall [^)]*\); the files (?:it holds|they hold) stay in use until (?:it is|they are) gone\.\z"
    )]
    private static partial Regex WarningText();

    [GeneratedRegex(
        @"\AThe process 'held' exited \(code 7\) before a line matched 'never printed'; its last stderr lines:\n[\s\S]*?\n\n"
            + @"(?<warning>The ended [^\n]*)\z"
    )]
    private static partial Regex RefusalWithWarning();
}
