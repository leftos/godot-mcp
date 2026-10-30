using System.ComponentModel;
using System.Diagnostics;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// How a run's process starts and how its output is read: <see cref="StartInfoProcess"/> through <see cref="System.Diagnostics.Process.Start()"/>,
/// or <see cref="DesktopProcess"/> through <c>CreateProcessW</c> onto the hidden desktop.
/// </summary>
internal interface IRunProcess
{
    /// <summary>The process: unstarted, or created suspended, until <see cref="Start"/> returns.</summary>
    Process Process { get; }

    /// <summary>Lets the process run. Its <c>Exited</c> handler is attached before this.</summary>
    /// <exception cref="SessionException">The process could not be started.</exception>
    void Start();

    /// <summary>
    /// Closes the process's stdin and passes each line of its stdout and stderr on until the end of the stream, then calls
    /// <paramref name="ended"/> once both streams have reached their end. After <see cref="CancelRead"/>, <paramref name="ended"/>
    /// may never be called (<see cref="StartInfoProcess"/> no longer sees the end), so a caller that cancels the read closes
    /// what <paramref name="ended"/> would have closed itself.
    /// </summary>
    void BeginRead(Action<string> stdout, Action<string> stderr, Action ended);

    /// <summary>Stops passing lines on.</summary>
    /// <exception cref="InvalidOperationException">The output is not being read.</exception>
    void CancelRead();
}

/// <summary>A process started with <see cref="System.Diagnostics.Process.Start()"/> from its <see cref="ProcessStartInfo"/>.</summary>
internal sealed class StartInfoProcess(Process process) : IRunProcess
{
    public Process Process { get; } = process;

    public void Start()
    {
        try
        {
            ChildProcesses.Start(Process);
        }
        catch (Win32Exception e)
        {
            string fileName = Process.StartInfo.FileName;
            Process.Dispose();
            throw new SessionException(
                $"Godot could not be started from {fileName}: {e.Message}. Set {Installation.GodotPathVariable} to the Godot 4.7 console executable.",
                e
            );
        }
    }

    public void BeginRead(Action<string> stdout, Action<string> stderr, Action ended)
    {
        StreamEnd end = new(ended);
        Process.OutputDataReceived += (_, e) => PassOn(stdout, e.Data, end);
        Process.ErrorDataReceived += (_, e) => PassOn(stderr, e.Data, end);
        Process.StandardInput.Close();
        Process.BeginOutputReadLine();
        Process.BeginErrorReadLine();
    }

    public void CancelRead()
    {
        Process.CancelOutputRead();
        Process.CancelErrorRead();
    }

    private static void PassOn(Action<string> handler, string? line, StreamEnd end)
    {
        if (line is null)
        {
            end.One();
        }
        else
        {
            handler(line);
        }
    }
}

/// <summary>
/// Counts a process's two output streams reaching their end, and calls <c>ended</c> once both have, from whichever stream ends
/// last; a stream that never ends never ends the pair.
/// </summary>
internal sealed class StreamEnd(Action ended)
{
    private const int Streams = 2;

    private int _ended;

    public void One()
    {
        if (Interlocked.Increment(ref _ended) == Streams)
        {
            ended();
        }
    }
}

/// <summary>One Godot process the server launched, its captured output and its bridge connection.</summary>
/// <param name="projectDir">The project folder the process runs.</param>
/// <param name="launcher">The Godot process, not yet running, with how it starts and how its output is read.</param>
/// <param name="previous">The run this one replaces on a restart, whose output buffers it continues; null for a first launch.</param>
internal sealed class GodotRun(string projectDir, IRunProcess launcher, GodotRun? previous) : IAsyncDisposable
{
    public const int OutputCapacity = 500;

    /// <summary>How long a released game handle waits for a game still ending with its wrapper, as a killed process tree may be.</summary>
    private static readonly TimeSpan GameExitWait = TimeSpan.FromSeconds(1);

    // Settled by the first line of this run's own output that says Godot could not create a window. A restart's buffers carry
    // its predecessor's lines over, so only a line arriving through this run's handlers can settle it.
    private readonly TaskCompletionSource<string> _noGlWindowLine = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Settled once both of this run's output streams have reached their end, so no line of it can still be in flight.
    private readonly TaskCompletionSource _outputDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Process? _game;
    private int? _gameExitCode;
    private bool _killed;

