using System.Text.Json.Serialization;

namespace GodotMcp.Server.Session;

/// <summary>
/// A run that launched and whose bridge said hello, the Godot it launched, what the prep did before it, and the file it
/// records to.
/// </summary>
internal sealed record LaunchResult(string Session, string ProjectPath, int ProcessId, bool Quiet, PrepResult Prep, string Godot)
{
    /// <summary>The server's version, to quote when filing an issue.</summary>
    public string Version { get; init; } = ServerVersion.Value;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecordingResult? Recording { get; init; }
}

/// <summary>
/// A recording run's file: <see cref="Path"/> is the full movie while it is kept (during the run, and after it when there were
/// no marks or the cut failed), <see cref="Clips"/> the files cut from the marks, <see cref="Error"/> why the cut did not
/// finish.
/// </summary>
internal sealed record RecordingResult
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Clips { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

/// <summary>
/// What run_project's prep did: <see cref="Build"/> is up-to-date, built, no-csproj or skipped, or failed for a headless run's
/// prep, which reports a red build instead of refusing (<see cref="ProjectPrep.RunReportingBuildAsync"/>); <see cref="Import"/> is
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

/// <summary>
/// A restarted run: the new process, the Godot it launched, the one it replaced and that one's exit code (the wrapper's on
/// Windows; left out while unknown), and what the prep did before the relaunch.
/// </summary>
internal sealed record RestartResult(
    string Session,
    string ProjectPath,
    int ProcessId,
    int PreviousProcessId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PreviousExitCode,
    PrepResult Prep,
    string Godot
)
{
    /// <summary>The server's version, to quote when filing an issue.</summary>
    public string Version { get; init; } = ServerVersion.Value;

    /// <summary>Whether the replaced run had already ended when the restart began.</summary>
    public required bool PreviousAlreadyExited { get; init; }

    /// <summary>The replaced game's own exit code; left out without a handle on it, or when it could not be read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public required int? PreviousGameExitCode { get; init; }

    /// <summary>Why the replaced game had to be killed; left out when it quit.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousKillReason { get; init; }

    /// <summary>
    /// How long the replaced game took to exit after the quit request, in wall milliseconds; left out when it was killed or had
    /// already exited. Against the grace (3 s, 30 s for a recording run), it shows a quit coming close to a kill.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PreviousQuitMs { get; init; }

    /// <summary>
    /// The processes the replaced game started and left running when it quit, as "name (pid N)"; they were ended with the
    /// console wrapper. Left out when there were none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? PreviousLeftRunning { get; init; }

    /// <summary>The file the new game records to.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecordingResult? Recording { get; init; }

    /// <summary>How the replaced game's recording ended: its full file or its clips.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecordingResult? PreviousRecording { get; init; }

    /// <summary>Set when a debugger was attached to the replaced game, whose debug session ended with it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Warning { get; init; }
}

/// <summary>
/// How a run ended: its exit code (the wrapper's on Windows), whether it had to be killed, whether the server's override.cfg
/// was deleted, and for a recording run its full file or its clips.
/// </summary>
internal sealed record StopResult(string Session, string ProjectPath, int? ExitCode, bool Killed, bool OverrideRemoved)
{
    /// <summary>Whether the run had already ended when the stop began: the game quit, crashed or was killed from outside.</summary>
    public required bool AlreadyExited { get; init; }

    /// <summary>
    /// The game's own exit code; null without a handle on it, when it could not be read, or when the game had to be killed,
    /// since the code read after a kill is not the game's own.
    /// </summary>
    public required int? GameExitCode { get; init; }

    /// <summary>Why the game had to be killed; left out when it quit.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KillReason { get; init; }

    /// <summary>
    /// How long the game took to exit after the quit request, in wall milliseconds; left out when it was killed or had already
    /// exited. Against the grace (3 s, 30 s for a recording run), it shows a quit coming close to a kill.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? QuitMs { get; init; }

    /// <summary>
    /// The processes the game started and left running when it quit, as "name (pid N)"; they were ended with the console
    /// wrapper. Left out when there were none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? LeftRunning { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecordingResult? Recording { get; init; }

    /// <summary>Set when a debugger was attached to the game, whose debug session ended with it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Warning { get; init; }
}

/// <summary>A game attach_project reached: its bridge said hello, and whether it was told to park its window.</summary>
internal sealed record AttachResult(string Session, string ProjectPath, bool Quiet)
{
    /// <summary>The server's version, to quote when filing an issue.</summary>
    public string Version { get; init; } = ServerVersion.Value;
}

/// <summary>How detach_project left the project: the game still runs; whether the server's override.cfg was deleted.</summary>
internal sealed record DetachResult(string Session, string ProjectPath, bool OverrideRemoved);

/// <summary>What list_sessions returns: the sessions it was asked for, ordered by name.</summary>
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
