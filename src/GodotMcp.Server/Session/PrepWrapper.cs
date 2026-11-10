using System.Globalization;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Server.Session;

/// <summary>
/// The command a project's godot-mcp.json names under <c>prepWrapper</c>, which the prep runs each of its processes through
/// (the C# build and the Godot import): the command's first element is the program, the rest its
/// arguments, with <c>{log}</c> replaced by the step's log, <c>{ceiling}</c> by the prep's ceiling in whole seconds and
/// <c>{slot}</c> by the step's slot (<see cref="HeavySlot"/> or <see cref="LightSlot"/>; a command without it keeps its own
/// slot for every step), and the wrapped program and its arguments appended: a program with a folder in it is absolute against its own working
/// directory, and a bare name is left for the wrapper's PATH. A wrapper that names <c>{log}</c> writes that file itself.
/// </summary>
/// <param name="Command">The wrapper's program, then its arguments, as the file lists them.</param>
/// <param name="ProjectDir">The folder holding the godot-mcp.json: the wrapper's working directory.</param>
internal sealed record PrepWrapper(IReadOnlyList<string> Command, string ProjectDir)
{
    public const string LogToken = "{log}";

    public const string CeilingToken = "{ceiling}";

    public const string SlotToken = "{slot}";

    /// <summary>What <see cref="SlotToken"/> becomes for the C# build, the Godot import and a process's prep.</summary>
    public const string HeavySlot = "heavy";

    /// <summary>What <see cref="SlotToken"/> becomes for the Compile-items listing: a one-second evaluation on one core.</summary>
    public const string LightSlot = "light";

    /// <summary>The prefixes of the lines a gate prints as its closing verdict.</summary>
    private static readonly string[] VerdictForms = ["gate: passed in ", "gate: FAILED (status "];

    /// <summary>The prefixes of the lines a gate prints when its watchdog killed the run: each names the cause.</summary>
    private static readonly string[] KillForms = ["gate: STALLED", "gate: TIMED OUT", "gate: BACKSTOP", "gate: TRANSFER"];

    /// <summary>The prefixes of a gate's lines while it waits for a slot: the first, then its heartbeat every 30 s.</summary>
    private static readonly string[] WaitForms = ["gate: waiting for a ", "gate: still waiting for a "];

    /// <summary>The prefix of the line a gate prints when it takes the slot it waited for.</summary>
    private const string TookForm = "gate: took a ";

    /// <summary>What follows the timestamp of a gate line in its own log: <c>2026-01-01T10:00:00+00:00 gat gate: …</c>.</summary>
    private const string StampedTag = "gat ";

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

    /// <summary>
    /// What a wrapper's console line says about its wait for a slot: <see cref="WaitState.Waiting"/> for a gate's
    /// <c>gate: waiting for a …</c> or <c>gate: still waiting for a …</c> (a heavy, light or remote slot, behind or beside
    /// another gate's request), <see cref="WaitState.Took"/> for its <c>gate: took a … slot after …</c>, and
    /// <see cref="WaitState.None"/> for any other line, its waits for a work folder or a push included. Each form counts bare
    /// or after the gate's <c>&lt;stamp&gt; gat </c> log prefix. These lines are the gate's prose, not its stable interface.
    /// A waiting wording that stops matching leaves the ceiling running from the wrapper's start, as for any other wrapper;
    /// a took wording that stops matching while the waiting lines still match never restarts the ceiling, so only the wall
    /// backstop of <see cref="LoadClock.BackstopFactor"/> times the ceiling bounds the step.
    /// </summary>
    public static WaitState Classify(string line)
    {
        string bare = BareGateLine(line);
        if (WaitForms.Any(form => bare.StartsWith(form, StringComparison.Ordinal)))
        {
            return WaitState.Waiting;
        }

        return bare.StartsWith(TookForm, StringComparison.Ordinal) ? WaitState.Took : WaitState.None;
    }

