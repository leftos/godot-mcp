using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GodotMcp.Server.Tools;

namespace GodotMcp.Server.Session;

/// <summary>
/// One step as the runner played it: its index and name, its pace in game milliseconds, the error of a call that failed
/// (PlayStep, the wait, GetStepUntil or GetStatus, or the until's), the second line of GetStatus, the error feed's entries since
/// the step's mark, and every stdout then stderr line of the step's window.
/// </summary>
internal sealed record ScratchStep(int Index, string Name, int GameMs)
{
    /// <summary>The figures of the wait_for the step played inside, for a step whose GetStepUntil gave a condition; else null.</summary>
    public ScratchUntil? Until { get; init; }

    public string? CallError { get; init; }

    /// <summary>
    /// The feed entry CallError was read from (an until's wait the bridge refused with an error the feed also logged), so the step's
    /// error is that entry cut to each budget with its file and line kept; else null.
    /// </summary>
    public ErrorEntry? CallErrorEntry { get; init; }

    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<ErrorEntry> Errors { get; init; } = [];

    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>How many of the window's lines the output buffer no longer held when the window closed.</summary>
    public long Dropped { get; init; }
}

/// <summary>
/// What the runner saw of one scene: the lines printed before the first step, never judged, and the error feed's entries from
/// the launch to the first step, which are; the steps it played, and at most one of: a refusal before any step (the scene did
/// not start, has no current scene, its root lacks the scratch protocol, a pace.steps key names no single step, or an
/// options.steps token names no step), or the step in flight when it was killed (-1
/// outside a step) with the reason; then why the pace after the last step failed (an error in the feed, or a wait not met),
/// the game's exit code, the stop's kill and warning, and the lines printed after the last step's window, the stop included.
/// </summary>
internal sealed record ScratchObservation(string Scene, string Session, double Pace, int Total)
{
    public IReadOnlyList<string> LaunchLines { get; init; } = [];

    /// <summary>The error feed's entries from the launch to the first step; an error among them fails the scene at the boot.</summary>
    public IReadOnlyList<ErrorEntry> BootErrors { get; init; } = [];

    public IReadOnlyList<ScratchStep> Steps { get; init; } = [];

    /// <summary>
    /// Each step's reason for its pace, by index: its own step pace's (null for a bare number), else the scene's; empty before
    /// the steps are named.
    /// </summary>
    public IReadOnlyList<string?> StepPaceReasons { get; init; } = [];

    /// <summary>
    /// The indices options.steps selected, ascending; null when the call gave no steps or the scene was refused before its steps
    /// were resolved (a pace.steps key or an options.steps token naming no step included).
    /// </summary>
    public IReadOnlyList<int>? Selected { get; init; }

    /// <summary>
    /// The setup steps the profile declares for the scene that this run added to options.steps' selection, ascending; null when it
    /// added none. Fixed when the steps resolve, as <see cref="Selected"/> is, so a scene stopped at a step, at the boot or by a
    /// kill still lists them.
    /// </summary>
    public IReadOnlyList<int>? Setup { get; init; }

    /// <summary>The time scale the scene's game ran at when it differs from the call's (its profile pace opted out); else null.</summary>
    public double? TimeScale { get; init; }

    public string? Refusal { get; init; }

    public ScratchFailure? Kill { get; init; }

    public string? AfterError { get; init; }

    public int? ExitCode { get; init; }

    public string? StopKillReason { get; init; }

    public string? StopWarning { get; init; }

    public IReadOnlyList<string> ExitLines { get; init; } = [];

    public double Seconds { get; init; }
}

/// <summary>
/// How a scene is judged: the patterns that fail a step's line, its known reason, the profile's reason for its pace, and whether
/// its steps are listed when green and every listed step carries its detail.
/// </summary>
internal sealed record ScratchRules(IReadOnlyList<ScratchPattern> Patterns, string? Known, string? PaceReason, bool Details);

/// <summary>
/// run_scratches' result: whether every scene passed, the count of each verdict, what the prep did, one entry a scene, and the
/// call's time scale when it is not 1.
/// </summary>
internal sealed record ScratchRunResult(
    bool Passed,
    int Green,
    int Red,
    int Known,
    int NoSteps,
    int Killed,
    PrepResult Prep,
    IReadOnlyList<ScratchSceneResult> Scenes
)
{
    /// <summary>options.timeScale, the time scale the scenes' games ran at; null at 1.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TimeScale { get; init; }
}

