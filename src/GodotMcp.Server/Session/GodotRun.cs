using System.Diagnostics;
using GodotMcp.Server.Wire;

namespace GodotMcp.Server.Session;

/// <summary>One Godot process the server launched, its captured output and its bridge connection.</summary>
internal sealed class GodotRun(string projectDir, Process process) : IAsyncDisposable
{
    public const int OutputCapacity = 500;

    public string ProjectDir { get; } = projectDir;

    public Process Process { get; } = process;

    public OutputBuffer Stdout { get; } = new(OutputCapacity);

    public OutputBuffer Stderr { get; } = new(OutputCapacity);

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
