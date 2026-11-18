using System.Diagnostics;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Tests.Wire;

namespace GodotMcp.Tests.Session;

/// <summary>Arming and disarming a folder: its override.cfg and armed.json, the settings they fix, and shutdown.</summary>
public sealed class SessionArmTests : IAsyncDisposable
{
    private static readonly ArmSettings Loud = new(Quiet: false, ShutOutRealGamepads: false, Mute: false);
    private static readonly ArmSettings Quiet = new(Quiet: true, ShutOutRealGamepads: false, Mute: false);
    private static readonly ArmSettings Muted = new(Quiet: false, ShutOutRealGamepads: false, Mute: true);

    private readonly RegistryHarness _harness = new();
    private Process? _foreign;

    public async ValueTask DisposeAsync()
    {
        if (_foreign is not null)
        {
            _foreign.Kill(entireProcessTree: true);
            _foreign.WaitForExit(10_000);
            _foreign.Dispose();
        }

        await _harness.DisposeAsync();
    }

    [Fact]
    public async Task ArmingAFolderAnotherServerArmedWithOtherSettingsIsRefused()
    {
        string alpha = _harness.Project("alpha");
        OverrideOwner foreign = StartForeignOwner();
        string armFile = ArmFile.PathIn(alpha);
        Directory.CreateDirectory(Path.GetDirectoryName(armFile)!);
        File.WriteAllText(armFile, $"{{\"quiet\":false,\"shutOutRealGamepads\":false,\"mute\":true,\"owners\":[\"{foreign}\"]}}");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.ArmAsync(alpha, Loud, TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"{alpha} is armed by another godot-mcp server (pid {foreign.ProcessId}) with quiet=false, shutOutRealGamepads=false and "
                + "mute=true; arm it with the same values, or disarm it in that server first.",
            refused.Message
        );
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
        Assert.Equal([foreign], ArmFile.Read(alpha)!.Owners);
        Assert.Empty(_harness.Sessions.ListArmed());
    }

    [Fact]
    public async Task ALiveProcessSessionDoesNotBindTheArmOrAPreviewsSettings()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        GodotSession process = await _harness.Sessions.ReserveProcessAsync("server", alpha, defaultName: true);

        GodotSession preview = _harness.Sessions.ReservePreview(alpha);
        ArmState armed = await _harness.Sessions.ArmAsync(alpha, Quiet, cancellation);

