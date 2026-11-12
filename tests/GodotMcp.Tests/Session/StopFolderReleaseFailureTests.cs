using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A stop or detach whose release of override.cfg fails: the test holds the file open without delete sharing, so its delete
/// throws, as a peer reading it does. The call still ends the session and reports the failure as a warning.
/// </summary>
public sealed class StopFolderReleaseFailureTests : IAsyncDisposable
{
    private readonly RegistryHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    /// <summary>The start of the warning a failed release of the folder's override.cfg adds.</summary>
    internal static string CouldNotBeRemoved(string projectDir) => $"override.cfg in {projectDir} could not be removed (IOException: ";

    /// <summary>The folder's override.cfg held open for reading, shared for read and write but not delete, so its delete fails.</summary>
    internal static FileStream HoldOverrideOpen(string projectDir) =>
        new(OverrideFile.PathIn(projectDir), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    [Fact]
    public async Task AnAttachedStopWhoseReleaseFailsStillEndsTheSession()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        await _harness.EndAttachedGameAsync(alpha, "server");

        StopResult stopped;
        using (HoldOverrideOpen(alpha))
        {
            stopped = await _harness.Sessions.StopAsync("server", TestContext.Current.CancellationToken);
        }

        Assert.False(stopped.OverrideRemoved);
        Assert.StartsWith(CouldNotBeRemoved(alpha), stopped.Warning, StringComparison.Ordinal);
        Assert.EndsWith(
            "this server's owner entry stays on it until stop_project {projectPath} on the folder or the server's exit.",
            stopped.Warning,
            StringComparison.Ordinal
        );
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }

