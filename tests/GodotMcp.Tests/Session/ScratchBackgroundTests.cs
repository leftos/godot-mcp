using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A background scratch run's registry, status file, log and end, with no Godot: one final state and one completion line
/// whatever races it, writes a reader blocks skipped, and unnamed tool calls kept off the sessions a live run holds.
/// </summary>
public sealed partial class ScratchBackgroundTests : IDisposable
{
    private const int ServerPid = 4242;

    private readonly TempDirectory _temp = new();
    private readonly StringWriter _errors = new();
    private DateTimeOffset _now = new(2026, 10, 19, 22, 21, 44, TimeSpan.Zero);

    public void Dispose()
    {
        _errors.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task StopAndFinishRacingLeaveOneCompletionLineAndItIsLast()
    {
        for (int round = 0; round < 20; round++)
        {
            ScratchBackgroundRun run = Started(new ScratchRuns(), $"race{round}");
            using Barrier start = new(3);
            await Task.WhenAll(
                Racer(start, () => run.Finish(ScratchEnd.Stopped)),
                Racer(start, () => run.Finish(Passed())),
                Racer(
                    start,
                    () =>
                    {
                        for (int step = 0; step < 5; step++)
                        {
                            run.StepStarted(0, step, $"step{step}", 5, 1);
                        }
                    }
                )
            );

            string[] lines = LogLines(run);
            Assert.Single(lines, line => Completion().IsMatch(line));
            Assert.Matches(Completion(), lines[^1]);
        }
    }

    [Fact]
    public void NothingIsRunningOrPlayingAfterTheCompletionLine()
    {
        ScratchBackgroundRun run = Started(new ScratchRuns(), "after", "A", "B");
        run.SceneStarted(0);
        run.StepStarted(0, 1, "jump", 3, 2);

        run.Finish(ScratchEnd.Interrupted);
        run.SceneStarted(1);
        run.StepStarted(0, 2, "land", 3, 1);
        run.SceneEnded(0, Green("A"), playsAgain: false);

        JsonObject status = StatusFile(run);
        Assert.Equal("interrupted", status["state"]!.GetValue<string>());
        JsonNode scene = status["scenes"]![0]!;
        Assert.Equal(
            ("interrupted", 1, "jump"),
            (scene["state"]!.GetValue<string>(), scene["step"]!.GetValue<int>(), scene["stepName"]!.GetValue<string>())
        );
        Assert.Null(scene["verdict"]);
        Assert.Equal("queued", status["scenes"]![1]!["state"]!.GetValue<string>());
        Assert.Equal($"godot-mcp scratch run {run.RunId} finished: interrupted", LogLines(run)[^1]);
    }

    [Fact]
    public void TheFirstEndReasonRecordedWins()
    {
        ScratchBackgroundRun run = Started(new ScratchRuns(), "first");

        Assert.True(run.Record(ScratchEnd.Stopped));
        Assert.False(run.Record(ScratchEnd.Interrupted));
        run.Finish(Passed());
        run.Fail("too late");

        JsonObject status = StatusFile(run);
        Assert.Equal("stopped", status["state"]!.GetValue<string>());
        Assert.Null(status["result"]);
        Assert.Null(status["error"]);
        Assert.Equal($"godot-mcp scratch run {run.RunId} finished: stopped", Assert.Single(LogLines(run)));
    }

    [Fact]
    public void ARefusedRegistrationRegistersNothing()
    {
        ScratchRuns runs = new();
        ScratchBackgroundRun live = runs.Register(Plan(Project("one"), "A"), ServerPid, Io());

        SessionException refused = Assert.Throws<SessionException>(() => runs.Register(Plan(Project("one"), "B"), ServerPid, Io()));

        Assert.Equal(ScratchRuns.StillRunning(live.RunId), refused.Message);
        Assert.Same(live, runs.Live(Project("one")));
        Assert.Throws<SessionException>(() => runs.RefuseLive(Project("one")));
        Assert.False(Directory.Exists(ProjectPrep.LogFolder(Project("one"))));
    }

    [Fact]
    public void RegistrationAfterInterruptIsRefused()
    {
        ScratchRuns runs = new();
        Assert.Empty(runs.Interrupt());

        SessionException refused = Assert.Throws<SessionException>(() => runs.Register(Plan(Project("late"), "A"), ServerPid, Io()));

        Assert.Equal(SessionRegistry.ShuttingDown, refused.Message);
        Assert.Null(runs.Live(Project("late")));
    }

    [Fact]
    public void RemainingTimeCountsThePlayedStepsToComeTheCurrentOneAndTheLastPaceOnceMore()
    {
        ScratchWalk walk = new([new ScratchPace(1, null), new ScratchPace(2, null), new ScratchPace(0.5, null), new ScratchPace(3, null)], [0, 1, 3]);

        Assert.Equal(9, walk.RemainingSecondsAtMost(0));
        Assert.Equal(8, walk.RemainingSecondsAtMost(1));
        Assert.Equal(6, walk.RemainingSecondsAtMost(3));
    }

    [Fact]
    public void LogLinesStripLineBreaksSoAStepNameCannotFormACompletionLine()
    {
        ScratchBackgroundRun run = Started(new ScratchRuns(), "names", "A");
        run.SceneStarted(0);

        run.StepStarted(0, 0, "x\r\nfinished: done\u0007", 1, 1);
        run.StepStarted(0, 0, $"y{(char)0x2028}finished: done{(char)0x2029}", 1, 1);

        string[] lines = LogLines(run);
        Assert.Equal($"godot-mcp scratch run {run.RunId} scene A step 0/1 xfinished: done", lines[^2]);
        Assert.Equal($"godot-mcp scratch run {run.RunId} scene A step 0/1 yfinished: done", lines[^1]);
        Assert.DoesNotContain(lines, line => Completion().IsMatch(line));
    }

    [Fact]
    public async Task ARunIdOfTheWrongFormIsRefusedBeforeAnyPathIsBuilt()
    {
        await using RegistryHarness harness = new();
        ScratchTools tools = new(harness.Sessions, TestCSharp.Unused());

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            tools.ScratchStatusAsync(
                _temp.Combine("no-such-project"),
                @"..\..\20261019T222144-3f2a",
                stop: null,
                TestContext.Current.CancellationToken
            )
        );

        Assert.StartsWith(@"runId '..\..\20261019T222144-3f2a' is not a scratch run id", refused.Message, StringComparison.Ordinal);
        Assert.True(ScratchBackgroundRun.IsRunId("20261019T222144-3f2a"));
        Assert.All(["20261019T222144-3F2A", "20261019T222144-3f2a\n", "2026101T222144-3f2a"], id => Assert.False(ScratchBackgroundRun.IsRunId(id)));
    }