        Assert.True(process.IsLive);
        Assert.False(process.Quiet);
        Assert.True(preview.Quiet);
        Assert.False(preview.ShutOutRealGamepads);
        Assert.True(armed.Quiet);
        Assert.False(armed.ShutOutRealGamepads);
    }

    [Fact]
    public async Task ArmWritesTheOverrideAndTheArmFileAndListsTheDormantGames()
    {
        string alpha = _harness.Project("alpha");
        RegistryHarness.WriteDormant(alpha, 4101);

        ArmState armed = await _harness.Sessions.ArmAsync(alpha, Quiet, TestContext.Current.CancellationToken);

        Assert.Equal((alpha, true, false, false), (armed.ProjectPath, armed.Quiet, armed.ShutOutRealGamepads, armed.Mute));
        Assert.Equal(4101, Assert.Single(armed.Dormant).Pid);
        Assert.True(OverrideFile.IsOurs(OverrideFile.PathIn(alpha)));
        Assert.Equal(Quiet, ArmFile.Read(alpha)!.Settings);
        Assert.Equal([OverrideOwner.Current], ArmFile.Read(alpha)!.Owners);
        ArmState listed = Assert.Single(_harness.Sessions.ListArmed());
        Assert.Equal((alpha, 4101), (listed.ProjectPath, Assert.Single(listed.Dormant).Pid));
    }

    [Fact]
    public async Task AnArmedFolderKeepsItsOverrideAcrossADetach()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Loud, cancellation);
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello());

        DetachResult detached = await _harness.Sessions.DetachAsync("server", cancellation);

        Assert.False(detached.OverrideRemoved);
        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
        Assert.True(File.Exists(ArmFile.PathIn(alpha)));
    }

    // A stop, a game's exit and a replaced session all release the folder through ReleaseFolder; a run needs a launched
    // Godot, so an ended attached session, the folder's last, is released here as a stop releases its run's.
    [Fact]
    public async Task AnArmedFolderKeepsItsOverrideWhenItsLastSessionReleasesIt()
    {
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Loud, TestContext.Current.CancellationToken);
        await _harness.EndAttachedGameAsync(alpha, "server");

        bool removed = _harness.Sessions.ReleaseFolder(_harness.Sessions.Resolve("server"));

        Assert.False(removed);
        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task AHeadlessCallOnAnArmedFolderKeepsItsOverride()
    {
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Loud, TestContext.Current.CancellationToken);

        HeadlessRunner.ClearFolder(_harness.Sessions, alpha);

        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
        Assert.Contains(OverrideOwner.Current, OverrideFile.LiveOwners(alpha));
    }

    [Fact]
    public async Task AnAttachWithoutOptionsOnAQuietArmedFolderTakesTheArmsSettings()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Quiet, cancellation);
        string attachFile = AttachFile.PathIn(alpha);

        Task<AttachResult> attach = _harness.Sessions.AttachAsync(
            new AttachRequest(alpha, "server", RegistryHarness.LongWait, null, null, null),
            cancellation
        );
        await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile) || attach.IsCompleted);
        JsonNode written = JsonNode.Parse(File.ReadAllText(attachFile))!;
        using FakeBridge game = await FakeBridge.DialAsync(_harness.Listener.Port, written["token"]!.GetValue<string>(), alpha, cancellation);
        AttachResult result = await attach;

        Assert.True(result.Quiet);
        Assert.Equal((true, false), (written["quiet"]!.GetValue<bool>(), written["shutOutRealGamepads"]!.GetValue<bool>()));
        Assert.True(_harness.Sessions.Resolve("server").Quiet);
    }

    [Fact]
    public async Task AnAttachWithoutMuteOnAMutedArmedFolderTakesTheArmsMute()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        ArmState armed = await _harness.Sessions.ArmAsync(alpha, Muted, cancellation);

        (JsonNode written, FakeBridge game) = await AttachAndReadTheAttachFileAsync(
            new AttachRequest(alpha, "server", RegistryHarness.LongWait, null, null, null)
        );
        using FakeBridge joined = game;

        Assert.True(armed.Mute);
        Assert.Equal((false, true), (written["quiet"]!.GetValue<bool>(), written["mute"]!.GetValue<bool>()));
        Assert.True(_harness.Sessions.Resolve("server").Mute);
    }

    [Fact]
    public async Task AnAttachWithAnotherMuteThanTheArmIsAccepted()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Muted, cancellation);

        (JsonNode written, FakeBridge game) = await AttachAndReadTheAttachFileAsync(
            new AttachRequest(alpha, "server", RegistryHarness.LongWait, null, null, null) { Mute = false }
        );
        using FakeBridge joined = game;

        Assert.False(written["mute"]!.GetValue<bool>());
        Assert.False(_harness.Sessions.Resolve("server").Mute);
    }

    [Fact]
    public async Task AnExplicitQuietFalseOnAQuietArmedFolderIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Quiet, cancellation);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(new AttachRequest(alpha, "client", RegistryHarness.LongWait, false, false, null), cancellation)
        );

        Assert.Equal($"{alpha} is armed with quiet=true; start this session with the same value, or disarm_project first.", refused.Message);
    }

    [Fact]
    public async Task AnArmWhoseSettingDiffersFromTheLiveSessionsIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.StartWaitingAttachAsync(alpha, "server");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _harness.Sessions.ArmAsync(alpha, Quiet, cancellation));

        Assert.Equal($"Sessions on {alpha} run with quiet=false; arm it with the same value, or stop them first.", refused.Message);
        Assert.False(File.Exists(ArmFile.PathIn(alpha)));
        Assert.Empty(_harness.Sessions.ListArmed());
    }

    [Fact]
    public async Task ArmingAgainWithTheSameSettingsChangesNothing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Quiet, cancellation);
        string before = File.ReadAllText(ArmFile.PathIn(alpha));

        ArmState again = await _harness.Sessions.ArmAsync(alpha, Quiet, cancellation);

        Assert.True(again.Quiet);
        Assert.Equal(before, File.ReadAllText(ArmFile.PathIn(alpha)));
        Assert.Single(_harness.Sessions.ListArmed());
    }

    [Fact]
    public async Task ArmingAgainWithOtherSettingsIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Quiet, cancellation);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _harness.Sessions.ArmAsync(alpha, Loud, cancellation));

        Assert.Equal(
            $"{alpha} is already armed with quiet=true, shutOutRealGamepads=false and mute=false; disarm_project first to arm it with "
                + "other settings.",
            refused.Message
        );
        Assert.Equal(Quiet, ArmFile.Read(alpha)!.Settings);
    }

    [Fact]
    public async Task ArmingAgainWithAnotherMuteIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Muted, cancellation);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _harness.Sessions.ArmAsync(alpha, Loud, cancellation));

        Assert.Equal(
            $"{alpha} is already armed with quiet=false, shutOutRealGamepads=false and mute=true; disarm_project first to arm it with "
                + "other settings.",
            refused.Message
        );
        Assert.Equal(Muted, ArmFile.Read(alpha)!.Settings);
    }

    [Fact]
    public void DisarmingAFolderThatIsNotArmedIsRefused()
    {
        string alpha = _harness.Project("alpha");

        SessionException refused = Assert.Throws<SessionException>(() => _harness.Sessions.Disarm(alpha));

        Assert.Equal($"{alpha} is not armed; arm_project arms it.", refused.Message);
    }

    [Fact]
    public async Task DisarmRemovesTheArmFileAndTheOverrideWhenNoSessionUsesTheFolder()
    {
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Loud, TestContext.Current.CancellationToken);

        DisarmResult disarmed = _harness.Sessions.Disarm(alpha);

        Assert.Equal(new DisarmResult(alpha, true), disarmed);
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
        Assert.False(File.Exists(ArmFile.PathIn(alpha)));
        Assert.Empty(_harness.Sessions.ListArmed());
    }

    [Fact]
    public async Task DisarmKeepsTheOverrideWhileALiveSessionUsesTheFolder()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Loud, cancellation);
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello());

        DisarmResult disarmed = _harness.Sessions.Disarm(alpha);
        DetachResult detached = await _harness.Sessions.DetachAsync("server", cancellation);

        Assert.False(disarmed.OverrideRemoved);
        Assert.False(File.Exists(ArmFile.PathIn(alpha)));
        Assert.True(detached.OverrideRemoved);
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task ShutdownDisarmsEveryArmedFolder()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        string beta = _harness.Project("beta");
        await _harness.Sessions.ArmAsync(alpha, Loud, cancellation);
        await _harness.Sessions.ArmAsync(beta, Quiet, cancellation);

        _harness.Sessions.Shutdown();

        Assert.All([alpha, beta], folder => Assert.False(File.Exists(ArmFile.PathIn(folder))));
        Assert.All([alpha, beta], folder => Assert.False(File.Exists(OverrideFile.PathIn(folder))));
        Assert.Empty(_harness.Sessions.ListArmed());
    }

    [Fact]
    public async Task AFailedDisarmReleaseIsRetriedAtShutdown()
    {
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Loud, TestContext.Current.CancellationToken);

        using (StopFolderReleaseFailureTests.HoldOverrideOpen(alpha))
        {
            Assert.Throws<IOException>(() => _harness.Sessions.Disarm(alpha));
        }

        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
        _harness.Sessions.Shutdown();
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task AFailedDisarmArmFileReleaseIsRetriedAtShutdown()
    {
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Loud, TestContext.Current.CancellationToken);

        // Read and write are shared, delete is not, so the disarm's removal of the arm file throws.
        using (new FileStream(ArmFile.PathIn(alpha), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.Throws<IOException>(() => _harness.Sessions.Disarm(alpha));
        }

        Assert.True(File.Exists(ArmFile.PathIn(alpha)));
        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
        _harness.Sessions.Shutdown();
        Assert.False(File.Exists(ArmFile.PathIn(alpha)));
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task AFailedDisarmArmFileReleaseIsRetriedByStopProject()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Loud, cancellation);

        // Read and write are shared, delete is not, so the disarm's removal of the arm file throws.
        using (new FileStream(ArmFile.PathIn(alpha), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.Throws<IOException>(() => _harness.Sessions.Disarm(alpha));
        }

        await _harness.Sessions.StopFolderAsync(alpha, session: null, cancellation);

        Assert.False(File.Exists(ArmFile.PathIn(alpha)));
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task AFailedDisarmArmFileReleaseSurvivesAStopProjectUntilShutdown()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Loud, cancellation);

        // The stop's retry meets the file still held, so its failure is reported and the arm file stays owed until the exit.
        using (new FileStream(ArmFile.PathIn(alpha), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.Throws<IOException>(() => _harness.Sessions.Disarm(alpha));
            await Assert.ThrowsAsync<SessionException>(() => _harness.Sessions.StopFolderAsync(alpha, session: null, cancellation));
        }

        _harness.Sessions.Shutdown();

        Assert.False(File.Exists(ArmFile.PathIn(alpha)));
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task AnArmFileDebtWhoseFileIsGoneStopsAsNothingOwed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Loud, cancellation);
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello());

        // The live session holds the override, so the failed arm release leaves only the arm file owed.
        using (new FileStream(ArmFile.PathIn(alpha), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.Throws<IOException>(() => _harness.Sessions.Disarm(alpha));
        }

        await _harness.Sessions.StopAsync("server", cancellation);

        // A peer's sweep deleted the file, so the debt names nothing any more and the stop falls through as having nothing owed.
        File.Delete(ArmFile.PathIn(alpha));

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.StopFolderAsync(alpha, session: null, cancellation)
        );

        Assert.StartsWith($"No Godot session or warm headless host runs in {alpha}.", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArmFileDebtOnASharedFileNamesTheOwnerItLeft()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Loud, cancellation);
        OverrideOwner foreign = StartForeignOwner();
        string armFile = ArmFile.PathIn(alpha);
        File.WriteAllText(
            armFile,
            $"{{\"quiet\":false,\"shutOutRealGamepads\":false,\"mute\":false,\"owners\":[\"{OverrideOwner.Current}\",\"{foreign}\"]}}"
        );

        // Read-only sharing lets the disarm read the owners and refuses its rewrite, so the release fails with the file shared.
        using (new FileStream(armFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => _harness.Sessions.Disarm(alpha));
        }

        HostStopResult stopped = Assert.IsType<HostStopResult>(await _harness.Sessions.StopFolderAsync(alpha, session: null, cancellation));

        Assert.False(stopped.ArmedRemoved);
        Assert.Contains(
            $"this server's owner entry was taken off {ArmFile.FileName}, which stays for the live owner it names (pid {foreign.ProcessId})",
            stopped.Warning,
            StringComparison.Ordinal
        );
        Assert.True(File.Exists(armFile));
    }

    [Fact]
    public async Task AStopTakingOffAnArmEntryWithTheOverrideGoneSaysSo()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Loud, cancellation);
        OverrideOwner foreign = StartForeignOwner();
        string armFile = ArmFile.PathIn(alpha);
        File.WriteAllText(
            armFile,
            $"{{\"quiet\":false,\"shutOutRealGamepads\":false,\"mute\":false,\"owners\":[\"{OverrideOwner.Current}\",\"{foreign}\"]}}"
        );
        using (new FileStream(armFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => _harness.Sessions.Disarm(alpha));
        }

        // The override its own retry owes was removed meanwhile, so only the arm entry's taking off is left to report.
        File.Delete(OverrideFile.PathIn(alpha));

        HostStopResult stopped = Assert.IsType<HostStopResult>(await _harness.Sessions.StopFolderAsync(alpha, session: null, cancellation));

        Assert.False(stopped.OverrideRemoved);
        Assert.False(stopped.ArmedRemoved);
        Assert.Contains($"this server's owner entry was taken off {ArmFile.FileName}", stopped.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHoldersViewPredictsAnArmFileDebtsRemoval()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Loud, cancellation);
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello());

        using (new FileStream(ArmFile.PathIn(alpha), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.Throws<IOException>(() => _harness.Sessions.Disarm(alpha));
        }

        // The session's own stop releases the override, leaving the arm file as the only file stop_project would remove.
        await _harness.Sessions.StopAsync("server", cancellation);
        FolderHoldersView view = _harness.Sessions.Holders(alpha);

        Assert.True(view.StopProjectWouldRemove);
        Assert.Equal("armed.json still names this server, whose earlier release of it failed, so stop_project would remove it", view.Reason);
    }

    [Fact]
    public async Task AFailedArmReleaseIsRetriedAtShutdown()
    {
        string alpha = _harness.Project("alpha");
        Directory.CreateDirectory(ArmFile.PathIn(alpha));
        _harness.Sessions.ReleaseOverride = _ => throw new IOException("the test keeps the file from being released");

        try
        {
            Exception? failed = await Record.ExceptionAsync(() => _harness.Sessions.ArmAsync(alpha, Loud, TestContext.Current.CancellationToken));
            Assert.IsType<UnauthorizedAccessException>(failed);
        }
        finally
        {
            _harness.Sessions.ReleaseOverride = OverrideFile.Release;
        }

        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
        Directory.Delete(ArmFile.PathIn(alpha));
        _harness.Sessions.Shutdown();
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    /// <summary>A child process that runs until the test ends, as the owner another live server would be.</summary>
    private OverrideOwner StartForeignOwner()
    {
        _foreign = Process.Start(
            new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            }
        )!;
        return new OverrideOwner(_foreign.Id, _foreign.StartTime.ToUniversalTime().Ticks);
    }

    /// <summary>Attaches as <paramref name="request"/> asks, dialling in as the game once the attach file is written; returns its content.</summary>
    private async Task<(JsonNode Written, FakeBridge Game)> AttachAndReadTheAttachFileAsync(AttachRequest request)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string attachFile = AttachFile.PathIn(request.ProjectPath);
        Task<AttachResult> attach = _harness.Sessions.AttachAsync(request, cancellation);
        await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile) || attach.IsCompleted);
        JsonNode written = JsonNode.Parse(File.ReadAllText(attachFile))!;
        FakeBridge game = await FakeBridge.DialAsync(_harness.Listener.Port, written["token"]!.GetValue<string>(), request.ProjectPath, cancellation);
        await attach;
        return (written, game);
    }
}