    [Fact]
    public async Task ADetachWhoseReleaseFailsReportsTheWarning()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello());

        DetachResult detached;
        using (HoldOverrideOpen(alpha))
        {
            detached = await _harness.Sessions.DetachAsync("server", TestContext.Current.CancellationToken);
        }

        Assert.False(detached.OverrideRemoved);
        Assert.StartsWith(CouldNotBeRemoved(alpha), detached.Warning, StringComparison.Ordinal);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }

    // The stop forgets the attached session before its release fails, so only the registry's memory of the folder brings the
    // shutdown to it.
    [Fact]
    public async Task AFailedReleaseOnAForgottenAttachIsReleasedAtShutdown()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string path = OverrideFile.PathIn(alpha);
        await StopWithTheReleaseFailingAsync(alpha, "server");
        Assert.True(File.Exists(path));

        _harness.Sessions.Shutdown();

        Assert.False(File.Exists(path));
    }

    // A cancelled attach abandons its start: the session is forgotten first, then its release fails.
    [Fact]
    public async Task AFailedAbandonedStartLeavesTheFolderForShutdownToRelease()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string path = OverrideFile.PathIn(alpha);
        await _harness.StartWaitingAttachAsync(alpha, "server");
        // The attach file is written after the override, so the override is complete once it is there.
        await RegistryHarness.WaitUntilAsync(() => File.Exists(AttachFile.PathIn(alpha)), _harness.Waiting[0].Attach);
        (Task attach, CancellationTokenSource cancel) = _harness.Waiting[0];

        using (HoldOverrideOpen(alpha))
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attach);
        }

        Assert.Empty(_harness.Sessions.List(includeStopped: true));
        Assert.True(File.Exists(path));

        _harness.Sessions.Shutdown();

        Assert.False(File.Exists(path));
    }

    // The second session's file names this server rightly, so stop_project with the folder keeps it. A background scratch run's
    // session is one that call leaves alone, so the call takes the path that clears a folder no session runs in.
    [Fact]
    public async Task AFailedReleaseThenAnotherSessionOnTheFolderKeepsTheFile()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string path = OverrideFile.PathIn(alpha);
        await StopWithTheReleaseFailingAsync(alpha, "first");
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "second", new FakeHello());
        GodotSession second = _harness.Sessions.Resolve("second");

        // The second session's write dropped the folder from those owed a release; put back, as a release failing while the
        // second session registered would leave it, so the retry itself meets the live session.
        _harness.Sessions.MarkReleaseOwedForTest(alpha);
        _harness.Sessions.ScratchRuns.Hold(second);
        try
        {
            SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _harness.Sessions.StopFolderAsync(alpha, null, cancellation));
            Assert.EndsWith("Override.cfg belongs to this server's own session or arm, left in place.", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            _harness.Sessions.ScratchRuns.Release(second);
        }

        Assert.True(File.Exists(path));
        Assert.True(second.IsLive);
    }

    [Fact]
    public async Task AFolderThisServerOwesAReleaseOnIsReportedRemovableByListSessions()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        await StopWithTheReleaseFailingAsync(alpha, "server");

        FolderHoldersView view = _harness.Sessions.Holders(alpha);

        Assert.True(view.StopProjectWouldRemove);
        Assert.Equal("override.cfg still names this server, whose earlier release of it failed, so stop_project would remove it", view.Reason);
    }

    // The owners list stays busy past the retry's budget, so the retry goes ahead without it and removes the file; the strict
    // clear of leftovers that would follow could only report the list as busy, so it does not run.
    [Fact]
    public async Task ARetryPastABusyOwnersListReportsTheRemovalAlone()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        string alpha = ProjectPaths.Normalise(harness.Project("alpha"));
        await StopWithTheReleaseFailingAsync(harness, alpha, "server");
        bool releaseRefused = false;
        harness.Sessions.OverrideFolders.OpenRefused += purpose =>
            Volatile.Write(ref releaseRefused, releaseRefused || purpose.StartsWith("releasing ", StringComparison.Ordinal));

        HostStopResult stopped;
        using (new FileStream(harness.Sessions.OverrideFolders.ListPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            // Off the test's thread: the list's retries block the caller until fake time passes.
            Task<object> stop = Task.Run(() => harness.Sessions.StopFolderAsync(alpha, null, cancellation), cancellation);
            await RegistryHarness.AdvanceUntilDoneAsync(time, stop, cancellation);
            stopped = Assert.IsType<HostStopResult>(await stop);
        }

        Assert.True(Volatile.Read(ref releaseRefused));
        Assert.True(stopped.OverrideRemoved);
        Assert.Null(stopped.Warning);
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    // A live peer co-owns the file this server's release failed on: the retry takes this server's entry off and leaves the file.
    [Fact]
    public async Task ARetryOnAFileALivePeerCoOwnsTakesOffThisServersEntryAndSaysSo()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string path = OverrideFile.PathIn(alpha);
        await StopWithTheReleaseFailingAsync(alpha, "server");
        System.Diagnostics.Process foreign = _harness.StartOwnedGame();
        var peer = new OverrideOwner(foreign.Id, foreign.StartTime.ToUniversalTime().Ticks);
        File.WriteAllText(
            path,
            $"{OverrideFile.Marker}\n{OverrideFile.OwnersPrefix}{OverrideOwner.Current},{peer}\n[autoload]\n\nGodotMcpBridge=\"*D:/gone/bridge.gd\"\n"
        );

        var stopped = (HostStopResult)await _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken);

        Assert.False(stopped.OverrideRemoved);
        Assert.Equal(
            $"this server's owner entry was taken off override.cfg, which stays for the live owner it names (pid {foreign.Id})",
            stopped.Warning
        );
        Assert.Equal([peer], OverrideFile.LiveOwners(alpha));
    }

    /// <summary>Attaches a session whose game then ends, and stops it while its override.cfg is held open, so its release fails.</summary>
    private Task StopWithTheReleaseFailingAsync(string projectDir, string name) => StopWithTheReleaseFailingAsync(_harness, projectDir, name);

    /// <inheritdoc cref="StopWithTheReleaseFailingAsync(string, string)"/>
    private static async Task StopWithTheReleaseFailingAsync(RegistryHarness harness, string projectDir, string name)
    {
        await harness.EndAttachedGameAsync(projectDir, name);
        using (HoldOverrideOpen(projectDir))
        {
            StopResult stopped = await harness.Sessions.StopAsync(name, TestContext.Current.CancellationToken);
            Assert.False(stopped.OverrideRemoved);
        }
    }
}