/// <summary>A scene's steps played and in all.</summary>
internal sealed record ScratchStepCount(int Played, int Total);

/// <summary>A scratch pattern a failing line matched: the regex as the profile wrote it, and the profile's reason for it, if any.</summary>
internal sealed record ScratchMatch(string Pattern, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason);

/// <summary>The first step that failed, or the one in flight when the scene was killed (index -1 before any step), and why.</summary>
internal sealed record ScratchFailure(int Index, string Name, string Error, string Status)
{
    /// <summary>The pattern the step's first failing line matched, with the profile's reason; null when the failure was not a line match.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScratchMatch? Pattern { get; init; }
}

/// <summary>An error feed entry as a step lists it, standing for every identical one (same message, file and line).</summary>
internal sealed record ScratchError(string Message, string File, int Line)
{
    /// <summary>How many identical entries the step logged; null for one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; init; }
}

/// <summary>
/// One step as a red or killed scene, or options.details, lists it: its status line always, with its pace in game milliseconds,
/// or for a step played inside a wait on its until that wait's figures; its detail (error, errors, lines) when it failed or
/// options.details asked for every step's, cut to fit a budget when it ran over.
/// </summary>
internal sealed record ScratchStepResult(
    int Index,
    string Name,
    bool Ok,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? GameMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScratchUntil? Until,
    string Status
)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ScratchError>? Errors { get; init; }

    /// <summary>How many distinct errors did not fit in <see cref="Errors"/>; null when all did.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ErrorsOmitted { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Lines { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>Why the detail lost lines or errors from its end, and where the full log is; null when it fit.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cut { get; init; }
}

/// <summary>
/// A step's until wait as it ended: whether its condition was met, the game milliseconds the bridge counted from PlayStep's frame
/// to the met or limit frame, and the real milliseconds and frames it took.
/// </summary>
internal sealed record ScratchUntil(bool Met, int GameMs, int ElapsedMs, int Frames);

/// <summary>A step that failed: its index, its name and why.</summary>
internal sealed record ScratchStepFailure(int Index, string Name, string Error);

/// <summary>
/// How the game ended: its exit code, the objects it leaked, the lines after the last step that matched a pattern, why the
/// pace after the last step failed, and whether the stop had to kill the game, why, and the stop's warning.
/// </summary>
internal sealed record ScratchExit(int? Code)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Leaked { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Lines { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Killed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KillReason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Warning { get; init; }
}

/// <summary>One scene's verdict and what it rests on.</summary>
internal sealed record ScratchSceneResult(string Scene, string Verdict, ScratchStepCount Steps, double Pace, double Seconds, string Session)
{
    /// <summary>
    /// The indices options.steps selected and the scene played from, ascending; null when the call gave no steps or the scene
    /// was refused before its steps were resolved (a pace.steps key or an options.steps token naming no step included).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? Selected { get; init; }

    /// <summary>
    /// The setup steps the profile declares for the scene that its partial run added to options.steps' selection, ascending; null
    /// when it added none. Fixed when the steps resolve, as <see cref="Selected"/> is, so a scene stopped at a step, at the boot or
    /// by a kill still lists them.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? Setup { get; init; }

    /// <summary>
    /// The time scale the scene's game ran at when it differs from the call's, as when its profile pace has "timeScale": 1;
    /// null otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TimeScale { get; init; }

    /// <summary>The profile's reason for the scene's pace, when the scene came out red or killed; null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PaceReason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScratchFailure? FailedAt { get; init; }

    /// <summary>Every step played that failed, in order; null when none did.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ScratchStepFailure>? Failures { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ScratchStepResult>? Details { get; init; }

