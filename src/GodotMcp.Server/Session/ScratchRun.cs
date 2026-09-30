using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GodotMcp.Server.Session;

/// <summary>
/// A scratch scene to play: its name (the file's, without .tscn), its res:// path, its pace in seconds, the user arguments it
/// starts with, and the reason the profile knows it to fail, if any.
/// </summary>
internal sealed record ScratchScenePlan(string Name, string ResPath, double Pace, IReadOnlyList<string> UserArgs)
{
    public string? Known { get; init; }
}

/// <summary>
/// A checked run_scratches call: the project, its scenes in order, the patterns, whether to prepare and list every step, and
/// how many scenes play at once.
/// </summary>
internal sealed record ScratchPlan(
    string ProjectDir,
    IReadOnlyList<ScratchScenePlan> Scenes,
    IReadOnlyList<Regex> Patterns,
    bool Prepare,
    bool Details
)
{
    public required int Parallel { get; init; }
}

/// <summary>A line number in each of a session's stdout and stderr: the edge of a step's window.</summary>
internal readonly record struct LineMark(long Stdout, long Stderr);

/// <summary>
/// Plays one scratch scene in a headless game session of its own, as an agent would with the runtime tools: launches it,
/// asks the game for its current scene's path, checks that root for the scratch protocol, reads its step names, then per step
/// marks the error feed, calls PlayStep, waits the pace in game time and reads GetStatus, stopping at the first failed step;
/// after the last step it waits one more pace, then stops the game gracefully and reads the lines it printed after the steps.
/// Each window of output (the launch, each step, the pace after the last) ends at a marker line the game prints to stdout
/// and to stderr, since the two streams arrive apart. The session stays in the registry, stopped, so get_debug_output can
/// read it.
/// </summary>
internal sealed class ScratchRun
{
    /// <summary>The scratch protocol: the methods a scratch scene's root implements.</summary>
    public static readonly IReadOnlyList<string> Protocol = ["PlayStep", "GetStepCount", "GetStepName", "GetStatus"];

    /// <summary>The start of every marker line the runner has the game print; such lines are never judged or listed.</summary>
    internal const string MarkerPrefix = "[godot-mcp] scratch ";

    private const string CurrentSceneScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\tvar scene: Node = scene_tree.current_scene\n"
        + "\treturn \"\" if scene == null else str(scene.get_path())\n";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReplyAllowance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StepAllowance = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MarkerPoll = TimeSpan.FromMilliseconds(10);
    private static readonly LineMark EveryLine = new(long.MaxValue, long.MaxValue);

    private readonly SessionRegistry _registry;
    private readonly ScratchPlan _plan;
    private readonly ScratchScenePlan _scene;
    private readonly string _nonce = Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _names = [];
    private readonly List<ScratchStep> _steps = [];
    private GodotSession? _session;
    private LoadDeadline? _deadline;
    private LineMark _mark;
    private int _inFlight = -1;
    private string _rootPath = string.Empty;
    private List<string> _launchLines = [];
    private List<string> _paceLines = [];
    private string? _afterError;
    private long _feedMark;

    private ScratchRun(SessionRegistry registry, ScratchPlan plan, ScratchScenePlan scene)
    {
        _registry = registry;
        _plan = plan;
        _scene = scene;
    }

    /// <summary>
    /// Prepares the project once when the plan says so, then plays the scenes, starting them in order at most the plan's
    /// parallel at once, and judges each; a scene red or killed beside others is then played once more alone, one at a time in
    /// order. The result lists the scenes in the plan's order, whatever finished first.
    /// </summary>
    /// <exception cref="SessionException">The prep failed.</exception>
    public static async Task<ScratchRunResult> RunAsync(SessionRegistry registry, ScratchPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Prepare)
        {
            await registry.PrepareFolderAsync(plan.ProjectDir, cancellationToken);
        }

        ScratchSceneResult[] scenes = await PlayBesideAsync(registry, plan, cancellationToken);
        for (int index = 0; index < scenes.Length; index++)
        {
            if (ScratchVerdict.PlaysAgainAlone(scenes[index], plan.Parallel))
            {
                ScratchSceneResult replay = await PlayOneAsync(registry, plan, plan.Scenes[index], cancellationToken);
                scenes[index] = ScratchVerdict.Alone(scenes[index], replay);
            }
        }

