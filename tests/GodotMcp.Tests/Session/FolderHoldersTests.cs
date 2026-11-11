using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The read-only view of who holds a folder's override.cfg (<see cref="FolderHolders.Inspect"/>). A dead owner is this process's
/// id with a start time it never had; a live foreign owner is one the probe says runs, so no process is started. No row changes
/// or deletes the file it reads.
/// </summary>
public sealed class FolderHoldersTests : IDisposable
{
    private const string Body = "[autoload]\n\nGodotMcpBridge=\"*D:/gone/bridge.gd\"\n";
    private const string ForeignVersion = "0.39.0-abc1234";

    private static readonly OverrideOwner Dead = new(Environment.ProcessId, OverrideOwner.Current.StartTicks - 1);
    private static readonly OverrideOwner Foreign = new(int.MaxValue - 7, OverrideOwner.Current.StartTicks - 2);
    private static readonly OwnerProbe Probe = new(owner => owner == Foreign, pid => pid == Foreign.ProcessId ? ForeignVersion : null);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AFolderWithoutAnOverrideHasNothingToRemove()
    {
        string project = Project();

        FolderHoldersView view = FolderHolders.Inspect(project, [], false, Probe);

        Assert.Equal(("absent", "absent"), (view.OverrideCfg, view.ArmedJson));
        Assert.Empty(view.Owners);
        Assert.False(view.StopProjectWouldRemove);
        Assert.Equal("nothing to remove: the folder holds neither override.cfg nor armed.json", view.Reason);
        Assert.False(File.Exists(OverrideFile.PathIn(project)));
    }