    public required ScratchExit Exit { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Known { get; init; }

    /// <summary>
    /// Whether the scene, red or killed beside others, passed when played again alone; null when it played once.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Alone { get; init; }

    /// <summary>The step the replay alone failed at, when it failed again and named one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScratchFailure? AloneFailedAt { get; init; }
}

/// <summary>
/// Turns what the runner saw of a scene into its verdict, with no Godot: green when every step played with no call error, no
/// error in the feed and no line matching a pattern, and the game exited 0 with no leak and no matching line after; red
/// otherwise; no-steps, killed, and known or known-now-green for a scene the profile knows to fail.
/// </summary>
internal static partial class ScratchVerdict
{
    public const string Green = "green";
    public const string Red = "red";
    public const string NoSteps = "no-steps";
    public const string Killed = "killed";
    public const string KnownRed = "known";
    public const string KnownNowGreen = "known-now-green";

    /// <summary>The name failedAt gives an error the feed logged between the launch and the first step.</summary>
    public const string BootStep = "boot";

    /// <summary>
    /// The most lines a step lists with options.details, and exit.lines; the rest are counted in one entry after them, "… N more".
    /// </summary>
    public const int MaxLines = 20;

    /// <summary>
    /// The most characters kept of a single-string copy of an error (failedAt.error, failures[].error with options.details, a step's error and
    /// exit.error, a feed error's location kept after it), of a step's status and of each of its lines.
    /// </summary>
    public const int MaxCopyChars = 500;

    /// <summary>
    /// The most characters failures[].error keeps without options.details: failures index the steps, whose own error carries
    /// the message at <see cref="MaxCopyChars"/>.
    /// </summary>
    public const int MaxFailureIndexChars = 200;

    /// <summary>
    /// The most characters a step's detail (its error, errors and lines, serialised) takes with options.details; past it, lines
    /// then errors are dropped from the end.
    /// </summary>
    public const int MaxStepDetailChars = 4000;

    /// <summary>The most distinct errors a failing step lists without options.details; the rest are counted in errorsOmitted.</summary>
    public const int MaxErrorsCompact = 3;

    /// <summary>The most lines a failing step lists without options.details; the rest are counted in one entry, "… N more".</summary>
    public const int MaxLinesCompact = 5;

    /// <summary>The most characters a failing step's detail takes without options.details, as <see cref="MaxStepDetailChars"/>.</summary>
    public const int MaxStepDetailCharsCompact = 1500;

    /// <summary>The detail every step gets with options.details.</summary>
    private static readonly DetailLimits Full = new(ErrorReport.MaxPerResult, ErrorReport.MaxMessageLength, MaxLines, MaxStepDetailChars);

    /// <summary>The detail a failing step gets without options.details.</summary>
    private static readonly DetailLimits Compact = new(MaxErrorsCompact, MaxCopyChars, MaxLinesCompact, MaxStepDetailCharsCompact);

    private const string ScratchNotePrefix = "[scratch]";

    /// <summary>
    /// Why a step failed: its call's error, else the feed's first error, else its first line a pattern matches, cut to
    /// <see cref="MaxCopyChars"/>; null if it passed.
    /// </summary>
    public static string? StepError(ScratchStep step, IReadOnlyList<ScratchPattern> patterns) => StepError(step, patterns, MaxCopyChars);

    /// <summary>As <see cref="StepError(ScratchStep, IReadOnlyList{ScratchPattern})"/>, cut to <paramref name="max"/> characters.</summary>
    private static string? StepError(ScratchStep step, IReadOnlyList<ScratchPattern> patterns, int max)
    {
        if (step.CallError is not null)
        {
            return step.CallErrorEntry is { } entry ? FeedError([entry], max)! : OutputBuffer.Cut(step.CallError, max);
        }

        return FeedError(step.Errors, max)
            ?? (
                step.Lines.Select(line => LineError(line, patterns)).FirstOrDefault(error => error is not null) is { } line
                    ? OutputBuffer.Cut(line, max)
                    : null
            );
    }

    /// <summary>
    /// The feed's first error (not warning) as a step or the exit reports it, its message cut to <see cref="MaxCopyChars"/>, with
    /// its file and line; null when none.
    /// </summary>
    public static string? FeedError(IEnumerable<ErrorEntry> entries) => FeedError(entries, MaxCopyChars);

    /// <summary>As <see cref="FeedError(IEnumerable{ErrorEntry})"/>, the message cut to <paramref name="max"/> characters.</summary>
    private static string? FeedError(IEnumerable<ErrorEntry> entries, int max) =>
        entries.FirstOrDefault(entry => entry.IsError) is { } error
            ? error.File.Length > 0
                ? $"{OutputBuffer.Cut(error.Message, max)} ({error.File}:{error.Line})"
                : OutputBuffer.Cut(error.Message, max)
            : null;