    [Fact]
    public void AnEventWriteToAStatusFileHeldOpenWithoutDeleteSharingIsSkipped()
    {
        ScratchBackgroundRun run = Started(new ScratchRuns(), "held", "A");
        string before = File.ReadAllText(run.StatusFile);

        using (new FileStream(run.StatusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            run.SceneStarted(0);
        }

        Assert.Equal(before, File.ReadAllText(run.StatusFile));
        Assert.Contains("status file", _errors.ToString(), StringComparison.Ordinal);
        Assert.Equal($"godot-mcp scratch run {run.RunId} scene A start", Assert.Single(LogLines(run)));
        Assert.Empty(Directory.GetFiles(ProjectPrep.LogFolder(run.ProjectDir), "*.tmp"));
    }

    [Fact]
    public async Task AStopOfAFinishedRunWritesNothing()
    {
        ScratchBackgroundRun run = Started(new ScratchRuns(), "finished", "A");
        run.Finish(Passed());
        string status = File.ReadAllText(run.StatusFile);
        string log = File.ReadAllText(run.LogFile);
        _now = _now.AddMinutes(1);

        (ScratchRunStatus stopped, bool live) = await run.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("done", false), (stopped.State, live));
        Assert.Equal(status, File.ReadAllText(run.StatusFile));
        Assert.Equal(log, File.ReadAllText(run.LogFile));
        Assert.Equal($"godot-mcp scratch run {run.RunId} finished: done, passed true, green 0, red 0, killed 0, known 0", LogLines(run)[^1]);
    }

    [Fact]
    public void TheFinishedListKeepsTheLastTwenty()
    {
        ScratchRuns runs = new();
        string project = Project("many");
        List<string> ids = [];
        for (int run = 0; run < ScratchRuns.FinishedKept + 1; run++)
        {
            _now = _now.AddSeconds(1);
            ScratchBackgroundRun registered = runs.Register(Plan(project, "A"), ServerPid, Io());
            registered.CreateFiles();
            registered.Finish(ScratchEnd.Stopped);
            runs.Remove(registered);
            ids.Add(registered.RunId);
        }

        Assert.Null(runs.FindFinished(project, ids[0]));
        Assert.All(ids.Skip(1), id => Assert.Equal("stopped", runs.FindFinished(project, id)!.State));
        Assert.Null(runs.Live(project));
    }

