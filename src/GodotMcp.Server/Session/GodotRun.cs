using System.ComponentModel;
using System.Diagnostics;
using GodotMcp.Server.Wire;

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

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null)
        {
            await Connection.DisposeAsync();
            Connection = null;
        }

        Process.Dispose();
    }
}