    /// <summary>The scene's verdict and result.</summary>
    public static ScratchSceneResult Judge(ScratchObservation seen, ScratchRules rules)
    {
        (string verdict, ScratchFailure? failedAt) = Underlying(seen, rules.Patterns);
        string shown = Known(verdict, failedAt, rules.Known, PlayedInPart(seen));
        return new ScratchSceneResult(
            seen.Scene,
            shown,
            new ScratchStepCount(seen.Steps.Count, seen.Total),
            seen.Pace,
            Math.Round(seen.Seconds, 1),
            seen.Session
        )
        {
            Selected = seen.Selected,
            Setup = seen.Setup,
            TimeScale = seen.TimeScale,
            PaceReason = shown is Red or Killed ? PaceReasonAt(seen, failedAt, rules.PaceReason) : null,
            FailedAt = failedAt,
            Failures = Failures(seen, rules.Patterns, rules.Details ? MaxCopyChars : MaxFailureIndexChars),
            Details = ListsSteps(rules.Details, verdict, failedAt) ? [.. seen.Steps.Select(step => StepResult(step, rules, seen.Session))] : null,
            Exit = ExitOf(seen, rules.Patterns),
            Known = shown is KnownRed or KnownNowGreen ? rules.Known : null,
        };
    }

    /// <summary>
    /// Every step that failed, in the order played, with why cut to <paramref name="max"/> characters, then the step a kill cut
    /// off with the kill's reason; null when none did.
    /// </summary>
    private static List<ScratchStepFailure>? Failures(ScratchObservation seen, IReadOnlyList<ScratchPattern> patterns, int max)
    {
        List<ScratchStepFailure> failures = [];
        foreach (ScratchStep step in seen.Steps)
        {
            if (StepError(step, patterns, max) is { } error)
            {
                failures.Add(new ScratchStepFailure(step.Index, step.Name, error));
            }
        }

        if (seen.Kill is { Index: >= 0 } kill)
        {
            failures.Add(new ScratchStepFailure(kill.Index, kill.Name, kill.Error));
        }

        return failures.Count > 0 ? failures : null;
    }

    /// <summary>
    /// The reason for the pace of the step a red or killed scene failed at, its own step pace's when it has one; the scene's when
    /// the failure is outside any step.
    /// </summary>
    private static string? PaceReasonAt(ScratchObservation seen, ScratchFailure? failedAt, string? sceneReason) =>
        failedAt is { Index: >= 0 } failure && failure.Index < seen.StepPaceReasons.Count ? seen.StepPaceReasons[failure.Index] : sceneReason;

    /// <summary>
    /// The run's result: each verdict counted, known-now-green among the red, the prep, and the call's
    /// <paramref name="timeScale"/> when it is not 1; it passed when none is red or killed.
    /// </summary>
    public static ScratchRunResult Summarise(IReadOnlyList<ScratchSceneResult> scenes, PrepResult prep, double timeScale)
    {
        int Count(params string[] verdicts) => scenes.Count(scene => verdicts.Contains(scene.Verdict, StringComparer.Ordinal));
        int red = Count(Red, KnownNowGreen);
        int killed = Count(Killed);
        return new ScratchRunResult(red == 0 && killed == 0, Count(Green), red, Count(KnownRed), Count(NoSteps), killed, prep, scenes)
        {
            TimeScale = timeScale == 1 ? null : timeScale,
        };
    }

    /// <summary>
    /// Whether a scene is played once more alone: it ran beside others (<paramref name="parallel"/> above 1) and came out red
    /// or killed, and it is not known. A red scene refused before any step (none played, failed at index -1: it did not start,
    /// has no current scene, lacks the scratch protocol, has a pace.steps key or an options.steps token naming no step, or
    /// logged an error at the boot) is not played again, since no game
    /// beside it explains that; a killed one is, before its first step or in the pace after its last alike, since load can kill
    /// either.
    /// </summary>
    public static bool PlaysAgainAlone(ScratchSceneResult first, int parallel) =>
        parallel > 1 && first.Verdict is Red or Killed && !RefusedBeforeAnyStep(first);