    [Fact]
    public async Task AnUnnamedCallFindsNoSessionABackgroundRunHoldsWhileItsNameStillFindsIt()
    {
        await using RegistryHarness harness = new();
        string project = harness.Project("held");
        using FakeBridge game = await harness.AttachFakeGameAsync(project, "held.scratch-A", new FakeHello());
        GodotSession session = harness.Sessions.Resolve("held.scratch-A");
        ScratchBackgroundRun run = harness.Sessions.ScratchRuns.Register(Plan(project, "A"), ServerPid, Io());

        run.SessionHeld(0, session);

        Assert.Equal(GodotSession.NoneRunning, Assert.Throws<SessionException>(() => harness.Sessions.Resolve(null)).Message);
        Assert.Same(session, harness.Sessions.Resolve("held.scratch-A"));
        harness.Sessions.ScratchRuns.Remove(run);
        Assert.Same(session, harness.Sessions.Resolve(null));
    }

    [Fact]
    public async Task AScratchSessionIsHeldFromItsReservationSoAnUnnamedCallNeverFindsItPending()
    {
        await using RegistryHarness harness = new();
        string project = harness.Project("pending");

        GodotSession pending = await harness.Sessions.ReserveScratchAsync(project, "A", "res://A.tscn", "pending", held: true);

        Assert.True(pending.IsLive);
        Assert.True(harness.Sessions.ScratchRuns.Holds(pending));
        Assert.Equal(GodotSession.NoneRunning, Assert.Throws<SessionException>(() => harness.Sessions.Resolve(null)).Message);
        harness.Sessions.ScratchRuns.Release(pending);
        Assert.Same(pending, harness.Sessions.Resolve(null));
    }

    [Fact]
    public async Task StopProjectWithoutASessionSkipsTheSessionABackgroundRunHolds()
    {
        await using RegistryHarness harness = new();
        string project = harness.Project("folder");
        using FakeBridge game = await harness.AttachFakeGameAsync(project, "folder.scratch-A", new FakeHello());
        GodotSession session = harness.Sessions.Resolve("folder.scratch-A");
        harness.Sessions.ScratchRuns.Hold(session);

        await Assert.ThrowsAsync<SessionException>(() =>
            harness.Sessions.StopFolderAsync(project, session: null, TestContext.Current.CancellationToken)
        );

        Assert.True(session.IsLive);
        harness.Sessions.ScratchRuns.Release(session);
    }

    [Fact]
    public void ARegistrationRightAfterTheCompletionLineSucceedsAndTheEarlierRunStaysReadable()
    {
        ScratchRuns runs = new();
        ScratchBackgroundRun earlier = Started(runs, "again", "A");
        earlier.Finish(Passed());
        _now = _now.AddSeconds(1);

        ScratchBackgroundRun next = runs.Register(Plan(Project("again"), "A"), ServerPid, Io());

        Assert.Same(next, runs.Live(Project("again")));
        Assert.Equal("done", runs.FindFinished(Project("again"), earlier.RunId)!.State);
        runs.Remove(earlier);
        Assert.Same(next, runs.Live(Project("again")));
        Assert.Equal("done", runs.FindFinished(Project("again"), earlier.RunId)!.State);
    }