    [Fact]
    public void AnUnmarkedOverrideIsTheProjectsOwnAndKept()
    {
        string project = Project();
        string path = OverrideFile.PathIn(project);
        File.WriteAllText(path, "[application]\nconfig/name=\"Mine\"\n");
        byte[] before = File.ReadAllBytes(path);

        FolderHoldersView view = FolderHolders.Inspect(project, [], false, Probe);

        Assert.Equal("project's own", view.OverrideCfg);
        Assert.Empty(view.Owners);
        Assert.False(view.StopProjectWouldRemove);
        Assert.Equal("override.cfg is the project's own (no godot-mcp marker), so stop_project would keep it", view.Reason);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void AMarkedOverrideWhoseOwnersHaveExitedWouldBeRemoved()
    {
        string project = Project();
        string path = WriteMarked(project, $"{Dead}");
        byte[] before = File.ReadAllBytes(path);

        FolderHoldersView view = FolderHolders.Inspect(project, [], false, Probe);

        Assert.Equal("godot-mcp", view.OverrideCfg);
        Assert.Equal([DeadEntry()], view.Owners);
        Assert.True(view.StopProjectWouldRemove);
        Assert.Null(view.Reason);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ALiveForeignOwnerKeepsTheOverrideAndEveryOwnerIsListed()
    {
        string project = Project();
        string path = WriteMarked(project, $"{Foreign},{Dead}");
        byte[] before = File.ReadAllBytes(path);

        FolderHoldersView view = FolderHolders.Inspect(project, [], false, Probe);

        HolderEntry foreign = new(
            OverrideFile.FileName,
            Foreign.ProcessId,
            StartOf(Foreign),
            Alive: true,
            ThisServer: false,
            ForeignVersion,
            Readable: true
        );
        Assert.Equal([foreign, DeadEntry()], view.Owners);
        Assert.False(view.StopProjectWouldRemove);
        Assert.Equal($"override.cfg names a live owner (pid {Foreign.ProcessId}), so stop_project would keep it", view.Reason);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void AnOwnerItCannotReadKeepsTheOverride()
    {
        string project = Project();
        string path = WriteMarked(project, $"{Dead},12@ab@cd");
        byte[] before = File.ReadAllBytes(path);

        FolderHoldersView view = FolderHolders.Inspect(project, [], false, Probe);

        HolderEntry unreadable = new(OverrideFile.FileName, null, null, Alive: true, ThisServer: false, null, Readable: false) { Entry = "12@ab@cd" };
        Assert.Equal([DeadEntry(), unreadable], view.Owners);
        Assert.False(view.StopProjectWouldRemove);
        Assert.Equal("override.cfg names an owner this server cannot read (12@ab@cd), so stop_project would keep it", view.Reason);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ThisServerAmongTheOwnersIsMarkedAsThisServer()
    {
        string project = Project();
        string path = WriteMarked(project, $"{OverrideOwner.Current}");
        byte[] before = File.ReadAllBytes(path);

        FolderHoldersView view = FolderHolders.Inspect(project, [], false, Probe);

        HolderEntry here = new(
            OverrideFile.FileName,
            Environment.ProcessId,
            StartOf(OverrideOwner.Current),
            Alive: true,
            ThisServer: true,
            null,
            Readable: true
        );
        Assert.Equal([here], view.Owners);
        Assert.False(view.StopProjectWouldRemove);
        Assert.Equal("override.cfg belongs to this server's own session or arm, so stop_project would keep it", view.Reason);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ALiveForeignOwnerKeepsTheArmFileAndEveryOwnerIsListed()
    {
        string project = Project();
        string path = WriteArm(project, $"\"{Foreign}\",\"{Dead}\"");
        byte[] before = File.ReadAllBytes(path);

        FolderHoldersView view = FolderHolders.Inspect(project, [], false, Probe);

        Assert.Equal(("absent", "godot-mcp"), (view.OverrideCfg, view.ArmedJson));
        HolderEntry foreign = new(
            ArmFile.FileName,
            Foreign.ProcessId,
            StartOf(Foreign),
            Alive: true,
            ThisServer: false,
            ForeignVersion,
            Readable: true
        );
        HolderEntry dead = new(ArmFile.FileName, Dead.ProcessId, StartOf(Dead), Alive: false, ThisServer: false, null, Readable: true);
        Assert.Equal([foreign, dead], view.Owners);
        Assert.False(view.StopProjectWouldRemove);
        Assert.Equal($"armed.json names a live owner (pid {Foreign.ProcessId}), so stop_project would keep it", view.Reason);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void OneLiveSessionOnTheFolderIsWhatStopProjectWouldStop()
    {
        string project = Project();
        WriteMarked(project, $"{Dead}");

        FolderHoldersView view = FolderHolders.Inspect(project, ["game"], false, Probe);

        Assert.Equal(["game"], view.Sessions);
        Assert.False(view.StopProjectWouldRemove);
        Assert.Equal(
            "stop_project would stop this server's session game on the folder, which releases its override.cfg, instead of clearing leftovers",
            view.Reason
        );
    }

    [Fact]
    public void SeveralLiveSessionsOnTheFolderMakeStopProjectRefuse()
    {
        string project = Project();

        FolderHoldersView view = FolderHolders.Inspect(project, ["alpha", "beta"], false, Probe);

        Assert.False(view.StopProjectWouldRemove);
        Assert.Equal("stop_project would refuse: several of this server's sessions run on the folder (alpha, beta); stop one by name", view.Reason);
    }

    [Fact]
    public void AnOverrideAnotherHandleHoldsExclusivelyIsInUse()
    {
        string project = Project();
        string path = WriteMarked(project, $"{Dead}");

        FolderHoldersView view;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            view = FolderHolders.Inspect(project, [], false, Probe);
        }

        Assert.Equal("in use", view.OverrideCfg);
        Assert.Empty(view.Owners);
        Assert.False(view.StopProjectWouldRemove);
        Assert.Equal("override.cfg is open in another process (a running Godot?), so stop_project would keep it", view.Reason);
    }

    [Fact]
    public void AnEmptyOverrideIsInUseAsWhileItIsWritten()
    {
        string project = Project();
        string path = OverrideFile.PathIn(project);
        File.WriteAllText(path, "");

        FolderHoldersView view = FolderHolders.Inspect(project, [], false, Probe);

        Assert.Equal("in use", view.OverrideCfg);
        Assert.False(view.StopProjectWouldRemove);
        Assert.Equal("override.cfg is empty, as while godot-mcp writes it, so stop_project would keep it", view.Reason);
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public void AnOverrideThatCannotBeReadIsUnreadableAndTheViewDoesNotThrow()
    {
        string project = Project();
        Directory.CreateDirectory(OverrideFile.PathIn(project));

        FolderHoldersView view = FolderHolders.Inspect(project, [], false, Probe);

        Assert.Equal("unreadable", view.OverrideCfg);
        Assert.Empty(view.Owners);
        Assert.False(view.StopProjectWouldRemove);
        Assert.StartsWith("override.cfg could not be read (", view.Reason, StringComparison.Ordinal);
        Assert.EndsWith("), so stop_project would keep it", view.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleOverrideBesideAKeptArmFileWouldStillBeRemoved()
    {
        string project = Project();
        WriteMarked(project, $"{Dead}");
        WriteArm(project, $"\"{Foreign}\"");

        FolderHoldersView view = FolderHolders.Inspect(project, [], false, Probe);

        Assert.True(view.StopProjectWouldRemove);
        Assert.Equal($"armed.json names a live owner (pid {Foreign.ProcessId}), so stop_project would keep it", view.Reason);
    }

    [Fact]
    public void BothFilesKeptJoinTheirReasons()
    {
        string project = Project();
        WriteMarked(project, $"{Foreign}");
        WriteArm(project, "\"x\"");

        FolderHoldersView view = FolderHolders.Inspect(project, [], false, Probe);

        Assert.False(view.StopProjectWouldRemove);
        Assert.Equal(
            $"override.cfg names a live owner (pid {Foreign.ProcessId}), so stop_project would keep it; "
                + "armed.json names an owner this server cannot read (x), so stop_project would keep it",
            view.Reason
        );
    }

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\godot-mcp\versions\0.39.0-abc1234\GodotMcp.Server.exe", "0.39.0-abc1234")]
    [InlineData(@"X:\dev\godot-mcp\src\GodotMcp.Server\bin\Release\GodotMcp.Server.exe", null)]
    [InlineData(null, null)]
    public void TheServerVersionIsTheVersionFolderTheExecutableRunsFrom(string? executable, string? version) =>
        Assert.Equal(version, FolderHolders.VersionFolderOf(executable));

    private static HolderEntry DeadEntry() =>
        new(OverrideFile.FileName, Dead.ProcessId, StartOf(Dead), Alive: false, ThisServer: false, null, Readable: true);

    private static DateTimeOffset StartOf(OverrideOwner owner) => new(owner.StartTicks, TimeSpan.Zero);

    private string Project()
    {
        string project = _temp.Combine("alpha");
        Directory.CreateDirectory(project);
        return project;
    }

    /// <summary>Writes an armed.json whose owners array holds <paramref name="owners"/>, already JSON; returns its path.</summary>
    private static string WriteArm(string project, string owners)
    {
        string path = ArmFile.PathIn(project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{{\"quiet\":false,\"shutOutRealGamepads\":false,\"mute\":false,\"owners\":[{owners}]}}\n");
        return path;
    }

    /// <summary>Writes a marked override.cfg whose owners line names <paramref name="owners"/>; returns its path.</summary>
    private static string WriteMarked(string project, string owners)
    {
        string path = OverrideFile.PathIn(project);
        File.WriteAllText(path, $"{OverrideFile.Marker}\n{OverrideFile.OwnersPrefix}{owners}\n{Body}");
        return path;
    }
}
