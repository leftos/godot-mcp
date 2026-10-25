using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>Stopping and restarting a real Godot on the InputProbe and the server's exit: a game slow to quit killed with the
/// reason, how long a quit took, what a game left running, and every game asked to quit, sharing one grace, at the server's
/// exit.</summary>
public sealed class SessionStopTests : IAsyncDisposable
{
    // Adds a node whose _exit_tree blocks for twice the test's own timeout: the game acknowledges the quit and is still shutting
    // down, its bridge's connection still open, however late a busy machine lets the stop decide; only a kill ends it. The node
    // is the root's last child, so it leaves the tree before the bridge does.
    private static readonly string NeverFinishesQuittingScript = ExitTreeBlocksScript(2 * TestTimeouts.OwnLaunchMs);

    // Blocking 1.2 s: the game quits, only just inside the stop's 3 s grace.
    private static readonly string SlowButInTimeScript = ExitTreeBlocksScript(1200);

    // The probe's user argument naming the file its main scene writes as it leaves the tree (tests/fixtures/InputProbe/main.gd).
    private const string ExitMarkerArg = "--exit-marker=";

    // A hang guard on the wait of one game's stop for the other's at the server's exit: games that share one grace meet at once.
    private static readonly TimeSpan GraceMeetingWait = TimeSpan.FromMinutes(2);

    // How long a restart takes to pass its refusal checks and reach the old game's stop, well inside that stop's 3 s grace.
    private static readonly TimeSpan RestartPastItsChecks = TimeSpan.FromSeconds(1);
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGameSlowToQuitIsKilledAndSaysWhy()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        await tools.RunScriptAsync(NeverFinishesQuittingScript, 10_000, null, cancellation);

        StopResult stopped = await _harness.Sessions.StopAsync(null, cancellation);

        Assert.True(stopped.Killed);
        Assert.Null(stopped.GameExitCode);
        Assert.NotNull(stopped.KillReason);
        Assert.StartsWith(
            "the game acknowledged the quit but was still shutting down after 3 s of load-adjusted time (wall ",
            stopped.KillReason,
            StringComparison.Ordinal
        );
        Assert.Null(stopped.QuitMs);
        Assert.Null(stopped.LeftRunning);
        Assert.True(stopped.OverrideRemoved);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGameSlowToQuitInItsTreeSaysTheConnectionWasStillOpen()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        await tools.RunScriptAsync(NeverFinishesQuittingScript, 10_000, null, cancellation);

        StopResult stopped = await _harness.Sessions.StopAsync(null, cancellation);

