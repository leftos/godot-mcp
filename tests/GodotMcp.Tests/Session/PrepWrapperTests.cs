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
    [InlineData("compile-items-0a1b.log", "compile-items-0a1b.wrapper.log")]
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
        string verdict = "gate: " + new string('x', 300);

        string? cut = PrepWrapper.GateVerdict(verdict);

        Assert.Equal(200, cut!.Length);
        Assert.Equal(verdict[..200], cut);
    }

    [Fact]
    public void AGatePrefixInsideALineIsNoVerdict() =>
        Assert.Null(PrepWrapper.GateVerdict("2026-01-01 out gate: passed in 1 s\nout gate: FAILED (status 1)\n"));

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