    private static bool RefusedBeforeAnyStep(ScratchSceneResult scene) =>
        scene.Verdict == Red && scene.Steps.Played == 0 && scene.FailedAt is { Index: -1 };

    /// <summary>
    /// A scene's entry after its replay alone: the replay's, marked alone, when it played green; else the first run's, marked
    /// not alone, with the step the replay failed at. The seconds are both runs'.
    /// </summary>
    public static ScratchSceneResult Alone(ScratchSceneResult first, ScratchSceneResult replay)
    {
        double seconds = Math.Round(first.Seconds + replay.Seconds, 1);
        return replay.Verdict == Green
            ? replay with
            {
                Seconds = seconds,
                Alone = true,
            }
            : first with
            {
                Seconds = seconds,
                Alone = false,
                AloneFailedAt = replay.FailedAt,
            };
    }

    /// <summary>
    /// The objects the game reported leaked at exit, "1 ObjectDB instance" or "N ObjectDB instances"; null when no line says so.
    /// </summary>
    public static string? Leaked(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            Match leak = LeakLine().Match(line);
            if (leak.Success)
            {
                string count = leak.Groups["count"].Value;
                return count == "1" ? "1 ObjectDB instance" : $"{count} ObjectDB instances";
            }
        }

        return null;
    }

    /// <summary>The verdict before the known rule, and the step it names.</summary>
    private static (string Verdict, ScratchFailure? FailedAt) Underlying(ScratchObservation seen, IReadOnlyList<ScratchPattern> patterns)
    {
        if (BeforeAnyStep(seen) is { } early)
        {
            return early;
        }

        if (seen.Total == 0)
        {
            return (NoSteps, null);
        }

        foreach (ScratchStep step in seen.Steps)
        {
            if (StepError(step, patterns) is { } error)
            {
                return (
                    Red,
                    new ScratchFailure(step.Index, step.Name, error, OutputBuffer.Cut(step.Status, MaxCopyChars))
                    {
                        Pattern = MatchedPattern(step, patterns),
                    }
                );
            }
        }

        return ExitFails(seen, patterns) ? (Red, null) : (Green, null);
    }

    /// <summary>
    /// The verdict a scene gets before its steps count, in this order: killed, refused, or red at the boot with the feed's first
    /// error; null when none applies.
    /// </summary>
    private static (string Verdict, ScratchFailure? FailedAt)? BeforeAnyStep(ScratchObservation seen)
    {
        if (seen.Kill is not null)
        {
            return (Killed, seen.Kill);
        }

        if (seen.Refusal is not null)
        {
            return (Red, new ScratchFailure(-1, string.Empty, seen.Refusal, string.Empty));
        }

        return FeedError(seen.BootErrors) is { } boot ? (Red, new ScratchFailure(-1, BootStep, boot, string.Empty)) : null;
    }

    /// <summary>
    /// Whether a scene lists its steps: with options.details, or when red or killed; never when it went red before any step played
    /// (a refusal, or an error at the boot).
    /// </summary>
    private static bool ListsSteps(bool details, string verdict, ScratchFailure? failedAt) =>
        (details || verdict is Red or Killed) && !(verdict == Red && failedAt is { Index: -1 });

    /// <summary>
    /// The verdict shown for a scene the profile knows to fail; one that failed before any step is red, never known. A green one
    /// is known-now-green only when the call selected every step: options.steps selecting fewer says nothing of the known failure,
    /// so it stays known.
    /// </summary>
    private static string Known(string verdict, ScratchFailure? failedAt, string? known, bool partial) =>
        known is null || failedAt is { Index: < 0 } ? verdict
        : verdict == Red ? KnownRed
        : verdict == Green ? (partial ? KnownRed : KnownNowGreen)
        : verdict;

    /// <summary>Whether options.steps selected fewer than all of the scene's steps.</summary>
    private static bool PlayedInPart(ScratchObservation seen) => seen.Selected is { } selected && selected.Count < seen.Total;

    /// <summary>
    /// Whether the exit turns a scene with every step green red: an error in the pace after the last step, a kill by the stop, a
    /// non-zero code, a leak, or a matching line after the steps.
    /// </summary>
    private static bool ExitFails(ScratchObservation seen, IReadOnlyList<ScratchPattern> patterns) =>
        seen.AfterError is not null
        || seen.StopKillReason is not null
        || seen.ExitCode is not (null or 0)
        || Leaked(seen.ExitLines) is not null
        || seen.ExitLines.Any(line => Matches(line, patterns));