        Assert.True(stopped.Killed);
        Assert.NotNull(stopped.KillReason);
        Assert.Contains("load-adjusted", stopped.KillReason, StringComparison.Ordinal);
        Assert.Contains("connection was still open", stopped.KillReason, StringComparison.Ordinal);
        Assert.Matches(@"\nProcess \d+: \d+ ms CPU over 1 s, \d+ threads, main thread ", stopped.KillReason);
        Assert.Contains("\nLast stderr lines:\n", stopped.KillReason, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AStopSaysHowLongTheQuitTook()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        await tools.RunScriptAsync(SlowButInTimeScript, 10_000, null, cancellation);

        StopResult stopped = await _harness.Sessions.StopAsync(null, cancellation);

        Assert.False(stopped.Killed);
        Assert.NotNull(stopped.QuitMs);
        Assert.InRange(stopped.QuitMs.Value, 1200, 2999);
        Assert.True(stopped.OverrideRemoved);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARestartReportsWhatTheOldGameLeftRunning()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        string started = await tools.RunScriptAsync(ProbeRuns.LingeringChildScript, 10_000, null, cancellation);
        int pingId = JsonNode.Parse(started)!["value"]!.GetValue<int>();

        var restarted = (RestartResult)await _harness.Sessions.RestartAsync(null, prepare: false, cancellation);
        await _harness.Sessions.StopAsync(null, cancellation);
        bool pingGone = await ProbeRuns.ProcessGoneAsync(pingId);

        Assert.Null(restarted.PreviousKillReason);
        Assert.NotNull(restarted.PreviousLeftRunning);
        string left = Assert.Single(restarted.PreviousLeftRunning);
        Assert.Equal($"ping.exe (pid {pingId})", left, ignoreCase: true);
        Assert.True(pingGone, $"ping (pid {pingId}) was still running after the restart");
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARestartSaysWhyItKilledTheOldGame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        await tools.RunScriptAsync(NeverFinishesQuittingScript, 10_000, null, cancellation);

        var restarted = (RestartResult)await _harness.Sessions.RestartAsync(null, prepare: false, cancellation);
        await _harness.Sessions.StopAsync(null, cancellation);

        Assert.NotNull(restarted.PreviousKillReason);
        Assert.Contains("still shutting down", restarted.PreviousKillReason, StringComparison.Ordinal);
        Assert.Null(restarted.PreviousLeftRunning);
        Assert.Null(restarted.PreviousQuitMs);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARestartSaysHowLongTheOldGameTookToQuit()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        await tools.RunScriptAsync(SlowButInTimeScript, 10_000, null, cancellation);

        var restarted = (RestartResult)await _harness.Sessions.RestartAsync(null, prepare: false, cancellation);
        await _harness.Sessions.StopAsync(null, cancellation);

        Assert.Null(restarted.PreviousKillReason);
        Assert.NotNull(restarted.PreviousQuitMs);
        Assert.InRange(restarted.PreviousQuitMs.Value, 1200, 2999);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AtServerExitAGameIsAskedToQuitAndRunsItsExitWork()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string marker = Path.Combine(Path.GetDirectoryName(_probe.Directory)!, "exit-marker.txt");
        await _harness.Sessions.LaunchAsync(Request(userArgs: [ExitMarkerArg + marker]), null, cancellation);
        using Process game = OpenGame("InputProbe");

        _harness.Sessions.Shutdown();
        _harness.Sessions.Shutdown();

        Assert.True(game.HasExited, "the shutdown returned with the game still running");
        Assert.Equal(0, game.ExitCode);
        Assert.True(File.Exists(marker), "the game was ended without running its exit work");
        Assert.False(File.Exists(_probe.OverrideFile));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AtServerExitGamesThatNeverFinishQuittingShareOneGraceAndAreKilled()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        List<Process> games = [];
        try
        {
            foreach (string name in new[] { "first", "second" })
            {
                await _harness.Sessions.LaunchAsync(Request(), name, cancellation);
                await tools.RunScriptAsync(NeverFinishesQuittingScript, 10_000, name, cancellation);
                games.Add(OpenGame(name));
            }

            // The stop reads the state of a game still running after its grace, after asking it to quit and before killing it. Each
            // read waits here for the other game's: games that share one grace both reach it, while stops one after the other
            // would hold the first game's kill until the second game's grace, which would never start.
            TaskCompletionSource bothGracesEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int gracesEnded = 0;
            _harness.Sessions.DescribeGameProcess = async processId =>
            {
                if (Interlocked.Increment(ref gracesEnded) == games.Count)
                {
                    bothGracesEnded.SetResult();
                }

                await bothGracesEnded.Task.WaitAsync(GraceMeetingWait, cancellation);
                return $"state of {processId}";
            };

            _harness.Sessions.Shutdown();

            Assert.True(
                bothGracesEnded.Task.IsCompletedSuccessfully,
                $"{Volatile.Read(ref gracesEnded)} of {games.Count} games had been asked to quit and run out their grace when the shutdown "
                    + "returned: the games did not share one grace"
            );
            Assert.All(games, game => Assert.True(game.HasExited, $"the game (pid {game.Id}) outlived the shutdown"));
            Assert.False(File.Exists(_probe.OverrideFile));
        }
        finally
        {
            foreach (Process game in games)
            {
                game.Dispose();
            }
        }
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AtServerExitOneSessionsFailedStopIsReportedAndTheOthersStillStop()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        string marker = Path.Combine(Path.GetDirectoryName(_probe.Directory)!, "exit-marker.txt");
        await _harness.Sessions.LaunchAsync(Request(), "failing", cancellation);
        await tools.RunScriptAsync(NeverFinishesQuittingScript, 10_000, "failing", cancellation);
        await _harness.Sessions.LaunchAsync(Request(userArgs: [ExitMarkerArg + marker]), "quitting", cancellation);
        using Process failing = OpenGame("failing");
        // The stop reads the state of a game still running after its grace; for this one the read fails with an unexpected exception.
        _harness.Sessions.DescribeGameProcess = processId =>
            processId == failing.Id ? throw new NotSupportedException("the state read failed") : Task.FromResult("state");
        TextWriter stderr = Console.Error;
        using StringWriter captured = new();
        Console.SetError(captured);
        try
        {
            _harness.Sessions.Shutdown();
        }
        finally
        {
            Console.SetError(stderr);
        }

        Assert.Contains(
            $"godot-mcp: stopping the game of {_probe.Directory} at shutdown failed: the state read failed",
            captured.ToString(),
            StringComparison.Ordinal
        );
        Assert.True(failing.HasExited, "the game whose stop failed outlived the shutdown");
        Assert.True(File.Exists(marker), "the other game did not quit by itself");
        Assert.False(File.Exists(_probe.OverrideFile));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AtServerExitARestartInFlightSettlesBeforeTheRegistryIsDisposed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), "held", cancellation);
        // The old game never finishes quitting, so the restart stays in flight through its stop's grace, past both refusal checks.
        await tools.RunScriptAsync(NeverFinishesQuittingScript, 10_000, "held", cancellation);
        Task<object> restart = _harness.Sessions.RestartAsync("held", prepare: false, cancellation);
        // Opened as the restart returns, before the registry stops its game, so the handle holds the pid while it is the game's.
        Task<Process?> restarted = restart.ContinueWith(
            completed => OpenRunning(((RestartResult)completed.Result).ProcessId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default
        );
        await Task.Delay(RestartPastItsChecks, cancellation);
        try
        {
            await Task.Run(_harness.Sessions.Dispose, cancellation);

            // Either the restart settled within the cap and its game was then stopped, or it was killed at the cap and failed.
            Exception? failed = await Record.ExceptionAsync(() => restart);
            Assert.True(failed is null or SessionException, $"the restart failed with something other than a refused start: {failed}");
            if (failed is null)
            {
                Assert.True(await ProbeRuns.ProcessGoneAsync(((RestartResult)await restart).ProcessId), "the restarted game outlived the registry");
            }

            Assert.False(File.Exists(_probe.OverrideFile));
        }
        finally
        {
            await EndIfRunningAsync(restarted);
        }
    }

    /// <summary>A run_script script that adds a node whose _exit_tree blocks the main thread for <paramref name="milliseconds"/>.</summary>
    private static string ExitTreeBlocksScript(int milliseconds) =>
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\tvar script := GDScript.new()\n"
        + $"\tscript.source_code = \"extends Node\\n\\n\\nfunc _exit_tree() -> void:\\n\\tOS.delay_msec({milliseconds})\\n\"\n"
        + "\tscript.reload()\n"
        + "\tvar node := Node.new()\n"
        + "\tnode.set_script(script)\n"
        + "\tscene_tree.root.add_child(node)\n"
        + "\treturn true\n";

    /// <summary>A process by id with its handle held, so its pid cannot be reused before the test ends it; null if unopenable.</summary>
    private static Process? OpenRunning(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            _ = process.Handle;
            return process;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // It is gone already, or cannot be opened.
            process?.Dispose();
            return null;
        }
    }

    /// <summary>Kills the process the restart started, through the handle held since it returned, with its tree when it still runs.</summary>
    private static async Task EndIfRunningAsync(Task<Process?> restarted)
    {
        Process? process;
        try
        {
            process = await restarted;
        }
        catch (TaskCanceledException)
        {
            // The restart failed, so it started no process to end.
            return;
        }

        using (process)
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>The named session's game, with its handle held so its exit code can be read after it exits.</summary>
    private Process OpenGame(string session)
    {
        int processId = _harness.Sessions.List(includeStopped: false).Single(info => info.Name == session).GameProcessId!.Value;
        var game = Process.GetProcessById(processId);
        _ = game.Handle;
        return game;
    }

    private LaunchRequest Request(string[]? userArgs = null) =>
        ProbeRuns.Request(_probe.Directory, userArgs, quiet: true, shutOutRealGamepads: false);
}
