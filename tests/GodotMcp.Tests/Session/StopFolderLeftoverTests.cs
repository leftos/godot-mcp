using System.Diagnostics;
using System.Text.Json;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Session;

/// <summary>
/// stop_project on a folder with no session clearing a dead server's leftover override.cfg and armed.json, and the strict
/// clear under the owners list it runs. A dead owner is this process's id with a start time it never had.
/// </summary>
public sealed class StopFolderLeftoverTests : IAsyncDisposable
{
    private static readonly OverrideOwner Dead = new(Environment.ProcessId, OverrideOwner.Current.StartTicks - 1);

    private readonly RegistryHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public void ClearStaleTouchesNothingWhileTheListIsHeld()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string path = WriteMarked(alpha, Dead.ToString());
        byte[] before = File.ReadAllBytes(path);
        OverrideFolders folders = _harness.Sessions.OverrideFolders;

        LeftoverClear cleared;
        using (new FileStream(folders.ListPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            cleared = folders.ClearStale(alpha);
        }

        Assert.Equal((false, false), (cleared.OverrideRemoved, cleared.ArmedRemoved));
        Assert.Equal(["the owners list could not be opened (busy, or see the server's stderr), nothing removed"], cleared.Left);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task StopRemovesAMarkedOverrideWhoseOwnersHaveExited()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string path = WriteMarked(alpha, Dead.ToString());

        object stopped = await _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken);

        Assert.Equal(new HostStopResult(alpha, HeadlessHostStopped: false) { OverrideRemoved = true, Folder = FolderState.NothingLeft }, stopped);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AStopThatRemovesAStaleOverrideLeavesTheFolderClean()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        WriteMarked(alpha, Dead.ToString());

