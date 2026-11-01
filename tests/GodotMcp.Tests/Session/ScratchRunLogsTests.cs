using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A scratch run's game log: named for the scene's session and the run, so repeated runs of one scene keep a log each, and the
/// folder pruned to its newest ones when a run starts.
/// </summary>
public sealed class ScratchRunLogsTests : IDisposable
{
    private const int Logs = 23;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void PathForNamesTheSessionAndTheRun() =>
        Assert.EndsWith(
            "run-delve.scratch-Lobby-20261031T231358-3f2a.log",
            ScratchRunLogs.PathFor("C:/x", "delve.scratch-Lobby", "20261031T231358-3f2a"),
            StringComparison.Ordinal
        );

    [Fact]
    public void PruneKeepsTheNewestScratchRunLogs()
    {
        List<string> names = WriteLogs();
        string sessionLog = _temp.Combine("run-delve.log");
        string spill = _temp.Combine("scratch-20261031T231358-3f2a.log");
        File.WriteAllText(sessionLog, "x");
        File.WriteAllText(spill, "x");

        ScratchRunLogs.Prune(_temp.Path, ScratchRunLogs.Kept, NullLogger.Instance);

        Assert.Equal(names.Skip(Logs - ScratchRunLogs.Kept), Present(names));
        Assert.True(File.Exists(sessionLog), "a session's own run log is not a scratch run log");
        Assert.True(File.Exists(spill), "a run's spill file is not a run log");
    }

    [Fact]
    public void PruneLeavesAFileItCannotDelete()
    {
        List<string> names = WriteLogs();
        using FileStream held = new(_temp.Combine(names[0]), FileMode.Open, FileAccess.Read, FileShare.None);

        ScratchRunLogs.Prune(_temp.Path, ScratchRunLogs.Kept, NullLogger.Instance);

        List<string> left = [names[0], .. names.Skip(Logs - ScratchRunLogs.Kept)];
        Assert.Equal(left, Present(names));
    }

    /// <summary>
    /// <see cref="Logs"/> scratch run logs in the temp folder, each written a minute after the one before, so their last write
    /// times ascend, and their names in that order.
    /// </summary>
    private List<string> WriteLogs()
    {
        DateTime written = new(2026, 10, 31, 23, 13, 58, DateTimeKind.Utc);
        List<string> names = [];
        for (int index = 0; index < Logs; index++)
        {
            string name = $"run-delve.scratch-Lobby-20261031T231358-{index:x4}.log";
            string path = _temp.Combine(name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, written.AddMinutes(index));
            names.Add(name);
        }

        return names;
    }

    /// <summary>The names in <paramref name="names"/> whose file is still in the temp folder, in the same order.</summary>
    private List<string> Present(List<string> names) => [.. names.Where(name => File.Exists(_temp.Combine(name)))];
}
