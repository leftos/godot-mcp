using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The file a run writes its whole output to: truncated on each open, whole lines from both streams under one lock, and
/// quiet once it is closed.
/// </summary>
public sealed class RunOutputLogTests : IDisposable
{
    private const int LinesPerStream = 1000;
    private const int Padding = 200;

    private static readonly string[] Streams = ["stdout", "stderr"];
    private static readonly string PaddingText = new('x', Padding);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void OpenTruncatesAnEarlierFile()
    {
        string path = _temp.Combine("run-probe.log");
        File.WriteAllText(path, "an earlier run's line\n");

        using (var log = RunOutputLog.Open(path, append: false, NullLogger.Instance))
        {
            log.Write("the new run's line");
        }

        Assert.Equal(["the new run's line"], File.ReadAllLines(path));
    }

    [Fact]
    public void OpenInAppendModeKeepsEarlierLines()
    {
        string path = _temp.Combine("run-probe.log");
        File.WriteAllText(path, "the first game's line\n");

        using (var log = RunOutputLog.Open(path, append: true, NullLogger.Instance))
        {
            log.Write("the restarted game's line");
        }

        Assert.Equal(["the first game's line", "the restarted game's line"], File.ReadAllLines(path));
    }

    [Fact]
    public void LinesFromTwoThreadsAreAllWrittenWhole()
    {
        string path = _temp.Combine("run-probe.log");

        using (var log = RunOutputLog.Open(path, append: false, NullLogger.Instance))
        {
            Thread[] writers = [.. Streams.Select(stream => new Thread(() => WriteLines(log, stream)))];
            writers.ToList().ForEach(writer => writer.Start());
            writers.ToList().ForEach(writer => writer.Join());
        }

        string[] written = File.ReadAllLines(path);
        string[] expected = [.. Streams.SelectMany(stream => Enumerable.Range(0, LinesPerStream).Select(index => Line(stream, index)))];

        Assert.Equal(expected.Order(StringComparer.Ordinal), written.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AWriteAfterDisposeIsDropped()
    {
        string path = _temp.Combine("run-probe.log");
        var log = RunOutputLog.Open(path, append: false, NullLogger.Instance);
        log.Write("before the close");

        log.Dispose();
        log.Write("after the close");
        log.Dispose();

        Assert.Equal(["before the close"], File.ReadAllLines(path));
    }

    [Fact]
    public async Task ClosedCompletesOnDispose()
    {
        string path = _temp.Combine("run-probe.log");
        var log = RunOutputLog.Open(path, append: false, NullLogger.Instance);
        Assert.False(log.Closed.IsCompleted);

        log.Dispose();
        await log.Closed;

        Assert.True(log.Closed.IsCompleted);
    }

    [Fact]
    public void AWriteThatFailsClosesTheLogAndDropsLaterLines()
    {
        FailingStream stream = new();
        RecordingLogger logger = new();
        var log = new RunOutputLog(_temp.Combine("run-probe.log"), stream, logger);

        log.Write("the line the full disk refuses");
        log.Write("a later line");
        log.Dispose();

        Assert.Equal(1, stream.Writes);
        Assert.True(stream.Closed);
        Assert.True(log.Closed.IsCompletedSuccessfully);
        string failure = Assert.Single(logger.Messages);
        Assert.Contains("run-probe.log failed", failure, StringComparison.Ordinal);
    }

    private static void WriteLines(RunOutputLog log, string stream)
    {
        for (int index = 0; index < LinesPerStream; index++)
        {
            log.Write(Line(stream, index));
        }
    }

    private static string Line(string stream, int index) => $"{stream} {index} {PaddingText}";

    /// <summary>A file on a full disk: every write throws, and the attempts are counted.</summary>
    private sealed class FailingStream : Stream
    {
        public int Writes { get; private set; }

        public bool Closed { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes++;
            throw new IOException("There is not enough space on the disk.");
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>Every message logged, formatted.</summary>
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
