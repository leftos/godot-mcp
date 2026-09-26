using System.Diagnostics;
using GodotMcp.Server.Wire;

namespace GodotMcp.Server.Session;

/// <summary>One Godot process the server launched, its captured output and its bridge connection.</summary>
/// <param name="projectDir">The project folder the process runs.</param>
/// <param name="process">The Godot process, not yet started.</param>
/// <param name="previous">The run this one replaces on a restart, whose output buffers it continues; null for a first launch.</param>
internal sealed class GodotRun(string projectDir, Process process, GodotRun? previous) : IAsyncDisposable
{
    public const int OutputCapacity = 500;

    public string ProjectDir { get; } = projectDir;

    public Process Process { get; } = process;

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