    /// <summary>A run of a process started with <see cref="System.Diagnostics.Process.Start()"/>, not yet started.</summary>
    public GodotRun(string projectDir, Process process, GodotRun? previous)
        : this(projectDir, new StartInfoProcess(process), previous) { }

    public string ProjectDir { get; } = projectDir;

    /// <summary>How the process starts and how its output is read.</summary>
    public IRunProcess Launcher { get; } = launcher;

    public Process Process => Launcher.Process;

    public OutputBuffer Stdout { get; } = previous?.Stdout ?? new(OutputCapacity);

    public OutputBuffer Stderr { get; } = previous?.Stderr ?? new(OutputCapacity);

    /// <summary>
    /// The file this run's whole output goes to, both streams interleaved; a restart carries its predecessor's path over and
    /// writes on into its file. Null until the file is opened, and when no file could be opened.
    /// </summary>
    public string? OutputLogPath { get; set; } = previous?.OutputLogPath;

    /// <summary>The open file the run writes its whole output to; null until the run starts, and when no file could be opened.</summary>
    public RunOutputLog? OutputLog { get; set; }

    /// <summary>
    /// Completes once both of the run's output streams have been read to their end, so no line its process printed can still be
    /// in flight; a run whose output was never read never completes it.
    /// </summary>
    public Task OutputDrained => _outputDrained.Task;

    /// <summary>Closes the run's output file and marks its output read to its end.</summary>
    public void OutputEnded()
    {
        OutputLog?.Dispose();
        _outputDrained.TrySetResult();
    }

    /// <summary>
    /// The first line of this run's own output that says Godot could not create an ANGLE OpenGL window, trimmed; it never
    /// completes for a run whose output never says it. The line arrives on a reader thread, and its continuations run
    /// asynchronously so the reader is never held up by the wait that joins this task.
    /// </summary>
    public Task<string> NoGlWindowLine => _noGlWindowLine.Task;

    /// <summary>Records one line the run's process printed: the first one that says Godot could not create a window settles it.</summary>
    public void WatchOutput(string line)
    {
        if (NoGlLine.Matches(line))
        {
            _noGlWindowLine.TrySetResult(line.Trim());
        }
    }

    public BridgeConnection? Connection { get; set; }

    /// <summary>The game's own process, kept from its hello by <see cref="KeepGameHandle"/>; null before, without one, or once released.</summary>
    public Process? Game => _game;

    public bool IsRunning
    {
        get
        {
            try
            {
                return !Process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // The process was never started.
                return false;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return Process.HasExited ? Process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                // The process was never started.
                return null;
            }
        }
    }

    /// <summary>
    /// Opens and keeps a handle on the game's own process (on Windows the run's process only wraps it), so its exit code can
    /// be read after it has exited. A game the server cannot open is logged and gets no handle.
    /// </summary>
    public void KeepGameHandle(int gameProcessId, ILogger logger)
    {
        Process? game = null;
        try
        {
            game = Process.GetProcessById(gameProcessId);
            // Reading Handle opens the process handle and keeps it on the object, which keeps the exit code readable.
            _ = game.Handle;
            _game = game;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            game?.Dispose();
            Log.GameHandleFailed(logger, e, gameProcessId, ProjectDir);
        }
    }

    /// <summary>
    /// Records that the server killed the game, so <see cref="ReleaseGameAsync"/> reports no exit code: the code read after
    /// a kill is not the game's own.
    /// </summary>
    public void MarkKilled() => _killed = true;

    /// <summary>
    /// Lets go of the game's handle and keeps its exit code, so a later call returns the same. It waits up to
    /// <see cref="GameExitWait"/> for a game still ending, unless the game was killed, whose kill has already waited for it;
    /// null with no handle, while the game still runs, or after <see cref="MarkKilled"/>.
    /// </summary>
    public async Task<int?> ReleaseGameAsync()
    {
        if (_game is not { } game)
        {
            return _killed ? null : _gameExitCode;
        }

        _game = null;
        using (game)
        {
            if (!_killed)
            {
                _gameExitCode = await ReadExitCodeAsync(game);
            }
        }

        return _killed ? null : _gameExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null)
        {
            await Connection.DisposeAsync();
            Connection = null;
        }

        _game?.Dispose();
        _game = null;
        Process.Dispose();
    }

    private static async Task<int?> ReadExitCodeAsync(Process game)
    {
        if (!await ProcessExit.WaitUntilGoneAsync(game, GameExitWait))
        {
            return null;
        }

        try
        {
            return game.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
