using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

/// <summary>The dormant games an armed folder lists, against a fake process boundary, and the join file that wakes one.</summary>
public sealed class DormantGamesTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _project = new();
    private readonly StringWriter _errors = new();
    private readonly Dictionary<int, ProcessStart> _processes = [];

    public void Dispose()
    {
        _errors.Dispose();
        _project.Dispose();
    }

    [Fact]
    public void AFolderWithNoDormantEntriesListsNone()
    {
        Assert.Empty(Games().List(_project.Path));
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void LiveGamesAreListedByPid()
    {
        WriteEntry(4102, Started);
        WriteEntry(4101, Started);
        _processes[4101] = new ProcessStart(true, Started - TimeSpan.FromSeconds(1));
        _processes[4102] = new ProcessStart(true, Started);

        IReadOnlyList<DormantGame> listed = Games().List(_project.Path);

        Assert.Equal([new DormantGame(4101, Started), new DormantGame(4102, Started)], listed);
    }

    [Fact]
    public void ADeadPidIsLeftOutAndItsEntryDeleted()
    {
        WriteEntry(4101, Started);
        _processes[4101] = new ProcessStart(false, null);

        Assert.Empty(Games().List(_project.Path));
        Assert.False(File.Exists(EntryPath(4101)));
    }

    [Fact]
    public void AReusedPidIsLeftOutAndItsEntryDeleted()
    {
        WriteEntry(4101, Started);
        _processes[4101] = new ProcessStart(true, Started + DormantGames.StartTolerance + TimeSpan.FromMilliseconds(1));

        Assert.Empty(Games().List(_project.Path));
        Assert.False(File.Exists(EntryPath(4101)));
    }

    [Fact]
    public void AProcessStartedWithinTheToleranceIsTheGame()
    {
        WriteEntry(4101, Started);
        _processes[4101] = new ProcessStart(true, Started + DormantGames.StartTolerance);

        Assert.Equal(4101, Assert.Single(Games().List(_project.Path)).Pid);
    }

    [Fact]
    public void AnUnreadableStartTimeCountsAsAlive()
    {
        WriteEntry(4101, Started);
        _processes[4101] = new ProcessStart(true, null);

        Assert.Equal(4101, Assert.Single(Games().List(_project.Path)).Pid);
        Assert.True(File.Exists(EntryPath(4101)));
    }

    [Theory]
    [InlineData("{\"pid\": 4101")]
    [InlineData("{\"pid\": \"4101\", \"startedUnixMs\": 1}")]
    [InlineData("{\"pid\": 4101.5, \"startedUnixMs\": 1}")]
    [InlineData("{\"startedUnixMs\": 1}")]
    [InlineData("{\"pid\": 4102, \"startedUnixMs\": 1}")]
    [InlineData("[4101]")]
    public void AMalformedEntryIsSkippedReportedAndKept(string content)
    {
        WriteRaw(4101, content);
        _processes[4101] = new ProcessStart(true, null);

        Assert.Empty(Games().List(_project.Path));
        Assert.Contains($"skipping the dormant game entry {EntryPath(4101)}", _errors.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(EntryPath(4101)));
    }

    [Fact]
    public void AnEntryGDScriptWroteWithFloatsIsRead()
    {
        long startedMs = Started.ToUnixTimeMilliseconds();
        WriteRaw(4101, $"{{\"pid\": 4101.0, \"startedUnixMs\": {startedMs}.0}}");
        _processes[4101] = new ProcessStart(true, null);

        Assert.Equal(new DormantGame(4101, Started), Assert.Single(Games().List(_project.Path)));
    }

    [Fact]
    public void TheJoinFileIsMovedIntoPlaceLeavingNoOtherFile()
    {
        DormantGames.WriteJoinFile(_project.Path, 4101, new BridgeEndpoint(51234, "ABCDEF"), false, false);
        DormantGames.WriteJoinFile(_project.Path, 4101, new BridgeEndpoint(51235, "FEDCBA"), false, false);

        string folder = _project.Combine(".godot", "godot-mcp");
        Assert.Equal([Path.Combine(folder, "join-4101.json")], Directory.GetFiles(folder));
        Assert.Equal("FEDCBA", JsonNode.Parse(File.ReadAllText(DormantGames.JoinPathIn(_project.Path, 4101)))!["token"]!.GetValue<string>());
    }

    [Fact]
    public void TheJoinFileHasTheAttachFilesShapeAndIsRemovedOnce()
    {
        DormantGames.WriteJoinFile(_project.Path, 4101, new BridgeEndpoint(51234, "ABCDEF"), true, false);

        string path = _project.Combine(".godot", "godot-mcp", "join-4101.json");
        JsonNode content = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(
            (51234, "ABCDEF", true, false),
            (
                content["port"]!.GetValue<int>(),
                content["token"]!.GetValue<string>(),
                content["shutOutRealGamepads"]!.GetValue<bool>(),
                content["quiet"]!.GetValue<bool>()
            )
        );
        Assert.Equal(path, DormantGames.JoinPathIn(_project.Path, 4101));
        Assert.True(DormantGames.RemoveJoinFile(_project.Path, 4101));
        Assert.False(DormantGames.RemoveJoinFile(_project.Path, 4101));
    }

    [Fact]
    public void TheRealProbeFindsThisProcessAndNotAnUnusedPid()
    {
        ProcessStart self = DormantGames.ProbeProcess(Environment.ProcessId);

        Assert.True(self.Exists);
        Assert.NotNull(self.StartTime);
        Assert.False(DormantGames.ProbeProcess(int.MaxValue).Exists);
    }

    private DormantGames Games() => new(pid => _processes.GetValueOrDefault(pid, new ProcessStart(false, null)), _errors);

    private string EntryPath(int pid) => Path.Combine(DormantGames.FolderIn(_project.Path), $"{pid}.json");

    private void WriteEntry(int pid, DateTimeOffset started) =>
        WriteRaw(pid, $"{{\"pid\":{pid},\"startedUnixMs\":{started.ToUnixTimeMilliseconds()}}}");

    private void WriteRaw(int pid, string content)
    {
        Directory.CreateDirectory(DormantGames.FolderIn(_project.Path));
        File.WriteAllText(EntryPath(pid), content);
    }
}