    /// <summary>
    /// The wait after one more console line: a waiting line (<see cref="Classify"/>) before any took line becomes the last
    /// wait, bare and cut to <see cref="VerdictLength"/> characters; a took line ends the wait for good, so its last wait is
    /// null and no later line changes it; any other line changes nothing.
    /// </summary>
    internal static (WaitState State, string? LastWait) Advance((WaitState State, string? LastWait) wait, string line)
    {
        if (wait.State == WaitState.Took)
        {
            return wait;
        }

        return Classify(line) switch
        {
            WaitState.Waiting => (WaitState.Waiting, Cut(BareGateLine(line))),
            WaitState.Took => (WaitState.Took, null),
            _ => wait,
        };
    }

    /// <summary><paramref name="line"/> without a trailing carriage return or the gate's <c>&lt;stamp&gt; gat </c> log prefix.</summary>
    private static string BareGateLine(string line)
    {
        string trimmed = line.TrimEnd('\r');
        int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 && trimmed.AsSpan(space + 1).StartsWith(StampedTag, StringComparison.Ordinal)
            ? trimmed[(space + 1 + StampedTag.Length)..]
            : trimmed;
    }

    private static string Cut(string line) => line.Length <= VerdictLength ? line : line[..VerdictLength];

    /// <summary>The last of <paramref name="lines"/> that starts with one of <paramref name="forms"/>, or null when none does.</summary>
    private static string? LastLineStartingWith(string[] lines, string[] forms) =>
        lines.LastOrDefault(line => forms.Any(form => line.StartsWith(form, StringComparison.Ordinal)));

