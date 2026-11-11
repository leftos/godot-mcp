using GodotMcp.Server.Session;
using GodotMcp.Tests.Tools;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A scratch run's game log: named for the scene's session and the run, so repeated runs of one scene keep a log each, and the
/// folder pruned to its newest ones when a run starts; and the shot folders each run keeps its shots in, pruned the same way.
/// </summary>
public sealed class ScratchRunLogsTests : IDisposable
{
    private const int Logs = 23;
    private const int Shots = 23;

    private static readonly DateTime Written = new(2026, 10, 31, 23, 13, 58, DateTimeKind.Utc);

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

    [Fact]
    public void PruneShotsKeepsTheNewestShotFolders()
    {
        List<string> names = WriteShotFolders();

        ScratchRunLogs.PruneShots(_temp.Path, ScratchRunLogs.Kept, NullLogger.Instance);

        Assert.Equal(names.Skip(Shots - ScratchRunLogs.Kept), PresentFolders(names));
    }

    [Fact]
    public void PruneShotsIgnoresTheRunsFilesAndUnrelatedFolders()
    {
        List<string> names = WriteShotFolders();
        string status = _temp.Combine("scratch-20261031T231358-00ff.json");
        string log = _temp.Combine("scratch-20261031T231358-00ff.log");
        string unrelated = Directory.CreateDirectory(_temp.Combine("userdata-1234-Probe-0badc0de")).FullName;
        File.WriteAllText(status, "x");
        File.WriteAllText(log, "x");

        ScratchRunLogs.PruneShots(_temp.Path, keep: 0, NullLogger.Instance);

        Assert.Empty(PresentFolders(names));
        Assert.True(File.Exists(status), "a run's status file is not a shot folder");
        Assert.True(File.Exists(log), "a run's log file is not a shot folder");
        Assert.True(Directory.Exists(unrelated), "a folder that is not a run's shots is not pruned");
    }

    [Fact]
    public void PruneShotsLeavesAFolderItCannotDeleteAndLogsIt()
    {
        List<string> names = WriteShotFolders();
        string held = _temp.Combine(names[0]);
        using FileStream open = new(Path.Combine(held, "0-step.png"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Directory.SetLastWriteTimeUtc(held, Written);
        RecordingLogger logger = new();

        ScratchRunLogs.PruneShots(_temp.Path, ScratchRunLogs.Kept, logger);

        Assert.Contains(names[0], PresentFolders(names));
        Assert.Contains(logger.Messages, message => message.Contains(held, StringComparison.Ordinal));
    }

    [Fact]
    public void PruneShotFolderTakesAFolderAnotherRunAlreadyDeletedAsDoneAndLogsNothing()
    {
        string gone = _temp.Combine("scratch-20261031T231358-0000");
        RecordingLogger logger = new();

        ScratchRunLogs.PruneShotFolder(gone, logger);

        Assert.False(Directory.Exists(gone));
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public void PruneShotsUnlinksALinkAndLeavesItsTarget()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink /J is Windows'");
        string target = Directory.CreateDirectory(_temp.Combine("target")).FullName;
        string shot = Path.Combine(target, "0-step.png");
        File.WriteAllText(shot, "x");
        string link = _temp.Combine("scratch-20261031T231358-abcd");
        ScreenshotPathValidationTests.MakeJunction(link, target);

        ScratchRunLogs.PruneShots(_temp.Path, keep: 0, NullLogger.Instance);

        Assert.False(Directory.Exists(link), "a link is unlinked");
        Assert.True(File.Exists(shot), "the link's target is left untouched");
    }

    /// <summary>
    /// <see cref="Logs"/> scratch run logs in the temp folder, each written a minute after the one before, so their last write
    /// times ascend, and their names in that order.
    /// </summary>
    private List<string> WriteLogs()
    {
        List<string> names = [];
        for (int index = 0; index < Logs; index++)
        {
            string name = $"run-delve.scratch-Lobby-20261031T231358-{index:x4}.log";
            string path = _temp.Combine(name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, Written.AddMinutes(index));
            names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// <see cref="Shots"/> shot folders in the temp folder, each written a minute after the one before, so their last write
    /// times ascend, and their names in that order.
    /// </summary>
    private List<string> WriteShotFolders()
    {
        List<string> names = [];
        for (int index = 0; index < Shots; index++)
        {
            string name = $"scratch-20261031T231358-{index:x4}";
            string path = Directory.CreateDirectory(_temp.Combine(name)).FullName;
            Directory.SetLastWriteTimeUtc(path, Written.AddMinutes(index));
            names.Add(name);
        }

        return names;
    }

    /// <summary>The names in <paramref name="names"/> whose file is still in the temp folder, in the same order.</summary>
    private List<string> Present(List<string> names) => [.. names.Where(name => File.Exists(_temp.Combine(name)))];

    /// <summary>The names in <paramref name="names"/> whose folder is still in the temp folder, in the same order.</summary>
    private List<string> PresentFolders(List<string> names) => [.. names.Where(name => Directory.Exists(_temp.Combine(name)))];

    /// <summary>A logger keeping every message it is given, for the warnings a prune logs.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _messages.Add(formatter(state, exception));
    }
}