        var stopped = (HostStopResult)await _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken);

        FolderState folder = Assert.IsType<FolderState>(stopped.Folder);
        Assert.True(folder.Clean);
        Assert.Empty(folder.Holders);
    }

    // The attached session's stop failed to release the file, which still names this server: stop_project with the folder
    // releases it again, now that no session of this server holds the folder.
    [Fact]
    public async Task AFailedReleaseIsRetriedByStopProjectOnTheFolder()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        await _harness.EndAttachedGameAsync(alpha, "server");
        using (StopFolderReleaseFailureTests.HoldOverrideOpen(alpha))
        {
            StopResult failed = await _harness.Sessions.StopAsync("server", cancellation);
            Assert.False(failed.OverrideRemoved);
        }

        object stopped = await _harness.Sessions.StopFolderAsync(alpha, null, cancellation);

        Assert.Equal(new HostStopResult(alpha, HeadlessHostStopped: false) { OverrideRemoved = true, Folder = FolderState.NothingLeft }, stopped);
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task StopRemovesALeftoverArmFile()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string path = ArmFile.PathIn(alpha);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{{\"quiet\":true,\"shutOutRealGamepads\":false,\"mute\":false,\"owners\":[\"{Dead}\"]}}");

        object stopped = await _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken);

        Assert.Equal(new HostStopResult(alpha, HeadlessHostStopped: false) { ArmedRemoved = true, Folder = FolderState.NothingLeft }, stopped);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task StopRefusesAndLeavesTheProjectsOwnOverrideByteIdentical()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string path = OverrideFile.PathIn(alpha);
        File.WriteAllText(path, "[application]\nconfig/name=\"Mine\"\n");
        byte[] before = File.ReadAllBytes(path);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"No Godot session or warm headless host runs in {alpha}. Override.cfg is the project's own (no godot-mcp marker), left in place.",
            refused.Message
        );
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task StopRefusesAndKeepsAnOverrideALiveOwnerNames()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        Process foreign = _harness.StartOwnedGame();
        string path = WriteMarked(alpha, new OverrideOwner(foreign.Id, foreign.StartTime.ToUniversalTime().Ticks).ToString());

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"No Godot session or warm headless host runs in {alpha}. Override.cfg names a live owner (pid {foreign.Id}), left in place.",
            refused.Message
        );
        Assert.True(File.Exists(path));
        // The refusal carries no result, so the folder is read as stop_project's folder field reads it after a stop.
        FolderState folder = FolderHolders.After(alpha);
        Assert.False(folder.Clean);
        FolderHolder holder = Assert.Single(folder.Holders);
        Assert.Equal(
            (OverrideFile.FileName, foreign.Id, new DateTimeOffset(foreign.StartTime.ToUniversalTime().Ticks, TimeSpan.Zero), false),
            (holder.File, holder.Pid, holder.StartedAt, holder.ThisServer)
        );
    }

    [Fact]
    public async Task StopRefusesAndKeepsBothFilesOfAFolderThisServerArmed()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await _harness.Sessions.ArmAsync(alpha, new ArmSettings(Quiet: false, ShutOutRealGamepads: false, Mute: false), cancellation);
        byte[] overrideBefore = File.ReadAllBytes(OverrideFile.PathIn(alpha));
        byte[] armBefore = File.ReadAllBytes(ArmFile.PathIn(alpha));

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _harness.Sessions.StopFolderAsync(alpha, null, cancellation));

        Assert.Equal(
            $"No Godot session or warm headless host runs in {alpha}. Override.cfg belongs to this server's own session or arm, left in "
                + "place; armed.json belongs to this server's own session or arm, left in place.",
            refused.Message
        );
        Assert.Equal(overrideBefore, File.ReadAllBytes(OverrideFile.PathIn(alpha)));
        Assert.Equal(armBefore, File.ReadAllBytes(ArmFile.PathIn(alpha)));
    }

    [Fact]
    public async Task StopRemovesTheOverrideAndWarnsOfAnArmFileHeldOpen()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string overridePath = WriteMarked(alpha, Dead.ToString());
        string armPath = ArmFile.PathIn(alpha);
        Directory.CreateDirectory(Path.GetDirectoryName(armPath)!);
        File.WriteAllText(armPath, $"{{\"quiet\":true,\"shutOutRealGamepads\":false,\"mute\":false,\"owners\":[\"{Dead}\"]}}");

        object stopped;
        using (new FileStream(armPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stopped = await _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            new HostStopResult(alpha, HeadlessHostStopped: false)
            {
                OverrideRemoved = true,
                Warning = "armed.json is open in another process (a running Godot?), left in place",
                Folder = new FolderState(false, []),
            },
            stopped
        );
        Assert.False(File.Exists(overridePath));
        Assert.True(File.Exists(armPath));
    }

    // The documented exception to stopProjectWouldRemove: the owners judge a file stale, but a handle another process holds
    // open without sharing deletion (Godot opens files sharing read and write only) keeps it at stop time.
    [Fact]
    public async Task AnArmFileHeldOpenWithoutDeleteSharingIsKeptThoughTheViewSaysItWouldGo()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string armPath = WriteStaleArmFile(alpha);

        FolderHoldersView view;
        SessionException refused;
        using (new FileStream(armPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            view = _harness.Sessions.Holders(alpha);
            refused = await Assert.ThrowsAsync<SessionException>(() =>
                _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken)
            );
        }

        Assert.True(view.StopProjectWouldRemove);
        Assert.Null(view.Reason);
        Assert.Equal(
            $"No Godot session or warm headless host runs in {alpha}. Armed.json is open in another process (a running Godot?), left in place.",
            refused.Message
        );
        Assert.True(File.Exists(armPath));
    }

    public const string ArmHeldOpen = "arm held open";

    // For each fixture above, the view read just before the stop agrees with it: it would remove a file exactly when the stop
    // removes one, and its reasons are the clauses of the stop's warning or refusal. ArmHeldOpen is the documented exception.
    [Theory]
    [InlineData("stale override")]
    [InlineData("stale arm")]
    [InlineData("project's own")]
    [InlineData("live owner")]
    [InlineData("owned here")]
    [InlineData(ArmHeldOpen)]
    public async Task TheViewAgreesWithTheStopThatFollowsIt(string row)
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        using IDisposable? held = await ArrangeAsync(row, alpha);

        FolderHoldersView view = _harness.Sessions.Holders(alpha);
        (bool removed, List<string> left) = await StopClausesAsync(alpha);

        List<string> kept = view.Reason is null
            ? []
            : [.. view.Reason.Split("; ").Select(clause => clause.Replace(", so stop_project would keep it", ""))];
        if (row == ArmHeldOpen)
        {
            Assert.True(view.StopProjectWouldRemove);
            Assert.Empty(kept);
            Assert.False(removed);
            Assert.Equal(["armed.json is open in another process (a running Godot?)"], left);
            return;
        }

        Assert.Equal(removed, view.StopProjectWouldRemove);
        Assert.Equal(left, kept);
    }

    /// <summary>Sets up the folder for a row of <see cref="TheViewAgreesWithTheStopThatFollowsIt"/>; returns a handle to hold, if any.</summary>
    private async Task<IDisposable?> ArrangeAsync(string row, string projectDir)
    {
        switch (row)
        {
            case "stale override":
                WriteMarked(projectDir, Dead.ToString());
                return null;
            case "stale arm":
                WriteStaleArmFile(projectDir);
                return null;
            case "project's own":
                File.WriteAllText(OverrideFile.PathIn(projectDir), "[application]\nconfig/name=\"Mine\"\n");
                return null;
            case "live owner":
                Process foreign = _harness.StartOwnedGame();
                WriteMarked(projectDir, new OverrideOwner(foreign.Id, foreign.StartTime.ToUniversalTime().Ticks).ToString());
                return null;
            case "owned here":
                await _harness.Sessions.ArmAsync(projectDir, new ArmSettings(false, false, false), TestContext.Current.CancellationToken);
                return null;
            default:
                return new FileStream(WriteStaleArmFile(projectDir), FileMode.Open, FileAccess.Read, FileShare.Read);
        }
    }

    /// <summary>Stops the folder; whether it removed a file, and the clauses of its warning or refusal, without their tail.</summary>
    private async Task<(bool Removed, List<string> Left)> StopClausesAsync(string projectDir)
    {
        try
        {
            var stopped = (HostStopResult)await _harness.Sessions.StopFolderAsync(projectDir, null, TestContext.Current.CancellationToken);
            return (stopped.OverrideRemoved || stopped.ArmedRemoved, Clauses(stopped.Warning));
        }
        catch (SessionException refused)
        {
            string rest = refused.Message[$"No Godot session or warm headless host runs in {projectDir}.".Length..].Trim().TrimEnd('.');
            return (false, Clauses(rest.Length == 0 ? null : char.ToLowerInvariant(rest[0]) + rest[1..]));
        }
    }

    private static List<string> Clauses(string? text) =>
        text is null ? [] : [.. text.Split("; ").Select(clause => clause.Replace(", left in place", ""))];

    /// <summary>Writes a stale armed.json naming only a dead owner; returns its path.</summary>
    private static string WriteStaleArmFile(string projectDir)
    {
        string path = ArmFile.PathIn(projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{{\"quiet\":true,\"shutOutRealGamepads\":false,\"mute\":false,\"owners\":[\"{Dead}\"]}}");
        return path;
    }

    [Fact]
    public void TheResultLeavesOutWhatWasNotRemovedAndNoWarning() =>
        Assert.Equal(
            "{\"projectPath\":\"X:/game\",\"headlessHostStopped\":true,\"folder\":{\"clean\":true,\"holders\":[]}}",
            JsonSerializer.Serialize(new HostStopResult("X:/game", HeadlessHostStopped: true) { Folder = FolderState.NothingLeft }, ProjectTools.Json)
        );

    [Fact]
    public void TheResultCarriesWhatWasRemovedAndTheWarning() =>
        Assert.Equal(
            "{\"projectPath\":\"X:/game\",\"headlessHostStopped\":false,\"overrideRemoved\":true,\"warning\":\"armed.json is open\","
                + "\"folder\":{\"clean\":false,\"holders\":[{\"file\":\"armed.json\",\"pid\":4242,"
                + "\"startedAt\":\"2026-11-10T12:00:00+00:00\",\"serverVersion\":\"0.39.0-abc1234\",\"thisServer\":false}]}}",
            JsonSerializer.Serialize(
                new HostStopResult("X:/game", HeadlessHostStopped: false)
                {
                    OverrideRemoved = true,
                    Warning = "armed.json is open",
                    Folder = new FolderState(
                        false,
                        [new FolderHolder("armed.json", 4242, new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero), "0.39.0-abc1234", false)]
                    ),
                },
                ProjectTools.Json
            )
        );

    [Fact]
    public async Task AFailedClearSaysWhatItRemovedFirstAndKeepsTheCause()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string overridePath = WriteMarked(alpha, Dead.ToString());
        string armPath = WriteReadOnlyStaleArmFile(alpha);
        try
        {
            SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
                _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken)
            );

            Assert.StartsWith($"Removed override.cfg in {alpha}, then clearing armed.json failed: ", refused.Message, StringComparison.Ordinal);
            Assert.IsType<UnauthorizedAccessException>(refused.InnerException);
            Assert.False(File.Exists(overridePath));
            // No handle of the failed removal is left open: an exclusive open succeeds.
            new FileStream(armPath, FileMode.Open, FileAccess.Read, FileShare.None).Dispose();
        }
        finally
        {
            File.SetAttributes(armPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task AFailedClearEndsWithWhyItLeftAFile()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        Process foreign = _harness.StartOwnedGame();
        string overridePath = WriteMarked(alpha, new OverrideOwner(foreign.Id, foreign.StartTime.ToUniversalTime().Ticks).ToString());
        string armPath = WriteReadOnlyStaleArmFile(alpha);
        try
        {
            SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
                _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken)
            );

            Assert.StartsWith($"Clearing armed.json in {alpha} failed: ", refused.Message, StringComparison.Ordinal);
            Assert.EndsWith($". Override.cfg names a live owner (pid {foreign.Id}), left in place.", refused.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(overridePath));
        }
        finally
        {
            File.SetAttributes(armPath, FileAttributes.Normal);
        }
    }

    /// <summary>Writes a stale armed.json naming only a dead owner and marks it read-only, so its delete throws; returns its path.</summary>
    private static string WriteReadOnlyStaleArmFile(string projectDir)
    {
        string path = ArmFile.PathIn(projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{{\"quiet\":true,\"shutOutRealGamepads\":false,\"mute\":false,\"owners\":[\"{Dead}\"]}}");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    /// <summary>Writes a marked override.cfg in the folder whose owners line names <paramref name="owners"/>; returns its path.</summary>
    private static string WriteMarked(string projectDir, string owners)
    {
        string path = OverrideFile.PathIn(projectDir);
        File.WriteAllText(path, $"{OverrideFile.Marker}\n{OverrideFile.OwnersPrefix}{owners}\n[autoload]\n\nGodotMcpBridge=\"*D:/gone/bridge.gd\"\n");
        return path;
    }
}