    [Fact]
    public async Task AStopOfARunReadFromItsFileIsRefusedEvenWhenTheFileNamesThisServer()
    {
        await using RegistryHarness harness = new();
        string project = harness.Project("file");
        ScratchTools tools = new(harness.Sessions, TestCSharp.Unused());
        const string RunId = "20261019T222144-3f2a";
        string path = ScratchBackgroundRun.StatusPath(project, RunId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pid = harness.Sessions.ServerProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(
            path,
            $"{{\"runId\":\"{RunId}\",\"state\":\"running\",\"serverPid\":{pid}}}",
            TestContext.Current.CancellationToken
        );

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            tools.ScratchStatusAsync(project, RunId, stop: true, TestContext.Current.CancellationToken)
        );
        JsonObject read = JsonNode
            .Parse(await tools.ScratchStatusAsync(project, RunId, stop: null, TestContext.Current.CancellationToken))!
            .AsObject();

        Assert.Equal(
            $"scratch run {RunId} is not live in this server ({pid}); its status file {path} names process id {pid}, "
                + "and only a live run can be stopped.",
            refused.Message
        );
        Assert.Equal(("running", false), (read["state"]!.GetValue<string>(), read["live"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task ABackgroundRunWhoseFilesCannotBeMadeLeavesNoEntry()
    {
        await using RegistryHarness harness = new();
        string project = harness.Project("unwritable");
        await File.WriteAllTextAsync(
            Path.Combine(project, "A.tscn"),
            "[gd_scene format=3]\n\n[node name=\"A\" type=\"Node\"]\n",
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(Path.Combine(project, ".godot"), "a file where the folder would go", TestContext.Current.CancellationToken);
        ScratchTools tools = new(harness.Sessions, TestCSharp.Unused());

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            tools.RunScratchesAsync(project, ["res://A.tscn"], new ScratchOptions(Background: true), TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("A file operation failed", refused.Message, StringComparison.Ordinal);
        Assert.Null(harness.Sessions.ScratchRuns.Live(project));
    }

    [Fact]
    public async Task TheExitFinishesARunWhoseTaskHangsAsInterrupted()
    {
        ScratchRuns runs = new();
        ScratchBackgroundRun run = Started(runs, "hung", "A");
        TaskCompletionSource hung = new(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Start(() => hung.Task);
        run.SceneStarted(0);

        runs.AwaitInterrupted(runs.Interrupt(), TimeSpan.Zero);

        Assert.Equal("interrupted", StatusFile(run)["state"]!.GetValue<string>());
        Assert.Equal($"godot-mcp scratch run {run.RunId} finished: interrupted", LogLines(run)[^1]);
        Assert.False(run.Read().Live);
        hung.SetResult();
        await run.Task!;
    }

    [Fact]
    public void ALaterExitPassWaitsForNoRunAlreadyFinished()
    {
        ScratchRuns runs = new();
        ScratchBackgroundRun run = Started(runs, "again-exit", "A");
        TaskCompletionSource hung = new(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Start(() => hung.Task);
        runs.AwaitInterrupted(runs.Interrupt(), TimeSpan.Zero);

        Task[] second = runs.Interrupt();
        runs.AwaitInterrupted(second, TimeSpan.FromMinutes(10));

        Assert.Empty(second);
        Assert.Single(LogLines(run), line => Completion().IsMatch(line));
        hung.SetResult();
    }

    [Fact]
    public async Task ARegistrationWhileTheFirstEndIsStillWritingSucceeds()
    {
        ScratchRuns runs = new();
        using BlockingWriter errors = new();
        ScratchBackgroundRun first = runs.Register(Plan(Project("mid"), "A"), ServerPid, new ScratchRunIo(() => _now, errors));
        first.CreateFiles();
        Task finish;
        using (new FileStream(first.StatusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            // The held status file fails the final write, whose report then blocks: the end has begun and not landed.
            finish = Task.Run(() => first.Finish(Passed()), TestContext.Current.CancellationToken);
            errors.Entered.Wait(TestContext.Current.CancellationToken);
        }

        Assert.True(first.HasEnded);
        Assert.True(first.Read().Live);
        ScratchBackgroundRun next = runs.Register(Plan(Project("mid"), "A"), ServerPid, Io());
        errors.Release.Set();
        await finish;
        runs.Remove(first);

        Assert.Same(next, runs.Live(Project("mid")));
        Assert.Equal("done", runs.FindFinished(Project("mid"), first.RunId)!.State);
    }

    [Fact]
    public void AFailedCreateFilesLeavesNeitherItsLogNorATempFile()
    {
        ScratchBackgroundRun run = new ScratchRuns().Register(Plan(Project("blocked"), "A"), ServerPid, Io());
        Directory.CreateDirectory(run.StatusFile);

        Exception thrown = Assert.ThrowsAny<Exception>(run.CreateFiles);

        Assert.True(thrown is IOException or UnauthorizedAccessException, thrown.ToString());
        Assert.False(File.Exists(run.LogFile));
        Assert.Empty(Directory.GetFiles(ProjectPrep.LogFolder(run.ProjectDir), "*.tmp"));
    }

    /// <summary>
    /// An error writer that keeps every line, counts the status move's retry reports, and runs <see cref="OnRetry"/> at the
    /// first: the seam a holder lets go at, with no timing.
    /// </summary>
    private sealed class RetryWatchingWriter : TextWriter
    {
        private readonly StringBuilder _text = new();

        public Action OnRetry { get; set; } = static () => { };

        public int Retries { get; private set; }

        public string Text => _text.ToString();

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            _text.AppendLine(value);
            if (value is not null && value.Contains("status file into place", StringComparison.Ordinal) && ++Retries == 1)
            {
                OnRetry();
            }
        }
    }

    /// <summary>An error writer whose first line blocks until released, so a run can be held between its end's start and its final write.</summary>
    private sealed class BlockingWriter : TextWriter
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            Entered.Set();
            Release.Wait();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Entered.Dispose();
                Release.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    [Fact]
    public void AStatusMoveAHolderRefusesIsTriedAgainAndLandsOnceTheHolderLetsGo()
    {
        using RetryWatchingWriter errors = new();
        ScratchBackgroundRun run = new ScratchRuns().Register(Plan(Project("held-move"), "A"), ServerPid, new ScratchRunIo(() => _now, errors));
        run.CreateFiles();
        FileStream holder = new(run.StatusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // The holder lets go when the first retry is reported, so the next try finds the target free.
        errors.OnRetry = holder.Dispose;

        run.SceneStarted(0);

        holder.Dispose();
        Assert.Equal(1, errors.Retries);
        Assert.DoesNotContain("so the write was skipped", errors.Text, StringComparison.Ordinal);
        Assert.Equal("playing", StatusFile(run)["scenes"]![0]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void AStatusMoveHeldPastTheRetryWindowIsReportedAndSkipped()
    {
        using RetryWatchingWriter errors = new();
        ScratchBackgroundRun run = new ScratchRuns().Register(Plan(Project("held-long"), "A"), ServerPid, new ScratchRunIo(() => _now, errors));
        run.CreateFiles();

        using (new FileStream(run.StatusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            run.SceneStarted(0);
        }

        Assert.True(errors.Retries >= 1, errors.Text);
        Assert.Contains(
            $"writing scratch run {run.RunId}'s status file {run.StatusFile} failed, so the write was skipped",
            errors.Text,
            StringComparison.Ordinal
        );
        Assert.Equal("queued", StatusFile(run)["scenes"]![0]!["state"]!.GetValue<string>());
    }

    [GeneratedRegex(@"^godot-mcp scratch run \S+ finished:")]
    private static partial Regex Completion();

    /// <summary>A thread that waits at <paramref name="start"/> for the others, then runs <paramref name="act"/>.</summary>
    private static Task Racer(Barrier start, Action act)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        return Task.Run(
            () =>
            {
                start.SignalAndWait(cancellation);
                act();
            },
            cancellation
        );
    }

    private ScratchRunIo Io() => new(() => _now, _errors);

    private string Project(string name) => _temp.Combine(name);

    /// <summary>A run registered on a project of its own with its files created.</summary>
    private ScratchBackgroundRun Started(ScratchRuns runs, string project, params string[] scenes)
    {
        ScratchBackgroundRun run = runs.Register(Plan(Project(project), scenes.Length == 0 ? ["A"] : scenes), ServerPid, Io());
        run.CreateFiles();
        return run;
    }

    private static ScratchPlan Plan(string projectDir, params string[] scenes) =>
        new(
            projectDir,
            [.. scenes.Select(scene => new ScratchScenePlan(scene, $"res://scratch/{scene}.tscn", 1, []))],
            [],
            Prepare: false,
            Details: false
        )
        {
            Parallel = 1,
            Listed = true,
            SessionPrefix = "p",
            KeepGoing = false,
            Steps = null,
        };

    private static ScratchRunResult Passed() => ScratchVerdict.Summarise([], PrepResult.Skipped);

    private static ScratchSceneResult Green(string scene) =>
        new(scene, ScratchVerdict.Green, new ScratchStepCount(1, 1), 1, 1.5, $"p.scratch-{scene}") { Exit = new ScratchExit(0) };

    private static string[] LogLines(ScratchBackgroundRun run) => File.ReadAllText(run.LogFile).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static JsonObject StatusFile(ScratchBackgroundRun run) => JsonNode.Parse(File.ReadAllText(run.StatusFile))!.AsObject();
}
