using System.Text.Json;
using GodotMcp.Server.Session;
using ModelContextProtocol;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A run's project settings beside the folder's other sessions and its arm: the folder's one override.cfg carries them, so every
/// session on it must carry the same block, an attach and an arm carry none, and a scratch session is exempt.
/// </summary>
public sealed class SessionProjectSettingsTests : IAsyncDisposable
{
    /// <summary>A guard against a group that never ends; a passing run never comes near it.</summary>
    private const int HangGuardMs = 120_000;

    private static readonly ArmSettings Loud = new(Quiet: false, ShutOutRealGamepads: false, Mute: false);
    private static readonly string Settings = Block("Seen");
    private static readonly string Other = Block("Other");

    private readonly RegistryHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact(Timeout = HangGuardMs)]
    public async Task ASecondRunWithOtherProjectSettingsIsRefused()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        await using Parking parking = new(_harness.Sessions, alpha);
        await parking.StartAsync(Request(alpha, Settings), "server", TestContext.Current.CancellationToken);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.LaunchAsync(Request(alpha, Other), "other", TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"Sessions on {alpha} run with projectSettings={ProjectSettingsText.Summary(Settings)}; start this one with the same "
                + "value, or stop them first.",
            refused.Message
        );
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task ASecondRunWithTheSameProjectSettingsIsAccepted()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        await using Parking parking = new(_harness.Sessions, alpha);

        await parking.StartAsync(Request(alpha, Settings), "server", TestContext.Current.CancellationToken);
        await parking.StartAsync(Request(alpha, Settings), "other", TestContext.Current.CancellationToken);

        Assert.Equal(["other", "server"], _harness.Sessions.List(includeStopped: false).Select(session => session.Name));
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task AnAttachBesideARunWithProjectSettingsIsRefused()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        await using Parking parking = new(_harness.Sessions, alpha);
        await parking.StartAsync(Request(alpha, Settings), "server", TestContext.Current.CancellationToken);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(
                new AttachRequest(alpha, "client", RegistryHarness.LongWait, false, false, null),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(
            $"Sessions on {alpha} run with projectSettings={ProjectSettingsText.Summary(Settings)}; attach_project takes no "
                + "projectSettings, so stop them first.",
            refused.Message
        );
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task ARunWithProjectSettingsOnAnArmedFolderIsRefused()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        await _harness.Sessions.ArmAsync(alpha, Loud, TestContext.Current.CancellationToken);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.LaunchAsync(Request(alpha, Settings), "server", TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"{alpha} is armed, and arm_project takes no projectSettings; disarm_project first to run with "
                + $"projectSettings={ProjectSettingsText.Summary(Settings)}.",
            refused.Message
        );
        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task AScratchSessionBesideARunWithProjectSettingsIsNotRefused()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        await using Parking parking = new(_harness.Sessions, alpha);
        await parking.StartAsync(Request(alpha, Settings), "server", TestContext.Current.CancellationToken);

        GodotSession scratch = await _harness.Sessions.ReserveScratchAsync(alpha, "A", "res://A.tscn", "alpha", held: false);

        Assert.True(scratch.IsLive);
        Assert.Equal("", scratch.ProjectSettings);
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task APreviewBesideASettingsRunCarriesItsProjectSettings()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        await using Parking parking = new(_harness.Sessions, alpha);
        await parking.StartAsync(Request(alpha, Settings), "server", TestContext.Current.CancellationToken);

        GodotSession preview = _harness.Sessions.ReservePreview(alpha);

        Assert.Equal(Settings, preview.ProjectSettings);
    }

    /// <summary>A launch request for a run on <paramref name="projectDir"/> that carries <paramref name="settings"/>.</summary>
    private static LaunchRequest Request(string projectDir, string settings) =>
        new(projectDir, null, [], [], Quiet: false, ShutOutRealGamepads: false, Prepare: true) { ProjectSettings = settings };

    /// <summary>The block one setting renders to, as a run's request carries it.</summary>
    private static string Block(string value) =>
        ProjectSettingsText.Block(
            [new KeyValuePair<string, JsonElement>("gmcp/value", JsonSerializer.SerializeToElement(value))],
            "options.projectSettings",
            problem => new McpException(problem)
        );

    /// <summary>
    /// Runs parked on the folder's prep lock, which the test holds, so each stays starting with no Godot installed: live, with
    /// its request's project settings. Disposing cancels them and lets the lock go.
    /// </summary>
    private sealed class Parking(SessionRegistry sessions, string projectDir) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _folderLock = sessions.PrepLock(projectDir);
        private readonly List<(Task Launch, CancellationTokenSource Cancel)> _runs = [];

        /// <summary>Starts the run and waits for it to be listed, leaving it starting.</summary>
        public async Task StartAsync(LaunchRequest request, string name, CancellationToken cancellation)
        {
            if (_runs.Count == 0)
            {
                await _folderLock.WaitAsync(cancellation);
            }

            CancellationTokenSource cancel = new();
            Task<LaunchResult> launch = sessions.LaunchAsync(request, name, cancel.Token);
            _runs.Add((launch, cancel));
            await RegistryHarness.WaitUntilAsync(() => sessions.List(includeStopped: true).Any(session => session.Name == name), launch);
        }

        public async ValueTask DisposeAsync()
        {
            foreach ((Task launch, CancellationTokenSource cancel) in _runs)
            {
                await cancel.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launch);
                cancel.Dispose();
            }

            if (_runs.Count > 0)
            {
                _folderLock.Release();
            }
        }
    }
}
