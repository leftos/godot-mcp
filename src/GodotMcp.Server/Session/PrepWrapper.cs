using System.Globalization;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Server.Session;

/// <summary>
/// The command a project's godot-mcp.json names under <c>prepWrapper</c>, which the prep runs each of its processes through
/// (the C# build and the Godot import): the command's first element is the program, the rest its
/// arguments, with <c>{log}</c> replaced by the step's log and <c>{ceiling}</c> by the prep's ceiling in whole seconds, and
/// the wrapped program and its arguments appended: a program with a folder in it is absolute against its own working
/// directory, and a bare name is left for the wrapper's PATH. A wrapper that names <c>{log}</c> writes that file itself.
/// </summary>
/// <param name="Command">The wrapper's program, then its arguments, as the file lists them.</param>
/// <param name="ProjectDir">The folder holding the godot-mcp.json: the wrapper's working directory.</param>
internal sealed record PrepWrapper(IReadOnlyList<string> Command, string ProjectDir)
{
    public const string LogToken = "{log}";

    public const string CeilingToken = "{ceiling}";

    /// <summary>The prefixes of the lines a gate prints as its closing verdict.</summary>
    private static readonly string[] VerdictForms = ["gate: passed in ", "gate: FAILED (status "];

    /// <summary>The prefixes of the lines a gate prints when its watchdog killed the run: each names the cause.</summary>
    private static readonly string[] KillForms = ["gate: STALLED", "gate: TIMED OUT", "gate: BACKSTOP", "gate: TRANSFER"];

    /// <summary>How much of a verdict line a result carries.</summary>
    public const int VerdictLength = 200;

    /// <summary>The exit code with which a wrapper says it killed the command it ran.</summary>
    public const int StoppedExitCode = 124;

    /// <summary>The wrapper's program as the file names it.</summary>
    public string Program => Command[0];

    /// <summary>Whether an element names the step's log, so the wrapper writes it and its own output goes to a file beside it.</summary>
    public bool OwnsLog => Command.Any(element => element.Contains(LogToken, StringComparison.Ordinal));

    /// <summary>What the prep's note says when a process ran through the wrapper.</summary>
    public string Note => $"ran through prepWrapper ({Program})";

    /// <summary>The folder's godot-mcp.json <c>prepWrapper</c>, read and checked as run_project reads the whole file.</summary>
    /// <returns>The wrapper; null when the folder has no godot-mcp.json or it sets none.</returns>
    /// <exception cref="SessionException">The godot-mcp.json is refused, with the profile's own message.</exception>
    public static PrepWrapper? Read(string projectDir)
    {
        IReadOnlyList<string>? command;
        try
        {
            command = ProjectProfile.Load(projectDir).PrepWrapper;
        }
        catch (McpException e)
        {
            throw new SessionException(e.Message, e);
        }

        return command is null ? null : new PrepWrapper(command, projectDir);
    }

    /// <summary>The file a step's log becomes when a wrapper writes it: <c>build.log</c> gives <c>build.wrapper.log</c>.</summary>
    public static string WrapperLog(string stepLog) =>
        Path.Combine(Path.GetDirectoryName(stepLog)!, Path.GetFileNameWithoutExtension(stepLog) + ".wrapper.log");

    /// <summary>Where the server writes the wrapper's own output: <see cref="WrapperLog"/> when it owns the step's log, else that log.</summary>
    public string CaptureLog(string stepLog) => OwnsLog ? WrapperLog(stepLog) : stepLog;

    /// <summary>
    /// The closing verdict line of a wrapper's capture log: the last line that starts with one of <see cref="VerdictForms"/>,
    /// or, when the gate killed the run, the last line that starts with one of <see cref="KillForms"/>, since the
    /// <c>gate: FAILED (status 124)</c> after a kill hides which kill it was. The prose a gate prints around its verdict
    /// (<c>gate: waiting for a heavy slot</c>, and a routed run's <c>gate: restoring here</c> and <c>gate: restored …</c>
    /// after it) is never a verdict. Null when the log holds none, which is what a wrapper that is not a gate leaves.
    /// A line loses a trailing carriage return and is cut to <see cref="VerdictLength"/> characters.
    /// </summary>
    public static string? GateVerdict(string logText)
    {
        string[] lines = [.. logText.Split('\n').Select(line => line.TrimEnd('\r'))];
        string? verdict = LastLineStartingWith(lines, KillForms) ?? LastLineStartingWith(lines, VerdictForms);
        return verdict is null || verdict.Length <= VerdictLength ? verdict : verdict[..VerdictLength];
    }

    /// <summary>The last of <paramref name="lines"/> that starts with one of <paramref name="forms"/>, or null when none does.</summary>
    private static string? LastLineStartingWith(string[] lines, string[] forms) =>
        lines.LastOrDefault(line => forms.Any(form => line.StartsWith(form, StringComparison.Ordinal)));

    /// <summary>
    /// <paramref name="request"/> run through the wrapper, from the project folder, with its output captured into
    /// <see cref="CaptureLog"/> and no stall kill: a wrapper waiting for a slot prints nothing and uses no CPU. The ceiling
    /// and its wall backstop stay.
    /// </summary>
    public ToolProcessRequest Wrap(ToolProcessRequest request)
    {
        string ceiling = ((long)request.Ceiling.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        string[] command =
        [
            .. Command.Select(element =>
                element.Replace(LogToken, request.LogPath, StringComparison.Ordinal).Replace(CeilingToken, ceiling, StringComparison.Ordinal)
            ),
        ];
        return request with
        {
            FileName = ResolveProgram(command[0], ProjectDir),
            Arguments = [.. command.Skip(1), ResolveProgram(request.FileName, request.WorkingDirectory), .. request.Arguments],
            WorkingDirectory = ProjectDir,
            LogPath = CaptureLog(request.LogPath),
            StallLimit = TimeSpan.MaxValue,
        };
    }

    /// <summary>Whether the wrapper says it killed the command: it exited <see cref="StoppedExitCode"/> by itself.</summary>
    public static bool Stopped(ToolProcessResult result) => !result.WasKilled && result.ExitCode == StoppedExitCode;

    /// <summary>The note, and the refusal, of a step the wrapper stopped, naming the step's log and the wrapper's own output.</summary>
    public string StoppedNote(string step, string stepLog)
    {
        string note = $"the prep wrapper stopped {step} (exit {StoppedExitCode}); its log: {stepLog}";
        return OwnsLog ? $"{note}; the wrapper's output: {WrapperLog(stepLog)}" : note;
    }

    /// <summary>A program with a folder in it is relative to the project folder; a bare name is looked up on PATH.</summary>
    internal static string ResolveProgram(string program, string projectDir) =>
        program.IndexOfAny(['/', '\\']) < 0 ? program : Path.GetFullPath(Path.Combine(projectDir, program));
}
