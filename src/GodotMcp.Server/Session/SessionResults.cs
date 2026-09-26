namespace GodotMcp.Server.Session;

/// <summary>A run that launched and whose bridge said hello.</summary>
internal sealed record LaunchResult(string ProjectPath, int ProcessId, bool Background);

/// <summary>How a run ended: its exit code, whether it had to be killed, whether the server's override.cfg was deleted.</summary>
internal sealed record StopResult(string ProjectPath, int? ExitCode, bool Killed, bool OverrideRemoved);

/// <summary>A game attach_project reached: its bridge said hello.</summary>
internal sealed record AttachResult(string ProjectPath);

/// <summary>How detach_project left the project: the game still runs; whether the server's override.cfg was deleted.</summary>
internal sealed record DetachResult(string ProjectPath, bool OverrideRemoved);

/// <summary>The latest run's state and the newest lines of its output.</summary>
internal sealed record DebugOutput
{
    public string? ProjectPath { get; init; }

    public bool Running { get; init; }

    public int? ExitCode { get; init; }

    public IReadOnlyList<string> Stdout { get; init; } = [];

    public IReadOnlyList<string> Stderr { get; init; } = [];

    public long StdoutTotalLines { get; init; }

    public long StderrTotalLines { get; init; }
}
