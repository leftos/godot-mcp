using System.Diagnostics;
using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A run_scratches scene session beside the folder's other sessions and its arm: it is exempt from their quiet and pad
/// agreement both ways, always runs quiet with the real pads live, never rewrites a file anything else of this server holds,
/// and keeps the file from being released while it is live.
/// </summary>
public sealed class ScratchSessionSettingsTests : IAsyncDisposable
{
    private static readonly ArmSettings Loud = new(Quiet: false, ShutOutRealGamepads: false, Mute: false);
    private static readonly ArmSettings Quiet = new(Quiet: true, ShutOutRealGamepads: false, Mute: false);

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
    public async Task AWindowedDriveBesideAScratchSessionIsNotRefusedAndWritesItsOwnSettings()
    {
        string alpha = _harness.Project("alpha");
        await StartScratchWithItsFileAsync(alpha);

        await _harness.StartWaitingAttachAsync(Drive(alpha, "drive", quiet: false, shutOutRealGamepads: false));

        string file = File.ReadAllText(OverrideFile.PathIn(alpha));
        Assert.DoesNotContain($"{OverrideFile.InitialPositionSetting}=", file, StringComparison.Ordinal);
        Assert.Contains($"{OverrideFile.IgnoreJoypadOnUnfocusedSetting}=false", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APadsShutDriveBesideAScratchSessionIsNotRefused()
    {
        string alpha = _harness.Project("alpha");
        await StartScratchWithItsFileAsync(alpha);

        await _harness.StartWaitingAttachAsync(Drive(alpha, "drive", quiet: true, shutOutRealGamepads: true));

        string file = File.ReadAllText(OverrideFile.PathIn(alpha));
        Assert.Contains($"{OverrideFile.IgnoreJoypadOnUnfocusedSetting}=true", file, StringComparison.Ordinal);
        Assert.Contains($"{OverrideFile.InitialPositionSetting}=", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScratchSessionKeepsQuietAndLivePadsBesideAWindowedDrive()
    {
        string alpha = _harness.Project("alpha");
        await _harness.StartWaitingAttachAsync(Drive(alpha, "drive", quiet: false, shutOutRealGamepads: true) with { Mute = true });

        GodotSession scratch = await ReserveScratchAsync(alpha);

        Assert.Equal((true, false, false), (scratch.Quiet, scratch.ShutOutRealGamepads, scratch.Mute));
    }

    [Fact]
    public async Task AScratchSessionDoesNotRewriteADrivesFile()
    {
        string alpha = _harness.Project("alpha");
        await _harness.StartWaitingAttachAsync(Drive(alpha, "drive", quiet: false, shutOutRealGamepads: false));
        GodotSession scratch = await ReserveScratchAsync(alpha);
        string before = File.ReadAllText(OverrideFile.PathIn(alpha));

        _harness.Sessions.WriteOverride(scratch, Installation.FindBridgeScript());

        Assert.Equal(before, File.ReadAllText(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task ADrivesStopKeepsTheFileWhileAScratchSessionIsLive()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        using Wire.FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "drive", new Wire.FakeHello());
        GodotSession scratch = await ReserveScratchAsync(alpha);
        _harness.Sessions.WriteOverride(scratch, Installation.FindBridgeScript());

        DetachResult detached = await _harness.Sessions.DetachAsync("drive", cancellation);

        Assert.False(detached.OverrideRemoved);
        Assert.True(OverrideFile.IsOurs(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task DisarmKeepsTheFileWhileAScratchSessionIsLive()
    {
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Quiet, TestContext.Current.CancellationToken);
        GodotSession scratch = await ReserveScratchAsync(alpha);
        _harness.Sessions.WriteOverride(scratch, Installation.FindBridgeScript());

        DisarmResult disarmed = _harness.Sessions.Disarm(alpha);

        Assert.False(disarmed.OverrideRemoved);
        Assert.True(OverrideFile.IsOurs(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task ArmingLoudBesideAScratchSessionWritesTheArmsSettings()
    {
        string alpha = _harness.Project("alpha");
        await StartScratchWithItsFileAsync(alpha);

        ArmState armed = await _harness.Sessions.ArmAsync(alpha, Loud, TestContext.Current.CancellationToken);

        Assert.False(armed.Quiet);
        Assert.DoesNotContain($"{OverrideFile.InitialPositionSetting}=", File.ReadAllText(OverrideFile.PathIn(alpha)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScratchSessionOnALoudArmedFolderIsNotRefused()
    {
        string alpha = _harness.Project("alpha");
        await _harness.Sessions.ArmAsync(alpha, Loud with { ShutOutRealGamepads = true }, TestContext.Current.CancellationToken);

        GodotSession scratch = await ReserveScratchAsync(alpha);

        Assert.Equal((true, false), (scratch.Quiet, scratch.ShutOutRealGamepads));
    }

    [Fact]
    public async Task APreviewTakesTheDrivesSettingsNotTheScratchSessions()
    {
        string alpha = _harness.Project("alpha");
        await ReserveScratchAsync(alpha);
        await _harness.StartWaitingAttachAsync(Drive(alpha, "drive", quiet: false, shutOutRealGamepads: true));

        GodotSession preview = _harness.Sessions.ReservePreview(alpha);

        Assert.Equal((false, true), (preview.Quiet, preview.ShutOutRealGamepads));
    }

    [Fact]
    public async Task AnotherServersOtherSettingsStillRefuseAScratchWrite()
    {
        string alpha = _harness.Project("alpha");
        OverrideOwner foreign = StartForeignOwner();
        string bridge = Path.GetFullPath(Installation.FindBridgeScript()).Replace('\\', '/');
        File.WriteAllText(
            OverrideFile.PathIn(alpha),
            $"{OverrideFile.Marker}\n{OverrideFile.OwnersPrefix}{foreign}\n[autoload]\n\n{OverrideFile.AutoloadName}=\"*{bridge}\"\n\n"
                + $"[input_devices]\n\n{OverrideFile.IgnoreJoypadOnUnfocusedSetting}=true\n"
        );
        GodotSession scratch = await ReserveScratchAsync(alpha);

        SessionException refused = Assert.Throws<SessionException>(() => _harness.Sessions.WriteOverride(scratch, Installation.FindBridgeScript()));

        Assert.Equal(
            $"{alpha}'s override.cfg is in use by another godot-mcp server (pid {foreign.ProcessId}) with quiet=false and "
                + "shutOutRealGamepads=true; start this session with the same values, or end that server's sessions on the folder first.",
            refused.Message
        );
    }

    [Fact]
    public async Task TwoDrivesStillAgreeBesideAScratchSession()
    {
        string alpha = _harness.Project("alpha");
        await _harness.StartWaitingAttachAsync(Drive(alpha, "drive", quiet: false, shutOutRealGamepads: false));
        await ReserveScratchAsync(alpha);
        LaunchRequest request = new(alpha, null, [], [], Quiet: true, ShutOutRealGamepads: false, Prepare: true);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.LaunchAsync(request, "other", TestContext.Current.CancellationToken)
        );

        Assert.Equal($"Sessions on {alpha} run with quiet=false; start this one with the same value, or stop them first.", refused.Message);
    }

    [Fact]
    public async Task ADriveWhoseSettingsMatchTheFileDoesNotRewriteIt()
    {
        string alpha = _harness.Project("alpha");
        await StartScratchWithItsFileAsync(alpha);
        string path = OverrideFile.PathIn(alpha);
        DateTime marked = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, marked);
        string before = File.ReadAllText(path);

        await _harness.StartWaitingAttachAsync(Drive(alpha, "drive", quiet: true, shutOutRealGamepads: false));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(marked, File.GetLastWriteTimeUtc(path));
    }

    private static AttachRequest Drive(string projectDir, string name, bool quiet, bool shutOutRealGamepads) =>
        new(projectDir, name, RegistryHarness.LongWait, shutOutRealGamepads, quiet, null);

    /// <summary>A pending scratch session of scene A on the folder, as run_scratches reserves one before its launch.</summary>
    private Task<GodotSession> ReserveScratchAsync(string projectDir) =>
        _harness.Sessions.ReserveScratchAsync(projectDir, "A", "res://A.tscn", "alpha", held: false);

    /// <summary>A pending scratch session that has written its override.cfg, as its launch does.</summary>
    private async Task<GodotSession> StartScratchWithItsFileAsync(string projectDir)
    {
        GodotSession scratch = await ReserveScratchAsync(projectDir);
        _harness.Sessions.WriteOverride(scratch, Installation.FindBridgeScript());
        return scratch;
    }

    /// <summary>A child process that runs until the test ends, as the owner another live server would be.</summary>
    private OverrideOwner StartForeignOwner()
    {
        _foreign = RegistryHarness.StartStandInGame();
        return new OverrideOwner(_foreign.Id, _foreign.StartTime.ToUniversalTime().Ticks);
    }
}
