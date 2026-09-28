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

    /// <summary>Closes the process's stdin and passes each line of its stdout and stderr on until the end of the stream.</summary>
    void BeginRead(Action<string> stdout, Action<string> stderr);

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

    public void BeginRead(Action<string> stdout, Action<string> stderr)
    {
        Process.OutputDataReceived += (_, e) => PassOn(stdout, e.Data);
        Process.ErrorDataReceived += (_, e) => PassOn(stderr, e.Data);
        Process.StandardInput.Close();
        Process.BeginOutputReadLine();
        Process.BeginErrorReadLine();
    }

    public void CancelRead()
    {
        Process.CancelOutputRead();
        Process.CancelErrorRead();
    }

    private static void PassOn(Action<string> handler, string? line)
    {
        if (line is not null)
        {
            handler(line);
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
    /// <see cref="GameExitWait"/> for a game still ending; null with no handle, while the game still runs, or after
    /// <see cref="MarkKilled"/>.
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
            _gameExitCode = await ReadExitCodeAsync(game);
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
