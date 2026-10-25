using System.Text;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// A run session's whole output in one file: every line of its stdout and stderr, interleaved in the order they arrived and
/// written as they are, beside the <see cref="GodotRun.OutputCapacity"/>-line rings <c>get_debug_output</c> pages. Its two
/// streams arrive on two threads, so each line is written under a lock; a line arriving after <see cref="Dispose"/> is
/// dropped, as <see cref="ToolProcess.LogSink"/> drops one after its close, since the readers draining a stopped run's pipes
/// outlive it. A write or close that fails (a full disk, a lost drive) is logged once and closes the file, never throwing on
/// the reader thread that called it: a throw there would end the server or stop the pipe's draining, which blocks the game.
/// </summary>
internal sealed class RunOutputLog : IDisposable
{
    private readonly Lock _lock = new();
    private readonly StreamWriter _writer;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;

    /// <summary>Writes to <paramref name="stream"/>, which the log owns and closes; internal so a test can give it one that fails.</summary>
    internal RunOutputLog(string path, Stream stream, ILogger logger)
    {
        Path = path;
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        _logger = logger;
    }

    /// <summary>The file's full path.</summary>
    public string Path { get; }

    /// <summary>Completes once the file has closed, so a stop can wait for the run's readers to finish before it returns.</summary>
    public Task Closed => _closing.Task;

    /// <summary>Creates the file's folder and opens the file fresh, or keeps what an earlier game already wrote to it.</summary>
    /// <param name="path">The file's full path.</param>
    /// <param name="append">Whether the file keeps its earlier lines, for a restart writing on into its predecessor's file.</param>
    /// <param name="logger">Where a failed write or close is reported.</param>
    /// <exception cref="IOException">The file or its folder could not be created or opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
    /// <remarks>
    /// The file is shared for reading, so an agent reads the output while the game writes it, and for deleting, so the file
    /// itself can be deleted while it is open. The folder it is in cannot be renamed or moved until the file is closed, which
    /// is why the session closes it when the run's output ends and before a stop returns.
    /// </remarks>
    public static RunOutputLog Open(string path, bool append, ILogger logger)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        FileStream stream = new(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
        return new RunOutputLog(path, stream, logger);
    }

    /// <summary>Writes one line as it arrived; called from the run's output reader threads, and never throws.</summary>
    public void Write(string line)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            try
            {
                _writer.WriteLine(line);
                return;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                Log.RunLogWriteFailed(_logger, e, Path);
                CloseLocked();
            }
        }

        _closing.TrySetResult();
    }

    /// <summary>Closes the file and completes <see cref="Closed"/>; a second call does nothing, and a failed close is logged.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (!_closed)
            {
                CloseLocked();
            }
        }

        _closing.TrySetResult();
    }

    /// <summary>Marks the log closed and closes the file, logging a close that fails, such as its last flush.</summary>
    private void CloseLocked()
    {
        _closed = true;
        try
        {
            _writer.Dispose();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            Log.RunLogCloseFailed(_logger, e, Path);
        }
    }
}
