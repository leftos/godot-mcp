using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A cold C# headless run and the prep's import whose Godot crashed at boot with 0xC06D007F, through a <c>GODOT_PATH</c> that
/// names a batch file standing in for Godot: run once more when the registry (or the prep's context) says so and nothing ran,
/// and failed as before otherwise. A cold run that wrote its result, or whose log has a line, is never run again.
/// </summary>
[Collection(LaunchRefusesNoGlTests.GodotPathCollection)]
public sealed class BootCrashRetryTests : IDisposable
{
    private const string ColdRetried = "retrying the cold headless";
    private const string ImportRetried = "retrying the Godot import of";
    private const string CrashExit = "exit -1066598273";

    private readonly TempDirectory _temp = new();
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly RecordingLogger<GodotSession> _log = new();
    private readonly string? _configuredGodot = Environment.GetEnvironmentVariable(Installation.GodotPathVariable);
    private readonly string _calls;
    private readonly string _marker;

    public BootCrashRetryTests()
    {
        _calls = _temp.Combine("calls.txt");
        _marker = _temp.Combine("crashed-once.txt");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Installation.GodotPathVariable, _configuredGodot);
        _listener.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task AColdRunThatCrashedAtBootRunsOnceMore()
    {
        FakeGodot(CrashOnceLines());

        SessionException failed = await Assert.ThrowsAnyAsync<SessionException>(() => RunColdAsync(retry: true));

        Assert.Contains("wrote no result (Godot exited 0)", failed.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(_marker), "the first run crashed");
        Assert.Equal(2, Calls());
        Assert.Single(_log.Warnings, warning => warning.Contains(ColdRetried, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AColdRunThatCrashedAtBootFailsAsBeforeWhenTheRegistryDoesNotRetry()
    {
        FakeGodot(CrashOnceLines());

        SessionException failed = await Assert.ThrowsAnyAsync<SessionException>(() => RunColdAsync(retry: false));

        Assert.Contains("wrote no result (Godot exited -1066598273)", failed.Message, StringComparison.Ordinal);
        Assert.Equal(1, Calls());
        Assert.DoesNotContain(_log.Warnings, warning => warning.Contains("0xC06D007F", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AColdRunThatWroteItsResultBeforeTheCrashCodeIsNotRunAgain()
    {
        FakeGodot(
            "set \"r=%~7\"",
            "set \"r=%r:.request.json=.result.json%\"",
            "set \"r=%r:/=\\%\"",
            ">\"%r%\" echo {\"ok\":true,\"result\":{\"answer\":42}}",
            CrashExit
        );

        HeadlessResult result = await RunColdAsync(retry: true);

        Assert.Equal(42, result.Result!["answer"]!.GetValue<int>());
        Assert.Equal(1, Calls());
        Assert.DoesNotContain(_log.Warnings, warning => warning.Contains("0xC06D007F", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AColdRunThatLoggedALineBeforeTheCrashCodeIsNotRunAgain()
    {
        FakeGodot("echo Godot Engine v4.7.2.stable", CrashExit);

        SessionException failed = await Assert.ThrowsAnyAsync<SessionException>(() => RunColdAsync(retry: true));

        Assert.Contains("wrote no result (Godot exited -1066598273)", failed.Message, StringComparison.Ordinal);
        Assert.Equal(1, Calls());
        Assert.DoesNotContain(_log.Warnings, warning => warning.Contains("0xC06D007F", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnImportThatCrashedAtBootRunsOnceMore()
    {
        FakeGodot(CrashOnceLines());

        PrepResult prep = await ImportAsync(retry: true);

        Assert.Equal("done", prep.Import);
        Assert.Equal(2, Calls());
        Assert.Single(_log.Warnings, warning => warning.Contains(ImportRetried, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnImportThatCrashedAtBootFailsAsBeforeWhenTheContextDoesNotRetry()
    {
        FakeGodot(CrashOnceLines());

        SessionException failed = await Assert.ThrowsAnyAsync<SessionException>(() => ImportAsync(retry: false));

        Assert.Contains("The Godot import of", failed.Message, StringComparison.Ordinal);
        Assert.Contains("exited -1066598273", failed.Message, StringComparison.Ordinal);
        Assert.Equal(1, Calls());
        Assert.DoesNotContain(_log.Warnings, warning => warning.Contains("0xC06D007F", StringComparison.Ordinal));
    }

    /// <summary>A Godot that crashes at boot on its first call, printing nothing, and exits 0 on every later one.</summary>
    private string[] CrashOnceLines() => [$"if exist \"{_marker}\" exit 0", $"type nul > \"{_marker}\"", CrashExit];

    /// <summary>
    /// Writes a batch file that counts its calls in <see cref="_calls"/> and then runs <paramref name="lines"/>, and points
    /// <c>GODOT_PATH</c> at it.
    /// </summary>
    private void FakeGodot(params string[] lines)
    {
        string godot = _temp.Combine("godot.cmd");
        string[] script = ["@echo off", $"echo x>>\"{_calls}\"", .. lines];
        File.WriteAllText(godot, string.Join("\r\n", script) + "\r\n");
        Environment.SetEnvironmentVariable(Installation.GodotPathVariable, godot);
    }

    private int Calls() => File.Exists(_calls) ? File.ReadAllLines(_calls).Length : 0;

    /// <summary>A validate run on a C# project, which runs cold, with no prep.</summary>
    private async Task<HeadlessResult> RunColdAsync(bool retry)
    {
        string game = ProjectPaths.Normalise(ProjectPrepFixtures.CreateGame(_temp, "Game", "Game.csproj"));
        using SessionRegistry registry = new(_listener, _log)
        {
            OverrideFolders = new OverrideFolders(_temp.Combine("override-folders.txt"), TextWriter.Null),
            Dormant = new DormantGames(_ => new ProcessStart(Exists: true, StartTime: null), TextWriter.Null),
            RetryBootCrashOnce = retry,
        };
        HeadlessRequest request = new(game, "validate", new JsonObject { ["targets"] = new JsonArray() }, Prepare: false, TimeSpan.FromMinutes(1));
        return await HeadlessRunner.RunAsync(registry, request, TestContext.Current.CancellationToken);
    }

    /// <summary>The prep of a folder whose import is due: its sidecar's md5 is not the one the record holds.</summary>
    private async Task<PrepResult> ImportAsync(bool retry)
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "icon.png"), "png", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "icon.png.import"), "importer=\"texture\"\n", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(ImportFingerprints.PathIn(game), "{\"icon.png.import\":\"a stale md5\"}", ProjectPrepFixtures.Old);
        PrepContext context = new(game, _log, () => [], () => { }) { RetryBootCrashOnce = retry };
        return await ProjectPrep.RunAsync(context, TestContext.Current.CancellationToken);
    }
}
