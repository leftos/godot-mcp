using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A real listener, its registry and the fake games dialled into it. A session that exists and is live is an attach still
/// waiting for its game, which the harness cancels on dispose.
/// </summary>
internal sealed class RegistryHarness : IAsyncDisposable
{
    public static readonly TimeSpan LongWait = TimeSpan.FromSeconds(60);

    private readonly TempDirectory _temp = new();
    private readonly List<(Task Attach, CancellationTokenSource Cancel)> _waiting = [];

    public RegistryHarness()
    {
        Listener = new BridgeListener(NullLogger<BridgeListener>.Instance);
        Sessions = new SessionRegistry(Listener, NullLogger<GodotSession>.Instance)
        {
            OverrideFolders = new OverrideFolders(_temp.Combine("override-folders.txt"), TextWriter.Null),
            Dormant = new DormantGames(_ => new ProcessStart(Exists: true, StartTime: null), TextWriter.Null),
        };
    }

    public BridgeListener Listener { get; }

    public SessionRegistry Sessions { get; }

    /// <summary>The pending attaches, cancelled and awaited on dispose.</summary>
    public List<(Task Attach, CancellationTokenSource Cancel)> Waiting => _waiting;

    public string Path => _temp.Path;

    public string Combine(params string[] parts) => _temp.Combine(parts);

    public async ValueTask DisposeAsync()
    {
        foreach ((Task attach, CancellationTokenSource cancel) in _waiting)
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attach);
            cancel.Dispose();
        }

        Sessions.Dispose();
        Listener.Dispose();
        _temp.Dispose();
    }

    /// <summary>A project folder with a project.godot.</summary>
    public string Project(string folder)
    {
        string projectDir = _temp.Combine(folder);
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(System.IO.Path.Combine(projectDir, "project.godot"), "config_version=5\n");
        return projectDir;
    }

    /// <summary>Starts an attach that stays waiting for its game, cancelled with the harness.</summary>
    public async Task StartWaitingAttachAsync(string projectDir, string name)
    {
        CancellationTokenSource cancel = new();
        Task attach = Sessions.AttachAsync(new AttachRequest(projectDir, name, LongWait, false, false, null), cancel.Token);
        _waiting.Add((attach, cancel));
        await WaitUntilAsync(() => Sessions.List(includeStopped: true).Any(session => session.Name == name));
    }

    /// <summary>Attaches a session to a fake game whose hello carries <paramref name="processId"/> when it is not null.</summary>
    public async Task<FakeBridge> AttachFakeGameAsync(string projectDir, string name, int? processId)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string attachFile = AttachFile.PathIn(projectDir);
        Task<AttachResult> attach = Sessions.AttachAsync(new AttachRequest(projectDir, name, LongWait, false, false, null), cancellation);
        await WaitUntilAsync(() => File.Exists(attachFile));
        string token = JsonNode.Parse(File.ReadAllText(attachFile))!["token"]!.GetValue<string>();
        FakeBridge game = await FakeBridge.DialAsync(Listener.Port, token, projectDir, processId, cancellation);
        await attach;
        return game;
    }

    /// <summary>
    /// Writes a dormant game's entry on the folder, as its bridge would; the harness's process boundary counts every pid as a
    /// live process whose start time cannot be read.
    /// </summary>
    public static void WriteDormant(string projectDir, int pid)
    {
        string folder = DormantGames.FolderIn(projectDir);
        Directory.CreateDirectory(folder);
        long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(System.IO.Path.Combine(folder, $"{pid}.json"), $"{{\"pid\":{pid},\"startedUnixMs\":{started}}}");
    }

    /// <summary>
    /// Starts an attach asking for <paramref name="pid"/> (null for none), waits for the join file of
    /// <paramref name="joinedPid"/>, and dials in as that game with the token it carries.
    /// </summary>
    public async Task<(AttachResult Result, FakeBridge Game)> JoinFakeGameAsync(string projectDir, string name, int? pid, int joinedPid)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string joinFile = DormantGames.JoinPathIn(projectDir, joinedPid);
        Task<AttachResult> attach = Sessions.AttachAsync(new AttachRequest(projectDir, name, LongWait, false, false, pid), cancellation);
        await WaitUntilAsync(() => File.Exists(joinFile) || attach.IsCompleted);
        if (attach.IsCompleted)
        {
            await attach;
            Assert.Fail("the attach ended before it wrote the join file");
        }

        string token = JsonNode.Parse(File.ReadAllText(joinFile))!["token"]!.GetValue<string>();
        FakeBridge game = await FakeBridge.DialAsync(Listener.Port, token, projectDir, joinedPid, cancellation);
        return (await attach, game);
    }

    /// <summary>Attaches a session to a fake game, then ends the game, leaving the session stopped.</summary>
    public async Task EndAttachedGameAsync(string projectDir, string name)
    {
        FakeBridge game = await AttachFakeGameAsync(projectDir, name, null);
        game.Dispose();
        await WaitUntilAsync(() => !Sessions.Resolve(name).IsLive);
    }

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition did not hold within 10 s");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
