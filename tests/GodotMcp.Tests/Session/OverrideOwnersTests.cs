using System.Diagnostics;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The owners line of the marked override.cfg and the machine-wide list of folders swept at startup. A live foreign owner is
/// a real child process; a dead one is this process's id with a start time it never had.
/// </summary>
public sealed class OverrideOwnersTests : IDisposable
{
    private const string Body = "[autoload]\n\nGodotMcpBridge=\"*D:/tools/bridge.gd\"\n";
    private const string UserOverride = "[application]\nconfig/name=\"Mine\"\n";

    private static readonly OverrideOwner Dead = new(Environment.ProcessId, OverrideOwner.Current.StartTicks - 1);

    private readonly TempDirectory _temp = new();
    private readonly StringWriter _errors = new();
    private Process? _child;

    public void Dispose()
    {
        if (_child is not null)
        {
            _child.Kill(entireProcessTree: true);
            _child.WaitForExit(10_000);
            _child.Dispose();
        }

        _errors.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void WriteListsThisServerAsTheOwner()
    {
        string project = Project("alpha");

        OverrideFile.Write(project, _temp.Combine("bridge.gd"), false, false);

        string[] lines = File.ReadAllLines(OverrideFile.PathIn(project));
        Assert.Equal(OverrideFile.Marker, lines[0]);
        Assert.Equal($"{OverrideFile.OwnersPrefix}{OverrideOwner.Current}", lines[1]);
        Assert.Equal("[autoload]", lines[2]);
    }

    [Fact]
    public void WriteKeepsALiveForeignOwnerAndDropsADeadOne()
    {
        string project = Project("alpha");
        OverrideOwner foreign = StartForeignOwner();
        WriteMarked(project, $"{OverrideFile.OwnersPrefix}{foreign},{Dead}\n{Body}");

        OverrideFile.Write(project, _temp.Combine("bridge.gd"), false, false);

        Assert.Equal([foreign, OverrideOwner.Current], OverrideFile.LiveOwners(project));
        Assert.Equal($"{OverrideFile.OwnersPrefix}{foreign},{OverrideOwner.Current}", File.ReadAllLines(OverrideFile.PathIn(project))[1]);
    }

    [Fact]
    public void ReleaseWithALiveForeignOwnerRewritesTheFileWithoutThisServer()
    {
        string project = Project("alpha");
        OverrideOwner foreign = StartForeignOwner();
        WriteMarked(project, $"{OverrideFile.OwnersPrefix}{OverrideOwner.Current},{foreign}\n{Body}");

        Assert.False(OverrideFile.Release(project));

        Assert.Equal($"{OverrideFile.Marker}\n{OverrideFile.OwnersPrefix}{foreign}\n{Body}", File.ReadAllText(OverrideFile.PathIn(project)));
    }

    [Fact]
    public void ReleaseAsTheLastLiveOwnerDeletesTheFile()
    {
        string project = Project("alpha");
        WriteMarked(project, $"{OverrideFile.OwnersPrefix}{OverrideOwner.Current},{Dead}\n{Body}");

        Assert.True(OverrideFile.Release(project));

        Assert.False(File.Exists(OverrideFile.PathIn(project)));
    }

    [Fact]
    public void AMarkedFileWithoutAnOwnersLineIsStale()
    {
        string project = Project("alpha");
        WriteMarked(project, Body);

        Assert.Empty(OverrideFile.LiveOwners(project));
        Assert.True(OverrideFile.Release(project));
        Assert.False(File.Exists(OverrideFile.PathIn(project)));
    }

    [Fact]
    public void SweepRemovesStaleFilesKeepsLiveOnesAndPrunesTheList()
    {
        string stale = Project("stale");
        string ownerless = Project("ownerless");
        string live = Project("live");
        string unmarked = Project("unmarked");
        string empty = Project("empty");
        OverrideOwner foreign = StartForeignOwner();
        WriteMarked(stale, $"{OverrideFile.OwnersPrefix}{Dead}\n{Body}");
        WriteMarked(ownerless, Body);
        WriteMarked(live, $"{OverrideFile.OwnersPrefix}{foreign}\n{Body}");
        File.WriteAllText(OverrideFile.PathIn(unmarked), UserOverride);
        string list = WriteList(stale, ownerless, live, unmarked, empty);

        new OverrideFolders(list, _errors).Sweep();

        Assert.False(File.Exists(OverrideFile.PathIn(stale)));
        Assert.False(File.Exists(OverrideFile.PathIn(ownerless)));
        Assert.True(OverrideFile.IsOurs(OverrideFile.PathIn(live)));
        Assert.Equal(UserOverride, File.ReadAllText(OverrideFile.PathIn(unmarked)));
        Assert.Equal([live], File.ReadAllLines(list));
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void SweepWithoutAListDoesNothing()
    {
        string list = _temp.Combine("cache", "override-folders.txt");

        new OverrideFolders(list, _errors).Sweep();

        Assert.False(File.Exists(list));
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void SweepDropsAFolderThatIsGone()
    {
        string gone = _temp.Combine("gone");
        string list = WriteList(gone);

        new OverrideFolders(list, _errors).Sweep();

        Assert.Empty(File.ReadAllLines(list));
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void RecordWhileListsTheFolderOnceAndRunsTheWrite()
    {
        string project = Project("alpha");
        string list = _temp.Combine("cache", "override-folders.txt");
        OverrideFolders folders = new(list, _errors);
        int writes = 0;

        folders.RecordWhile(project, () => writes++);
        folders.RecordWhile(project + Path.DirectorySeparatorChar, () => writes++);

        Assert.Equal(2, writes);
        Assert.Equal([project], File.ReadAllLines(list));
        Assert.Empty(_errors.ToString());
    }

    private string Project(string name)
    {
        string project = _temp.Combine(name);
        Directory.CreateDirectory(project);
        return project;
    }

    private static void WriteMarked(string project, string afterMarker) =>
        File.WriteAllText(OverrideFile.PathIn(project), $"{OverrideFile.Marker}\n{afterMarker}");

    private string WriteList(params string[] folders)
    {
        string list = _temp.Combine("override-folders.txt");
        File.WriteAllText(list, string.Concat(folders.Select(folder => folder + "\n")));
        return list;
    }

    /// <summary>A child process that runs until the test ends, as the owner another live server would be.</summary>
    private OverrideOwner StartForeignOwner()
    {
        _child = Process.Start(
            new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            }
        )!;
        return new OverrideOwner(_child.Id, _child.StartTime.ToUniversalTime().Ticks);
    }
}
