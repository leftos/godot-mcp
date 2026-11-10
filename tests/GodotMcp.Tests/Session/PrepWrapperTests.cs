using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The prep wrapper without running it: reading it from godot-mcp.json, the command it makes of a prep process, where the
/// wrapper's own output goes, and its exit 124.
/// </summary>
public sealed class PrepWrapperTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string StepLog => Path.Combine(_temp.Path, ".godot", "godot-mcp", "build.log");

    private string WrapperLog => Path.Combine(_temp.Path, ".godot", "godot-mcp", "build.wrapper.log");

    private string Dotnet => Path.Combine(_temp.Path, "sdk", "dotnet.exe");

    [Fact]
    public void TheWrapperRunsFirstWithBothTokensAndThenTheCommand()
    {
        PrepWrapper wrapper = new(["gate", "run", "--log", "{log}", "--timeout", "{ceiling}", "--"], _temp.Path);

        ToolProcessRequest wrapped = wrapper.Wrap(BuildRequest());

        Assert.Equal("gate", wrapped.FileName);
        Assert.Equal(["run", "--log", StepLog, "--timeout", "300", "--", Dotnet, "build", "Game.csproj"], wrapped.Arguments);
        Assert.Equal(_temp.Path, wrapped.WorkingDirectory);
    }

    [Fact]
    public void TheWrapperKeepsTheCeilingAndTheEnvironmentAndDropsTheStallKill()
    {
        PrepWrapper wrapper = new(["gate", "{log}"], _temp.Path);

        ToolProcessRequest wrapped = wrapper.Wrap(BuildRequest());

        Assert.Equal(TimeSpan.FromSeconds(300), wrapped.Ceiling);
        Assert.Equal(TimeSpan.MaxValue, wrapped.StallLimit);
        Assert.Equal("1", wrapped.SetVariables["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal(["MSBuildSDKsPath"], wrapped.RemovedVariables);
    }

    [Fact]
    public void ATokenInsideAnElementIsReplacedInPlace()
    {
        PrepWrapper wrapper = new(["gate", "-Log={log}", "--ceiling={ceiling}s", "{ceiling}{ceiling}"], _temp.Path);

        ToolProcessRequest wrapped = wrapper.Wrap(BuildRequest());

        Assert.Equal(["-Log=" + StepLog, "--ceiling=300s", "300300", Dotnet, "build", "Game.csproj"], wrapped.Arguments);
    }

    [Fact]
    public void AWrapperNamingTheLogOwnsItAndItsOutputGoesBesideIt()
    {
        PrepWrapper wrapper = new(["gate", "-Log", "{log}"], _temp.Path);

        ToolProcessRequest wrapped = wrapper.Wrap(BuildRequest());

        Assert.True(wrapper.OwnsLog);
        Assert.Equal(WrapperLog, wrapped.LogPath);
        Assert.False(wrapped.AppendToLog);
    }

    [Fact]
    public void AWrapperNotNamingTheLogIsCapturedIntoTheStepLog()
    {
        PrepWrapper wrapper = new(["gate", "--ceiling", "{ceiling}"], _temp.Path);

        ToolProcessRequest wrapped = wrapper.Wrap(BuildRequest() with { AppendToLog = true });

        Assert.False(wrapper.OwnsLog);
        Assert.Equal(StepLog, wrapped.LogPath);
        Assert.True(wrapped.AppendToLog);
    }

    [Theory]
    [InlineData("build.log", "build.wrapper.log")]
    [InlineData("import.log", "import.wrapper.log")]
    [InlineData("import.2.log", "import.2.wrapper.log")]
    public void TheWrapperLogIsTheStepLogsNameWithWrapper(string stepLog, string wrapperLog)
    {
        string folder = Path.Combine(_temp.Path, ".godot", "godot-mcp");

        Assert.Equal(Path.Combine(folder, wrapperLog), PrepWrapper.WrapperLog(Path.Combine(folder, stepLog)));
    }

    [Fact]
    public void TheGateVerdictIsTheLastGateLineOfTheLog()
    {
        const string log =
            "gate: waiting for a heavy slot\n"
            + "gate: routed to the builder\n"
            + "Build succeeded.\n"
            + "0 Warning(s)\n"
            + "gate: passed in 20 s (load-adjusted 2 s, ceiling 300 s)\n";

        Assert.Equal("gate: passed in 20 s (load-adjusted 2 s, ceiling 300 s)", PrepWrapper.GateVerdict(log));
    }

    [Fact]
    public void TheGateVerdictDropsTheCarriageReturnOfACRLFLog()
    {
        const string verdict = "gate: passed in 1 s (load-adjusted 1 s, ceiling 300 s)";

        Assert.Equal(verdict, PrepWrapper.GateVerdict("building\r\n" + verdict + "\r\n"));
    }

    [Fact]
    public void ALogWithoutAGateLineHasNoVerdict()
    {
        Assert.Null(PrepWrapper.GateVerdict("Build succeeded.\n0 Warning(s)\n0 Error(s)\n"));
        Assert.Null(PrepWrapper.GateVerdict(string.Empty));
    }

    [Fact]
    public void AVerdictIsCutToTwoHundredCharacters()
    {
        string verdict = "gate: passed in " + new string('x', 300);

        string? cut = PrepWrapper.GateVerdict(verdict);

        Assert.Equal(200, cut!.Length);
        Assert.Equal(verdict[..200], cut);
    }

    [Fact]
    public void AGatePrefixInsideALineIsNoVerdict() =>
        Assert.Null(PrepWrapper.GateVerdict("2026-01-01 out gate: passed in 1 s\nout gate: FAILED (status 1)\n"));

    [Fact]
    public void ATrailingRestoredLineIsNotTheVerdict()
    {
        const string verdict = "gate: passed in 10 s on builder@leftos-buildbox (load-adjusted 9 s, ceiling 300 s; command 8 s). Full output: x";
        const string log =
            verdict
            + "\n"
            + "gate: restoring here\n"
            + "gate: restored X:\\a\\G.csproj here in 1.9 s, so a --no-build run here can use the routed build's outputs\n";

        Assert.Equal(verdict, PrepWrapper.GateVerdict(log));
    }

    [Fact]
    public void AFailedRunsVerdictIsItsFailedLine()
    {
        const string log =
            "gate: FAILED (status 1). Full output: l\n" + "gate: dotnet restore of X:\\a\\G.csproj here failed: the command exited 1\n";

        Assert.Equal("gate: FAILED (status 1). Full output: l", PrepWrapper.GateVerdict(log));
    }

    [Theory]
    [InlineData("gate: STALLED: no output and no CPU for 120 s (the whole tree idle); killed it")]
    [InlineData("gate: TIMED OUT after 300 s of load-adjusted time (ceiling 300 s)")]
    [InlineData("gate: BACKSTOP: 1500 s of wall time (five times the 300 s ceiling)")]
    [InlineData("gate: TRANSFER TIMED OUT after 1800 s fetching the outputs")]
    [InlineData("gate: TRANSFER FAILED: the command exited 1 on host; sync-back failed")]
    public void AKillLineBeatsTheFailedLineThatFollowsIt(string kill) =>
        Assert.Equal(kill, PrepWrapper.GateVerdict(kill + "\ngate: FAILED (status 124). Full output: l\n"));

    [Fact]
    public void ALogOfOnlyProseHasNoVerdict() =>
        Assert.Null(PrepWrapper.GateVerdict("gate: waiting for a heavy slot\ngate: not routed: no builder free\n"));

    [Fact]
    public void AStampedGateLogLineIsNoVerdict() =>
        Assert.Null(
            PrepWrapper.GateVerdict(
                "2026-01-01T10:00:00+00:00 gat gate: passed in 1 s (load-adjusted 1 s, ceiling 300 s)\n"
                    + "2026-01-01T10:00:01+00:00 gat gate: FAILED (status 1). Full output: l\n"
            )
        );

    [Theory]
    [InlineData("gate: waiting for a heavy slot (1 busy)", "Waiting")]
    [InlineData("gate: waiting for a heavy slot (2 busy), the first frees in about 40 s", "Waiting")]
    [InlineData("gate: still waiting for a heavy slot (480 s; 1 busy)", "Waiting")]
    [InlineData("gate: waiting for a heavy slot behind the critical request of gate 4242", "Waiting")]
    [InlineData("gate: waiting for a light slot beside the critical request of gate 4242", "Waiting")]
    [InlineData("gate: still waiting for a light slot (30 s)", "Waiting")]
    [InlineData("gate: waiting for a remote slot on builder", "Waiting")]
    [InlineData("2026-01-01T10:00:00+00:00 gat gate: waiting for a heavy slot (1 busy)", "Waiting")]
    [InlineData("2026-01-01T10:00:30+00:00 gat gate: still waiting for a heavy slot (30 s)", "Waiting")]
    [InlineData("gate: took a heavy slot after 480.2 s", "Took")]
    [InlineData("gate: took a remote slot on builder after 12 s", "Took")]
    [InlineData("2026-01-01T10:08:00+00:00 gat gate: took a heavy slot after 480.2 s", "Took")]
    [InlineData("gate: waiting for this worktree's work folder", "None")]
    [InlineData("gate: waiting for the pending gate push", "None")]
    [InlineData("gate: waiting for the machine (critical)", "None")]
    [InlineData("gate: running under the heavy slot of gate 4242", "None")]
    [InlineData("gate: passed in 20 s (load-adjusted 2 s, ceiling 300 s)", "None")]
    [InlineData("Build succeeded.", "None")]
    [InlineData("2026-01-01 out gate: waiting for a heavy slot (1 busy)", "None")]
    [InlineData("", "None")]
    public void TheWaitStateIsClassifiedFromTheConsoleLine(string line, string state) => Assert.Equal(state, PrepWrapper.Classify(line).ToString());

    [Fact]
    public void TheLastWaitIsTheLastWaitingLineOfTheLog()
    {
        string still = "gate: still waiting for a heavy slot (480 s; 1 busy) " + new string('x', 300);
        string log =
            "gate: waiting for a heavy slot (1 busy)\r\n"
            + "gate: still waiting for a heavy slot (30 s)\r\n"
            + "2026-01-01T10:08:00+00:00 gat "
            + still
            + "\r\n"
            + "gate: waiting for the pending gate push\r\n";

        Assert.Equal(still[..200], LastWaitOf(log));
    }

    [Fact]
    public void ALogWithNoWaitingLineHasNoLastWait()
    {
        Assert.Null(LastWaitOf("Build succeeded.\ngate: passed in 20 s (load-adjusted 2 s, ceiling 300 s)\n"));
        Assert.Null(LastWaitOf(string.Empty));
    }

    [Fact]
    public void ATookLineAfterTheLastWaitingLineEndsTheWait()
    {
        const string log =
            "gate: waiting for a heavy slot (1 busy)\n"
            + "gate: still waiting for a heavy slot (30 s)\n"
            + "gate: took a heavy slot after 31.4 s\n"
            + "gate: still waiting for a heavy slot (60 s)\n"
            + "Build succeeded.\n";

        Assert.Null(LastWaitOf(log));
    }

    [Fact]
    public void TheWrapperWatchesTheConsoleAfterTheRequestsOwnCallback()
    {
        const string waiting = "gate: waiting for a heavy slot (1 busy)";
        List<string> seen = [];
        PrepWrapper wrapper = new(["gate", "{log}"], _temp.Path);

        ToolProcessRequest wrapped = wrapper.Wrap(BuildRequest() with { StandardErrorLine = seen.Add });
        wrapped.StandardErrorLine!(waiting);

        Assert.Equal([waiting], seen);
        Assert.NotNull(wrapped.WaitWatch);
        Assert.Equal(WaitState.Waiting, wrapped.WaitWatch.State);
    }

    /// <summary>The last wait of a log's lines read in order, as the wait watch reads the console (<see cref="PrepWrapper.Advance"/>).</summary>
    private static string? LastWaitOf(string log) =>
        log.Split('\n').Aggregate<string, (WaitState State, string? LastWait)>((WaitState.None, null), PrepWrapper.Advance).LastWait;

    [Theory]
    [InlineData("tools/gate.cmd")]
    [InlineData("./gate.cmd")]
    [InlineData("tools\\gate.cmd")]
    public void AProgramWithAFolderIsRelativeToTheProjectFolder(string program)
    {
        PrepWrapper wrapper = new([program], _temp.Path);

        ToolProcessRequest wrapped = wrapper.Wrap(BuildRequest());

        Assert.Equal(Path.GetFullPath(Path.Combine(_temp.Path, program)), wrapped.FileName);
    }

    [Fact]
    public void ABareProgramIsLeftForPathAndARootedOneAsItIs()
    {
        string rooted = Path.Combine(Path.GetTempPath(), "gate.cmd");

        string bare = new PrepWrapper(["pwsh"], _temp.Path).Wrap(BuildRequest()).FileName;
        string absolute = new PrepWrapper([rooted], _temp.Path).Wrap(BuildRequest()).FileName;

        Assert.Equal("pwsh", bare);
        Assert.Equal(rooted, absolute);
    }

    [Theory]
    [InlineData(124, false, true)]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(123, false, false)]
    [InlineData(-1, true, false)]
    [InlineData(124, true, false)]
    public void OnlyAnExit124TheWrapperChoseIsAStop(int exitCode, bool killedByServer, bool stopped)
    {
        ToolProcessResult result = new(exitCode, TimeSpan.FromSeconds(1), killedByServer ? KillReason.Ceiling : KillReason.None);

        Assert.Equal(stopped, PrepWrapper.Stopped(result));
    }

    [Fact]
    public void TheStoppedNoteNamesTheStepAndItsLogs()
    {
        string owning = new PrepWrapper(["gate", "{log}"], _temp.Path).StoppedNote("the build", StepLog);
        string capturing = new PrepWrapper(["gate"], _temp.Path).StoppedNote("the build", StepLog);

        Assert.Equal($"the prep wrapper stopped the build (exit 124); its log: {StepLog}; the wrapper's output: {WrapperLog}", owning);
        Assert.Equal($"the prep wrapper stopped the build (exit 124); its log: {StepLog}", capturing);
    }

    [Fact]
    public void TheNoteNamesTheProgram() => Assert.Equal("ran through prepWrapper (pwsh)", new PrepWrapper(["pwsh", "{log}"], _temp.Path).Note);

    [Fact]
    public void ReadTakesTheKeyWithTheProjectFolder()
    {
        File.WriteAllText(_temp.Combine(ProjectProfile.FileName), """{ "scene": "res://main.tscn", "prepWrapper": ["pwsh", "{log}"] }""");

        var wrapper = PrepWrapper.Read(_temp.Path);

        Assert.NotNull(wrapper);
        Assert.Equal(["pwsh", "{log}"], wrapper.Command);
        Assert.Equal(_temp.Path, wrapper.ProjectDir);
    }

    [Fact]
    public void ReadIsNullWithoutTheFileOrTheKey()
    {
        var noFile = PrepWrapper.Read(_temp.Path);
        File.WriteAllText(_temp.Combine(ProjectProfile.FileName), """{ "quiet": false }""");
        var noKey = PrepWrapper.Read(_temp.Path);

        Assert.Null(noFile);
        Assert.Null(noKey);
    }

    [Fact]
    public void ReadRefusesAMalformedFileWithTheProfilesMessage()
    {
        string path = _temp.Combine(ProjectProfile.FileName);
        File.WriteAllText(path, """{ "prepWrapper": [] }""");

        SessionException refused = Assert.Throws<SessionException>(() => PrepWrapper.Read(_temp.Path));

        Assert.StartsWith($"{path} (top level): \"prepWrapper\" must be a non-empty array", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("; it is empty.", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProcessPrepRequestIsWrappedWithItsOwnLog()
    {
        string log = Path.Combine(_temp.Path, ".godot", "godot-mcp", "process-x.prep.log");
        string prep = Path.Combine(_temp.Path, "prep.ps1");
        PrepWrapper wrapper = new(["pwsh", "-NoProfile", "-File", "prep-wrapper.ps1", "{log}", "--"], _temp.Path);
        ToolProcessRequest request = new(prep, ["--fast"], _temp.Path, log, TimeSpan.FromSeconds(300));

        ToolProcessRequest wrapped = wrapper.Wrap(request);

        Assert.Equal("pwsh", wrapped.FileName);
        Assert.Equal(["-NoProfile", "-File", "prep-wrapper.ps1", log, "--", prep, "--fast"], wrapped.Arguments);
        Assert.Equal(
            ["-NoProfile", "-File", "prep-wrapper.ps1", log, "--", "pwsh", "--fast"],
            wrapper.Wrap(request with { FileName = "pwsh" }).Arguments
        );
        Assert.True(wrapper.OwnsLog);
        Assert.Equal("process-x.prep.wrapper.log", Path.GetFileName(PrepWrapper.WrapperLog(log)));
        Assert.Equal(PrepWrapper.WrapperLog(log), wrapped.LogPath);
    }

    private ToolProcessRequest BuildRequest() =>
        new(Dotnet, ["build", "Game.csproj"], Path.Combine(_temp.Path, "src"), StepLog, TimeSpan.FromSeconds(300))
        {
            SetVariables = new Dictionary<string, string> { ["MSBUILDDISABLENODEREUSE"] = "1" },
            RemovedVariables = ["MSBuildSDKsPath"],
        };
}