        return ScratchVerdict.Summarise(scenes);
    }

    /// <summary>
    /// Plays every scene, each started once a slot is free, in the plan's order; waits for every scene started, even when a
    /// scene throws or the call is cancelled, so no game outlives the call.
    /// </summary>
    private static async Task<ScratchSceneResult[]> PlayBesideAsync(SessionRegistry registry, ScratchPlan plan, CancellationToken cancellationToken)
    {
        var results = new ScratchSceneResult[plan.Scenes.Count];
        using SemaphoreSlim slots = new(plan.Parallel, plan.Parallel);
        List<Task> playing = [];
        try
        {
            for (int index = 0; index < plan.Scenes.Count; index++)
            {
                await slots.WaitAsync(cancellationToken);
                playing.Add(PlayInSlotAsync(index));
            }
        }
        finally
        {
            await Task.WhenAll(playing).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await Task.WhenAll(playing);
        return results;

        async Task PlayInSlotAsync(int index)
        {
            try
            {
                results[index] = await PlayOneAsync(registry, plan, plan.Scenes[index], cancellationToken);
            }
            finally
            {
                slots.Release();
            }
        }
    }

    private static async Task<ScratchSceneResult> PlayOneAsync(
        SessionRegistry registry,
        ScratchPlan plan,
        ScratchScenePlan scene,
        CancellationToken cancellationToken
    )
    {
        ScratchObservation seen = await new ScratchRun(registry, plan, scene).PlayAsync(cancellationToken);
        return ScratchVerdict.Judge(seen, new ScratchRules(plan.Patterns, scene.Known, plan.Details));
    }

    /// <summary>
    /// The ceiling of a scene's steps, load-adjusted: each step's pace and 10 s, the pace after the last, and 10 s more for the
    /// launch and pace markers.
    /// </summary>
    internal static TimeSpan Ceiling(int steps, double pace) =>
        TimeSpan.FromSeconds((steps * (pace + StepAllowance.TotalSeconds)) + pace + StepAllowance.TotalSeconds);

    /// <summary>A pace as the game milliseconds a step waits: at least 1.</summary>
    internal static int GameMs(double pace) => (int)Math.Max(1, Math.Round(pace * 1000));

    /// <summary>The note line of GetStatus's text, its second line; empty when it has none.</summary>
    internal static string StatusLine(JsonNode? status)
    {
        string text = status is JsonValue value && value.TryGetValue(out string? held) ? held : string.Empty;
        string[] lines = text.Split('\n');
        return lines.Length > 1 ? lines[1].TrimEnd('\r') : string.Empty;
    }

    /// <summary>
    /// The line numbers of <paramref name="marker"/> in each stream after <paramref name="after"/>, or null until both streams
    /// hold it: a line a step printed to one stream can still be on its way when the other stream's marker has arrived.
    /// </summary>
    internal static LineMark? Markers(OutputPage stdout, OutputPage stderr, LineMark after, string marker) =>
        MarkerLine(stdout, after.Stdout, marker) is { } outLine && MarkerLine(stderr, after.Stderr, marker) is { } errLine
            ? new LineMark(outLine, errLine)
            : null;

    /// <summary>
    /// The stdout lines, then the stderr lines, numbered after <paramref name="after"/> and before <paramref name="before"/>,
    /// each stream's own, without the marker lines of the run whose nonce is <paramref name="nonce"/>.
    /// </summary>
    internal static List<string> Window(OutputPage stdout, OutputPage stderr, LineMark after, LineMark before, string nonce) =>
        [
            .. Between(stdout, after.Stdout, before.Stdout).Where(line => !IsMarker(line, nonce)),
            .. Between(stderr, after.Stderr, before.Stderr).Where(line => !IsMarker(line, nonce)),
        ];

    /// <summary>
    /// How many lines of the window after <paramref name="after"/> and before <paramref name="before"/> the pages no longer
    /// hold: the output buffer keeps its newest lines only, and a page holds the whole buffer.
    /// </summary>
    internal static long Dropped(OutputPage stdout, OutputPage stderr, LineMark after, LineMark before) =>
        DroppedFrom(stdout, after.Stdout, before.Stdout) + DroppedFrom(stderr, after.Stderr, before.Stderr);

    /// <summary>
    /// The feed's errors after <paramref name="mark"/>, which the window takes, moving the mark past the last of them, so every
    /// error lands in exactly one window however late the bridge flushes it.
    /// </summary>
    internal static IReadOnlyList<ErrorEntry> TakeErrors(ErrorFeed feed, ref long mark)
    {
        IReadOnlyList<ErrorEntry> errors = feed.ErrorsSince(mark);
        mark = errors.Count == 0 ? mark : Math.Max(mark, errors.Max(entry => entry.Seq));
        return errors;
    }

    /// <summary>Why a window's markers did not both arrive in time; a stdout marker missing beside stderr's points at buffering.</summary>
    internal static string MarkerTimeout(string label, bool stdoutArrived, bool stderrArrived)
    {
        string text = $"the {label} end marker reached stdout and stderr not both within {StepAllowance.TotalSeconds:0} s";
        return !stdoutArrived && stderrArrived
            ? text + "; stdout may be buffered: is application/run/flush_stdout_on_print.debug off in project.godot?"
            : text;
    }

    private static bool IsMarker(string line, string nonce) =>
        line.StartsWith(MarkerPrefix, StringComparison.Ordinal) && line.EndsWith($" end {nonce}", StringComparison.Ordinal);

    private static long DroppedFrom(OutputPage page, long after, long before) =>
        page.FirstLine is { } first ? Math.Max(0, Math.Min(first, before) - (after + 1)) : 0;

    private static long? MarkerLine(OutputPage page, long after, string marker)
    {
        for (int index = 0; page.FirstLine is { } first && index < page.Lines.Count; index++)
        {
            if (first + index > after && string.Equals(page.Lines[index], marker, StringComparison.Ordinal))
            {
                return first + index;
            }
        }

        return null;
    }

    private static IEnumerable<string> Between(OutputPage page, long after, long before) =>
        page
            .Lines.Select((line, index) => (Line: line, Number: (page.FirstLine ?? 0) + index))
            .Where(numbered => numbered.Number > after && numbered.Number < before)
            .Select(numbered => numbered.Line);

    private async Task<ScratchObservation> PlayAsync(CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            ScratchObservation seen = await StartAndWalkAsync(cancellationToken);
            return seen with { Seconds = Stopwatch.GetElapsedTime(started).TotalSeconds };
        }
        finally
        {
            _deadline?.Dispose();
            if (_session is { HasGame: true } live)
            {
                await live.StopAsync(CancellationToken.None);
            }
        }
    }

    private async Task<ScratchObservation> StartAndWalkAsync(CancellationToken cancellationToken)
    {
        GodotSession session;
        try
        {
            session = await _registry.ReserveScratchAsync(_plan.ProjectDir, _scene.Name, _scene.ResPath);
            _session = session;
            await session.LaunchAsync(LaunchRequestFor(session), cancellationToken);
        }
        catch (SessionException e)
        {
            string name = _session?.Name ?? SessionRegistry.ScratchName(SessionRegistry.NameFor(null, _plan.ProjectDir), _scene.Name);
            return Seen(name) with { Refusal = $"{_scene.ResPath} did not start: {e.Message}" };
        }

        ScratchObservation seen = await WalkAsync(session, cancellationToken);
        StopResult stopped = await session.StopAsync(CancellationToken.None);
        (OutputPage stdout, OutputPage stderr, _) = Output(session);
        return seen with
        {
            ExitCode = stopped.GameExitCode ?? stopped.ExitCode,
            StopKillReason = stopped.Killed ? stopped.KillReason ?? "the stop had to kill the game" : null,
            StopWarning = stopped.Warning,
            ExitLines = [.. _paceLines, .. Window(stdout, stderr, _mark, EveryLine, _nonce)],
        };
    }

    private LaunchRequest LaunchRequestFor(GodotSession session) =>
        new(_plan.ProjectDir, _scene.ResPath, ["--headless"], _scene.UserArgs, session.Quiet, session.ShutOutRealGamepads, Prepare: false)
        {
            Mute = session.Mute,
        };

    /// <summary>The walk, or the scene killed with the step in flight when its ceiling passes or the game stops answering.</summary>
    private async Task<ScratchObservation> WalkAsync(GodotSession session, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAndPlayAsync(session, cancellationToken);
        }
        catch (Exception e) when (e is TimeoutException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            string name = _inFlight >= 0 && _inFlight < _names.Count ? _names[_inFlight] : string.Empty;
            return Seen(session.Name) with { Kill = new ScratchFailure(_inFlight, name, KillReason(e), string.Empty) };
        }
    }

    private async Task<ScratchObservation> ReadAndPlayAsync(GodotSession session, CancellationToken cancellationToken)
    {
        string? refusal =
            await FindRootAsync(session, cancellationToken)
            ?? await CheckProtocolAsync(session, cancellationToken)
            ?? await ReadStepsAsync(session, cancellationToken);
        if (refusal is not null)
        {
            return Seen(session.Name) with { Refusal = refusal };
        }

        if (_names.Count > 0)
        {
            _deadline = _registry.Clock.Start(Ceiling(_names.Count, _scene.Pace), cancellationToken);
            await PlayStepsAsync(session, _deadline.Token);
        }

        return Seen(session.Name);
    }

    /// <summary>Null once the game has named its current scene's path, which every call then addresses; else why not.</summary>
    private async Task<string?> FindRootAsync(GodotSession session, CancellationToken cancellationToken)
    {
        (JsonNode? path, string? error) = await ScriptAsync(session, CurrentSceneScript, cancellationToken);
        if (error is not null)
        {
            return $"reading the current scene failed: {error}";
        }

        _rootPath = path is JsonValue value && value.TryGetValue(out string? text) ? text : string.Empty;
        return _rootPath.Length > 0 ? null : $"{_scene.ResPath} has no current scene once started; is it a scene file the game can run?";
    }

    /// <summary>Null when the scene's root has every protocol method; else why not, naming the root and what it lacks.</summary>
    private async Task<string?> CheckProtocolAsync(GodotSession session, CancellationToken cancellationToken)
    {
        List<string> missing = [];
        foreach (string method in Protocol)
        {
            (JsonNode? has, string? error) = await CallAsync(session, "has_method", [method], cancellationToken);
            if (error is not null)
            {
                return error;
            }

            if (has is not JsonValue value || !value.TryGetValue(out bool found) || !found)
            {
                missing.Add(method);
            }
        }

        return missing.Count == 0
            ? null
            : $"the scene root {_rootPath} lacks {string.Join(", ", missing)}; a scratch scene implements PlayStep(int), GetStepCount(), "
                + "GetStepName(int) and GetStatus()";
    }

    /// <summary>Reads the step count and every step's name, then closes the launch window: its lines are never judged.</summary>
    private async Task<string?> ReadStepsAsync(GodotSession session, CancellationToken cancellationToken)
    {
        (JsonNode? count, string? error) = await CallAsync(session, "GetStepCount", [], cancellationToken);
        int total = error is null ? WholeNumber(count) : 0;
        if (error is not null || total < 0)
        {
            return error ?? $"GetStepCount() returned {count?.ToJsonString() ?? "null"}, not a whole number of 0 or more";
        }

        error = await ReadNamesAsync(session, total, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        (_launchLines, _, error) = await CloseWindowAsync(session, "launch", cancellationToken);
        _feedMark = session.Errors.Mark();
        return error;
    }

    private async Task<string?> ReadNamesAsync(GodotSession session, int total, CancellationToken cancellationToken)
    {
        for (int index = 0; index < total; index++)
        {
            (JsonNode? name, string? error) = await CallAsync(session, "GetStepName", [index], cancellationToken);
            if (error is not null)
            {
                return error;
            }

            _names.Add(StepName(name));
        }

        return null;
    }

    private static string StepName(JsonNode? name) =>
        name is JsonValue value && value.TryGetValue(out string? text) ? text : name?.ToJsonString() ?? string.Empty;

    /// <summary>Plays each step until one fails; after the last green one, waits one more pace.</summary>
    private async Task PlayStepsAsync(GodotSession session, CancellationToken limit)
    {
        int gameMs = GameMs(_scene.Pace);
        for (int index = 0; index < _names.Count; index++)
        {
            _inFlight = index;
            ScratchStep step = await PlayStepAsync(session, index, gameMs, limit);
            _steps.Add(step);
            if (ScratchVerdict.StepError(step, _plan.Patterns) is not null)
            {
                return;
            }
        }

        _inFlight = -1;
        await PaceAfterAsync(session, gameMs, limit);
    }

    private async Task<ScratchStep> PlayStepAsync(GodotSession session, int index, int gameMs, CancellationToken limit)
    {
        (_, string? error) = await CallAsync(session, "PlayStep", [index], limit);
        error ??= await WaitAsync(session, gameMs, limit);
        (JsonNode? status, string? statusError) = await CallAsync(session, "GetStatus", [], limit);
        (List<string> lines, long dropped, string? markerError) = await CloseWindowAsync(session, $"step {index}", limit);
        return new ScratchStep(index, _names[index], gameMs)
        {
            CallError = error ?? statusError ?? markerError,
            Status = StatusLine(status),
            Errors = TakeErrors(session.Errors, ref _feedMark),
            Lines = lines,
            Dropped = dropped,
        };
    }

    /// <summary>The pace after the last step: an error in the feed during it, or a wait not met, fails the scene's exit.</summary>
    private async Task PaceAfterAsync(GodotSession session, int gameMs, CancellationToken limit)
    {
        string? waitError = await WaitAsync(session, gameMs, limit);
        (_paceLines, _, string? markerError) = await CloseWindowAsync(session, "pace", limit);
        _afterError = ScratchVerdict.FeedError(TakeErrors(session.Errors, ref _feedMark)) ?? waitError ?? markerError;
    }

    /// <summary>
    /// Has the game print a marker line to stdout and to stderr, waits until both have arrived, and returns the lines of the
    /// window they close, the marker lines left out, and how many of its lines the output buffer no longer held; the next
    /// window starts after the markers. When the markers fail or do not arrive in time, the window takes every line so far,
    /// with why.
    /// </summary>
    private async Task<(List<string> Lines, long Dropped, string? Error)> CloseWindowAsync(
        GodotSession session,
        string label,
        CancellationToken limit
    )
    {
        string marker = $"{MarkerPrefix}{label} end {_nonce}";
        (_, string? error) = await ScriptAsync(session, MarkerScript(marker), limit);
        LineMark? found = error is null ? await WaitForMarkersAsync(session, marker, limit) : null;
        (OutputPage stdout, OutputPage stderr, LineMark total) = Output(session);
        LineMark after = _mark;
        List<string> lines = Window(stdout, stderr, after, found ?? EveryLine, _nonce);
        long dropped = Dropped(stdout, stderr, after, found ?? EveryLine);
        _mark = found ?? total;
        if (error is not null)
        {
            return (lines, dropped, $"the {label} end marker failed: {error}");
        }

        return found is null
            ? (
                lines,
                dropped,
                MarkerTimeout(label, MarkerLine(stdout, after.Stdout, marker) is not null, MarkerLine(stderr, after.Stderr, marker) is not null)
            )
            : (lines, dropped, null);
    }

    private async Task<LineMark?> WaitForMarkersAsync(GodotSession session, string marker, CancellationToken limit)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            (OutputPage stdout, OutputPage stderr, _) = Output(session);
            if (Markers(stdout, stderr, _mark, marker) is { } found)
            {
                return found;
            }

            if (Stopwatch.GetElapsedTime(started) > StepAllowance)
            {
                return null;
            }

            await Task.Delay(MarkerPoll, limit);
        }
    }

    private static string MarkerScript(string marker) =>
        $"extends RefCounted\n\n\nfunc execute(_scene_tree: SceneTree) -> Variant:\n\tprint(\"{marker}\")\n\tprinterr(\"{marker}\")\n\treturn null\n";

    /// <summary>call_method on the scene's root: its value, or why the call failed. A timeout or the ceiling is left to throw.</summary>
    private async Task<(JsonNode? Value, string? Error)> CallAsync(GodotSession session, string method, JsonArray args, CancellationToken limit)
    {
        JsonObject parameters = new()
        {
            ["node"] = _rootPath,
            ["method"] = method,
            ["args"] = args,
        };
        (JsonNode? value, string? error) = await SendAsync(session, "call_method", parameters, limit);
        return (value, error is null ? null : $"{method} failed: {error}");
    }

    /// <summary>run_script with <paramref name="source"/>: its execute's value, or why it failed.</summary>
    private static Task<(JsonNode? Value, string? Error)> ScriptAsync(GodotSession session, string source, CancellationToken limit) =>
        SendAsync(session, "run_script", new JsonObject { ["source"] = source }, limit);

    /// <summary>A bridge command's value, or why it failed. A timeout or the ceiling is left to throw.</summary>
    private static async Task<(JsonNode? Value, string? Error)> SendAsync(
        GodotSession session,
        string command,
        JsonObject parameters,
        CancellationToken limit
    )
    {
        try
        {
            JsonNode? reply = await session.SendAsync(command, parameters, CallTimeout + ReplyAllowance, limit, CallTimeout);
            return (reply?["value"]?.DeepClone(), null);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or SessionException)
        {
            return (null, e.Message);
        }
    }

    /// <summary>wait_for {gameMs}: null once the game's clock has run that long; else why it did not.</summary>
    private static async Task<string?> WaitAsync(GodotSession session, int gameMs, CancellationToken limit)
    {
        TimeSpan release = TimeSpan.FromMilliseconds(gameMs) + StepAllowance;
        JsonObject parameters = new()
        {
            ["gameMs"] = gameMs,
            ["kind"] = "gameMs",
            ["timeoutMs"] = (int)release.TotalMilliseconds,
        };
        try
        {
            JsonNode? reply = await session.SendAsync("wait_for", parameters, release + ReplyAllowance, limit, release);
            return reply?["met"] is JsonValue met && met.TryGetValue(out bool isMet) && !isMet
                ? $"wait_for {gameMs} game ms was not met within {release.TotalSeconds:0.#} s: the game's clock stood still (is it paused?)"
                : null;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or SessionException)
        {
            return $"wait_for {gameMs} game ms failed: {e.Message}";
        }
    }

    private string KillReason(Exception e)
    {
        if (e is TimeoutException)
        {
            return e.Message;
        }

        string backstop = _deadline?.Reason == DeadlineReason.Backstop ? _deadline.BackstopClause() : string.Empty;
        string seconds = Ceiling(_names.Count, _scene.Pace).TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        return $"the scene passed its ceiling of {seconds} s of load-adjusted time "
            + $"({_names.Count} steps x (pace + 10 s), one more pace, and 10 s for the markers)"
            + $"{backstop}, so its game was stopped";
    }

    private ScratchObservation Seen(string session) =>
        new(_scene.Name, session, _scene.Pace, _names.Count)
        {
            LaunchLines = _launchLines,
            Steps = _steps,
            AfterError = _afterError,
        };

    private static int WholeNumber(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double number) && number >= 0 && number <= int.MaxValue && number == Math.Floor(number)
            ? (int)number
            : -1;

    /// <summary>The newest lines of each stream, and the number of each stream's last line.</summary>
    private static (OutputPage Stdout, OutputPage Stderr, LineMark Total) Output(GodotSession session)
    {
        DebugOutput output = session.GetDebugOutput(GodotRun.OutputCapacity, before: null);
        return (
            new OutputPage(output.StdoutFirstLine, output.Stdout),
            new OutputPage(output.StderrFirstLine, output.Stderr),
            new LineMark(output.StdoutTotalLines, output.StderrTotalLines)
        );
    }
}