    /// <summary>
    /// <paramref name="request"/> run through the wrapper, from the project folder, with its output captured into
    /// <see cref="CaptureLog"/>, its console watched for a gate's wait for a slot (<see cref="Watch"/>), and no stall kill:
    /// a gate waiting for a slot uses no CPU, and though it prints a waiting line and a heartbeat every 30 s, a wrapper owes
    /// the server no output. The ceiling holds, paused while the gate waits, and a wall backstop bounds the whole step.
    /// </summary>
    /// <param name="request">The step's own command.</param>
    /// <param name="slot">What <see cref="SlotToken"/> becomes: <see cref="HeavySlot"/>, or <see cref="LightSlot"/> for the Compile listing.</param>
    public ToolProcessRequest Wrap(ToolProcessRequest request, string slot)
    {
        string ceiling = ((long)request.Ceiling.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        string[] command =
        [
            .. Command.Select(element =>
                element
                    .Replace(LogToken, request.LogPath, StringComparison.Ordinal)
                    .Replace(CeilingToken, ceiling, StringComparison.Ordinal)
                    .Replace(SlotToken, slot, StringComparison.Ordinal)
            ),
        ];
        return Watch(
            request with
            {
                FileName = ResolveProgram(command[0], ProjectDir),
                Arguments = [.. command.Skip(1), ResolveProgram(request.FileName, request.WorkingDirectory), .. request.Arguments],
                WorkingDirectory = ProjectDir,
                LogPath = CaptureLog(request.LogPath),
                StallLimit = TimeSpan.MaxValue,
            }
        );
    }

    /// <summary>
    /// <paramref name="request"/> with a new <see cref="WaitWatch"/> reading each stdout and stderr line after any callback
    /// the request already has, so its ceiling does not run while the wrapper waits for a slot.
    /// </summary>
    internal static ToolProcessRequest Watch(ToolProcessRequest request)
    {
        WaitWatch watch = new();
        return request with
        {
            WaitWatch = watch,
            StandardOutputLine = Chain(request.StandardOutputLine, watch.Observe),
            StandardErrorLine = Chain(request.StandardErrorLine, watch.Observe),
        };
    }

    private static Action<string> Chain(Action<string>? first, Action<string> then) =>
        first is null
            ? then
            : line =>
            {
                first(line);
                then(line);
            };

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

/// <summary>
/// A wrapped step's wait for a slot, read from its wrapper's console lines on the output readers' threads, and the ceiling
/// that follows it: the ceiling runs from the wrapper's start, stops at its first waiting line, and starts afresh at its took
/// line. Each move of the wait and each read of it with the ceiling happen under one lock, so the check loop never sees a
/// wait without its ceiling.
/// </summary>
internal sealed class WaitWatch : IDisposable
{
    private readonly Lock _lock = new();
    private (WaitState State, string? LastWait) _wait = (WaitState.None, null);
    private LoadClock? _clock;
    private TimeSpan _ceiling;
    private CancellationToken _token;
    private LoadDeadline? _running;
    private bool _closed;

    /// <summary>Where the wait stands.</summary>
    internal WaitState State
    {
        get
        {
            lock (_lock)
            {
                return _wait.State;
            }
        }
    }

    /// <summary>
    /// Reads one console line (<see cref="PrepWrapper.Advance"/>): the first waiting line ends the ceiling, and the took line
    /// after it starts a fresh one. A took line with no waiting line before it leaves the ceiling running from the start.
    /// </summary>
    public void Observe(string line)
    {
        lock (_lock)
        {
            WaitState before = _wait.State;
            _wait = PrepWrapper.Advance(_wait, line);
            // A ceiling that has already run out stays, so the check loop still kills for it.
            if (before == WaitState.None && _wait.State == WaitState.Waiting && _running is not { Expired: true })
            {
                _running?.Dispose();
                _running = null;
            }
            else if (before == WaitState.Waiting && _wait.State == WaitState.Took)
            {
                StartCeiling();
            }
        }
    }

    /// <summary>Starts the ceiling at the wrapper's start, unless a waiting line has already come.</summary>
    /// <param name="clock">The clock the ceiling runs on.</param>
    /// <param name="ceiling">The step's ceiling of load-adjusted time.</param>
    /// <param name="token">Cancels the ceiling's token without a reason.</param>
    public void Arm(LoadClock clock, TimeSpan ceiling, CancellationToken token)
    {
        lock (_lock)
        {
            (_clock, _ceiling, _token) = (clock, ceiling, token);
            if (_wait.State != WaitState.Waiting)
            {
                StartCeiling();
            }
        }
    }

    /// <summary>
    /// The kill the ceiling or <paramref name="backstop"/> calls for, and the last waiting line when the wrapper still waits
    /// for a slot, read together.
    /// </summary>
    /// <param name="backstop">The deadline that ends <see cref="LoadClock.BackstopFactor"/> times the ceiling in wall time after the start.</param>
    public (KillReason Reason, string? Detail, string? LastWait) Verdict(LoadDeadline backstop)
    {
        lock (_lock)
        {
            (KillReason reason, string? detail) =
                _running is { Expired: true } running ? ToolProcess.DeadlineKill(running)
                : backstop.Expired ? (KillReason.Backstop, ToolProcess.BackstopDetail(backstop, _ceiling))
                : (KillReason.None, null);
            return (reason, detail, reason == KillReason.None ? null : _wait.LastWait);
        }
    }

    /// <summary>Ends the ceiling; lines read after it move the wait but start no ceiling.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _closed = true;
            _running?.Dispose();
            _running = null;
        }
    }

    /// <summary>Starts a ceiling unless one that ran out before the wait is still there for the check loop to kill for.</summary>
    private void StartCeiling()
    {
        if (_clock is not null && !_closed && _running is null)
        {
            _running = _clock.Start(_ceiling, _token);
        }
    }
}

/// <summary>Where a wrapped step stands in its wrapper's wait for a slot, as its console lines say (<see cref="PrepWrapper.Classify"/>).</summary>
internal enum WaitState
{
    /// <summary>No waiting line yet: the wrapper got its slot at once, is no gate, or has not said.</summary>
    None,

    /// <summary>The wrapper said it waits for a slot and has not said it took one.</summary>
    Waiting,

    /// <summary>The wrapper said it took its slot: the wrapped command runs.</summary>
    Took,
}