    private static ScratchExit ExitOf(ScratchObservation seen, IReadOnlyList<ScratchPattern> patterns)
    {
        List<string> matched = [.. seen.ExitLines.Where(line => Matches(line, patterns))];
        return new ScratchExit(seen.ExitCode)
        {
            Leaked = Leaked(seen.ExitLines),
            Lines = matched.Count > 0 ? Capped(matched, MaxLines) : null,
            Error = seen.AfterError,
            Killed = seen.StopKillReason is null ? null : true,
            KillReason = seen.StopKillReason,
            Warning = seen.StopWarning,
        };
    }

    /// <summary>
    /// A step as it is listed: its status line, and its detail when it failed or options.details asks for every step's; the errors
    /// folded and the lines its matching ones and [scratch] notes, within the limits of <see cref="Full"/> detail with
    /// options.details, of <see cref="Compact"/> detail without.
    /// </summary>
    private static ScratchStepResult StepResult(ScratchStep step, ScratchRules rules, string session)
    {
        string? error = StepError(step, rules.Patterns);
        ScratchStepResult listed = new(
            step.Index,
            step.Name,
            error is null,
            step.Until is null ? step.GameMs : null,
            step.Until,
            OutputBuffer.Cut(step.Status, MaxCopyChars)
        );
        if (error is null && !rules.Details)
        {
            return listed;
        }

        DetailLimits limits = rules.Details ? Full : Compact;
        List<ErrorReport.Collapsed> folded = ErrorReport.Collapse([.. step.Errors.Where(entry => entry.IsError)]);
        return WithinBudget(
            listed with
            {
                Errors = [.. folded.Take(limits.Errors).Select(entry => Folded(entry, limits.MessageChars))],
                ErrorsOmitted = folded.Count > limits.Errors ? folded.Count - limits.Errors : null,
                Error = error,
            },
            StepLines(step, rules.Patterns),
            limits,
            session
        );
    }

    private static ScratchError Folded(ErrorReport.Collapsed error, int messageChars) =>
        new(OutputBuffer.Cut(error.First.Message, messageChars), error.First.File, error.First.Line) { Count = error.Count > 1 ? error.Count : null };

    /// <summary>A step's matching lines and [scratch] notes, each cut to <see cref="MaxCopyChars"/>, after a note of the lines it lost.</summary>
    private static List<string> StepLines(ScratchStep step, IReadOnlyList<ScratchPattern> patterns)
    {
        List<string> lines =
        [
            .. step
                .Lines.Where(line => Matches(line, patterns) || line.StartsWith(ScratchNotePrefix, StringComparison.Ordinal))
                .Select(line => OutputBuffer.Cut(line, MaxCopyChars)),
        ];
        if (step.Dropped > 0)
        {
            lines.Insert(0, DroppedNote(step.Dropped));
        }

        return lines;
    }

    /// <summary>
    /// The step with its lines, at most the limits' count then "… N more", fitted with its error and errors in the limits'
    /// characters: lines go first, then errors from the end but the first, those counted in errorsOmitted; a step that lost any
    /// carries a cut note naming the budget and the session whose log holds them.
    /// </summary>
    private static ScratchStepResult WithinBudget(ScratchStepResult step, List<string> allLines, DetailLimits limits, string session)
    {
        List<ScratchError> errors = [.. step.Errors ?? []];
        int errorSize = step.Error is null ? 0 : Serialised(step.Error);
        int shown = Math.Min(allLines.Count, limits.Lines);
        int kept = FitLines(allLines, shown, limits.Chars - errorSize - ArraySize(errors));
        List<string> lines = Capped(allLines, kept);
        int dropped = DropErrors(errors, limits.Chars - errorSize - ArraySize(lines));
        return step with
        {
            Lines = lines,
            Errors = errors,
            ErrorsOmitted = Omitted(step.ErrorsOmitted, dropped),
            Cut = kept < shown || dropped > 0 ? CutNote(limits.Chars, session) : null,
        };
    }

