using System.Text.Json.Serialization;

namespace GodotMcp.Server.Session;

/// <summary>A run that launched and whose bridge said hello, and what the prep did before it.</summary>
internal sealed record LaunchResult(string Session, string ProjectPath, int ProcessId, bool Quiet, PrepResult Prep);

/// <summary>
/// What run_project's prep did: <see cref="Build"/> is up-to-date, built, no-csproj or skipped; <see cref="Import"/> is
/// not-needed, done or skipped (both skipped under prepare "never"); the times are set for a step that ran.
/// </summary>
internal sealed record PrepResult
{
    /// <summary>What a launch with prepare "never" reports.</summary>
    public static PrepResult Skipped { get; } = new() { Build = "skipped", Import = "skipped" };

    public required string Build { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BuildMs { get; init; }

    public required string Import { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ImportMs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}

/// <summary>How a run ended: its exit code, whether it had to be killed, whether the server's override.cfg was deleted.</summary>
internal sealed record StopResult(string Session, string ProjectPath, int? ExitCode, bool Killed, bool OverrideRemoved);

/// <summary>A game attach_project reached: its bridge said hello.</summary>
internal sealed record AttachResult(string Session, string ProjectPath);

/// <summary>How detach_project left the project: the game still runs; whether the server's override.cfg was deleted.</summary>
internal sealed record DetachResult(string Session, string ProjectPath, bool OverrideRemoved);

/// <summary>What list_sessions returns: every session, ordered by name.</summary>
internal sealed record SessionList(IReadOnlyList<SessionInfo> Sessions);

/// <summary>
/// The latest run's state and a page of each stream: its lines, and the number of the first of them (null when there are
/// none), to pass as before for the page ahead of it.
/// </summary>
internal sealed record DebugOutput
{
    public string? Session { get; init; }

    public string? ProjectPath { get; init; }

    public bool Running { get; init; }

    public int? ExitCode { get; init; }

    public IReadOnlyList<string> Stdout { get; init; } = [];

    public IReadOnlyList<string> Stderr { get; init; } = [];

    public long? StdoutFirstLine { get; init; }

    public long? StderrFirstLine { get; init; }

    public long StdoutTotalLines { get; init; }

    public long StderrTotalLines { get; init; }
}
