using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>
/// preview_scene's 60 s on a registry whose requests run on wall time and whose launches run load-adjusted, as the itest
/// harness sets it: the preview covers a launch, so it is timed on the launch's clock. No Godot runs: the preview is parked on
/// its folder's prep lock, which the test holds.
/// </summary>
public sealed class PreviewLimitTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly LoadClock _wallClock;
    private readonly FakeLoadSource _machine;
    private readonly LoadClock _launchClock;
    private readonly TempDirectory _temp = new();
    private readonly BridgeListener _listener;
    private readonly SessionRegistry _sessions;

    public PreviewLimitTests()
    {
        _wallClock = new LoadClock(_time, new NoLoadSource());
        _machine = new FakeLoadSource(_time, processors: 4);
        _launchClock = new LoadClock(_time, _machine);
        _listener = new BridgeListener(NullLogger<BridgeListener>.Instance) { Clock = _wallClock };
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance) { LaunchClock = _launchClock };
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _listener.Dispose();
        _launchClock.Dispose();
        _wallClock.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task APreviewOnABusyMachineRunsToItsBackstopNotToSixtySecondsOfWallTime()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // Other work keeps 90% of four processors busy: the launch clock runs at a tenth of wall time.
        _machine.SetLoad(busyShare: 0.9, ownCores: 0);
        string project = _temp.Combine("game");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "project.godot"), "config_version=5\n");
        SemaphoreSlim folderLock = _sessions.PrepLock(project);
        await folderLock.WaitAsync(cancellation);
        LaunchRequest request = new(project, "res://main.tscn", [], [], Quiet: true, ShutOutRealGamepads: false, Prepare: true) { Preview = true };
        long started = _time.GetTimestamp();
        try
        {
            Task<int> preview = _sessions.PreviewAsync(request, (_, _, _) => Task.FromResult(0), cancellation);
            await AdvanceSecondsUntilDoneAsync(preview, cancellation);

            SessionException refused = await Assert.ThrowsAsync<SessionException>(() => preview);
            TimeSpan wall = _time.GetElapsedTime(started);
            Assert.True(wall >= TimeSpan.FromSeconds(299), $"the preview was stopped after {wall} of wall time: {refused.Message}");
            Assert.Contains("that is 5 x its 60 s ceiling in wall time, the backstop", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            folderLock.Release();
        }
    }

    /// <summary>Advances the fake time a second at a time, letting real time pass after each step, until the task completes.</summary>
    private async Task AdvanceSecondsUntilDoneAsync(Task task, CancellationToken cancellation)
    {
        DateTime guard = DateTime.UtcNow + RegistryHarness.HangGuard;
        while (!task.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < guard, $"the preview did not end within its {RegistryHarness.HangGuard} hang guard");
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellation);
        }
    }
}