    /// <summary>How many of the lines, at most <paramref name="kept"/>, fit in <paramref name="room"/> characters with their "… N more".</summary>
    private static int FitLines(List<string> allLines, int kept, int room)
    {
        while (kept > 0 && ArraySize(Capped(allLines, kept)) > room)
        {
            kept--;
        }

        return kept;
    }

    /// <summary>Drops errors from the end, never the first, until they fit in <paramref name="room"/> characters; returns how many.</summary>
    private static int DropErrors(List<ScratchError> errors, int room)
    {
        int dropped = 0;
        while (errors.Count > 1 && ArraySize(errors) > room)
        {
            errors.RemoveAt(errors.Count - 1);
            dropped++;
        }

        return dropped;
    }

    private static int? Omitted(int? capped, int dropped) => (capped ?? 0) + dropped is > 0 and int omitted ? omitted : null;

    private static string CutNote(int budget, string session) =>
        string.Create(CultureInfo.InvariantCulture, $"detail over {budget} characters; get_debug_output {session} holds the full log");

    private static int ArraySize<T>(List<T> items) => 2 + items.Sum(item => Serialised(item)) + Math.Max(0, items.Count - 1);

    private static int Serialised<T>(T value) => JsonSerializer.Serialize(value, ToolJson.Options).Length;

    /// <summary>The entry a step lists first when its window lost lines to the output buffer's cap.</summary>
    internal static string DroppedNote(long dropped) =>
        string.Create(CultureInfo.InvariantCulture, $"… {dropped} earlier lines were no longer in the output buffer");

    /// <summary>The first <paramref name="max"/> lines, followed by "… N more" when there are more.</summary>
    internal static List<string> Capped(List<string> lines, int max)
    {
        if (lines.Count <= max)
        {
            return lines;
        }

        List<string> kept = lines[..max];
        kept.Add(string.Create(CultureInfo.InvariantCulture, $"… {lines.Count - max} more"));
        return kept;
    }

    /// <summary>How much detail a listed step keeps: its distinct errors, its lines and the characters of the whole.</summary>
    private sealed record DetailLimits(int Errors, int MessageChars, int Lines, int Chars);

    /// <summary>Whether a pattern matches the line; a pattern that ran past its timeout on it counts as matching.</summary>
    private static bool Matches(string line, IReadOnlyList<ScratchPattern> patterns) => LineError(line, patterns) is not null;

    /// <summary>
    /// The pattern a step's first failing line matched, with the profile's reason; null when the step failed for another reason
    /// (a call error or the feed's).
    /// </summary>
    private static ScratchMatch? MatchedPattern(ScratchStep step, IReadOnlyList<ScratchPattern> patterns) =>
        step.CallError is not null || FeedError(step.Errors) is not null ? null : FailedLine(step.Lines, patterns);

    /// <summary>The match of the first line any pattern fails, patterns tried in profile order; null when no line fails.</summary>
    private static ScratchMatch? FailedLine(IReadOnlyList<string> lines, IReadOnlyList<ScratchPattern> patterns)
    {
        foreach (string line in lines)
        {
            foreach (ScratchPattern pattern in patterns)
            {
                if (LineError(line, pattern) is not null)
                {
                    return new ScratchMatch(pattern.Regex.ToString(), pattern.Reason);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Why the line fails a step: the line itself when a pattern matches it, the pattern's refusal when it ran past its timeout
    /// on it, else null.
    /// </summary>
    private static string? LineError(string line, IReadOnlyList<ScratchPattern> patterns)
    {
        foreach (ScratchPattern pattern in patterns)
        {
            if (LineError(line, pattern) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>Why one pattern fails the line: the line itself on a match, the refusal on a timeout, else null.</summary>
    private static string? LineError(string line, ScratchPattern pattern)
    {
        try
        {
            return pattern.Regex.IsMatch(line) ? line : null;
        }
        catch (RegexMatchTimeoutException)
        {
            string seconds = pattern.Regex.MatchTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            return $"pattern '{pattern.Regex}' took over {seconds} s on a line; simplify it in scratch.patterns";
        }
    }

    [GeneratedRegex(@"(?<count>\d+) ObjectDB instances? (was|were) leaked at exit", RegexOptions.CultureInvariant)]
    private static partial Regex LeakLine();
}
