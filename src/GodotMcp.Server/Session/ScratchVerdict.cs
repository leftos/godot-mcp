using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GodotMcp.Server.Session;

/// <summary>
/// One step as the runner played it: its index and name, the game milliseconds it waited, the error of a call that failed
/// (PlayStep, the wait or GetStatus), the second line of GetStatus, the error feed's entries since the step's mark, and every
/// stdout then stderr line of the step's window.
/// </summary>
internal sealed record ScratchStep(int Index, string Name, int GameMs)
{
    public string? CallError { get; init; }

    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<ErrorEntry> Errors { get; init; } = [];

    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>How many of the window's lines the output buffer no longer held when the window closed.</summary>
    public long Dropped { get; init; }
}

/// <summary>
/// What the runner saw of one scene: the lines printed before the first step, never judged; the steps it played, and at most
/// one of: a refusal before any step (the scene did not start, has no current scene, or its root lacks the scratch protocol),
/// or the step in flight when it was killed (-1 outside a step) with the reason; then why the pace after the last step
/// failed (an error in the feed, or a wait not met), the game's exit code, the stop's kill and warning, and the lines printed
/// after the last step's window, the stop included.
/// </summary>
internal sealed record ScratchObservation(string Scene, string Session, double Pace, int Total)
{
    public IReadOnlyList<string> LaunchLines { get; init; } = [];

    public IReadOnlyList<ScratchStep> Steps { get; init; } = [];

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
/// its steps are listed when green.
/// </summary>
internal sealed record ScratchRules(IReadOnlyList<Regex> Patterns, string? Known, string? PaceReason, bool Details);

/// <summary>run_scratches' result: whether every scene passed, the count of each verdict, and one entry a scene.</summary>
internal sealed record ScratchRunResult(
    bool Passed,
    int Green,
    int Red,
    int Known,
    int NoSteps,
    int Killed,
    IReadOnlyList<ScratchSceneResult> Scenes
);

/// <summary>A scene's steps played and in all.</summary>
internal sealed record ScratchStepCount(int Played, int Total);

/// <summary>The first step that failed, or the one in flight when the scene was killed (index -1 before any step), and why.</summary>
internal sealed record ScratchFailure(int Index, string Name, string Error, string Status);

/// <summary>An error feed entry as a step lists it.</summary>
internal sealed record ScratchError(string Message, string File, int Line);

/// <summary>One step as a red scene or options.details lists it.</summary>
internal sealed record ScratchStepResult(
    int Index,
    string Name,
    bool Ok,
    int GameMs,
    string Status,
    IReadOnlyList<ScratchError> Errors,
    IReadOnlyList<string> Lines
);

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
    /// <summary>The profile's reason for the scene's pace, when the scene came out red or killed; null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PaceReason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScratchFailure? FailedAt { get; init; }

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

    /// <summary>The most lines a step lists; the rest are counted in one entry after them, "… N more".</summary>
    public const int MaxLines = 20;

    private const string ScratchNotePrefix = "[scratch]";

    /// <summary>Why a step failed: its call's error, else the feed's first error, else its first line a pattern matches; null if it passed.</summary>
    public static string? StepError(ScratchStep step, IReadOnlyList<Regex> patterns)
    {
        if (step.CallError is not null)
        {
            return step.CallError;
        }

        return FeedError(step.Errors) ?? step.Lines.Select(line => LineError(line, patterns)).FirstOrDefault(error => error is not null);
    }

    /// <summary>The feed's first error (not warning) as a step or the exit reports it, with its file and line; null when none.</summary>
    public static string? FeedError(IEnumerable<ErrorEntry> entries) =>
        entries.FirstOrDefault(entry => entry.IsError) is { } error
            ? error.File.Length > 0
                ? $"{error.Message} ({error.File}:{error.Line})"
                : error.Message
            : null;

    /// <summary>The scene's verdict and result.</summary>
    public static ScratchSceneResult Judge(ScratchObservation seen, ScratchRules rules)
    {
        (string verdict, ScratchFailure? failedAt) = Underlying(seen, rules.Patterns);
        string shown = Known(verdict, failedAt, rules.Known);
        bool listSteps = rules.Details || verdict is Red or Killed;
        return new ScratchSceneResult(
            seen.Scene,
            shown,
            new ScratchStepCount(seen.Steps.Count, seen.Total),
            seen.Pace,
            Math.Round(seen.Seconds, 1),
            seen.Session
        )
        {
            PaceReason = shown is Red or Killed ? rules.PaceReason : null,
            FailedAt = failedAt,
            Details = listSteps && seen.Refusal is null ? [.. seen.Steps.Select(step => StepResult(step, rules.Patterns, verdict != Green))] : null,
            Exit = ExitOf(seen, rules.Patterns),
            Known = shown is KnownRed or KnownNowGreen ? rules.Known : null,
        };
    }

    /// <summary>The run's result: each verdict counted, known-now-green among the red; it passed when none is red or killed.</summary>
    public static ScratchRunResult Summarise(IReadOnlyList<ScratchSceneResult> scenes)
    {
        int Count(params string[] verdicts) => scenes.Count(scene => verdicts.Contains(scene.Verdict, StringComparer.Ordinal));
        int red = Count(Red, KnownNowGreen);
        int killed = Count(Killed);
        return new ScratchRunResult(red == 0 && killed == 0, Count(Green), red, Count(KnownRed), Count(NoSteps), killed, scenes);
    }

    /// <summary>
    /// Whether a scene is played once more alone: it ran beside others (<paramref name="parallel"/> above 1) and came out red
    /// or killed, and it is not known. A red scene refused before any step (none played, failed at index -1: it did not start,
    /// has no current scene or lacks the scratch protocol) is not played again, since no game beside it explains that; a
    /// killed one is, before its first step or in the pace after its last alike, since load can kill either.
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
    private static (string Verdict, ScratchFailure? FailedAt) Underlying(ScratchObservation seen, IReadOnlyList<Regex> patterns)
    {
        if (seen.Kill is not null)
        {
            return (Killed, seen.Kill);
        }

        if (seen.Refusal is not null)
        {
            return (Red, new ScratchFailure(-1, string.Empty, seen.Refusal, string.Empty));
        }

        if (seen.Total == 0)
        {
            return (NoSteps, null);
        }

        foreach (ScratchStep step in seen.Steps)
        {
            if (StepError(step, patterns) is { } error)
            {
                return (Red, new ScratchFailure(step.Index, step.Name, error, step.Status));
            }
        }

        return ExitFails(seen, patterns) ? (Red, null) : (Green, null);
    }

    /// <summary>The verdict shown for a scene the profile knows to fail; one that failed before any step is red, never known.</summary>
    private static string Known(string verdict, ScratchFailure? failedAt, string? known) =>
        known is null || failedAt is { Index: < 0 } ? verdict
        : verdict == Red ? KnownRed
        : verdict == Green ? KnownNowGreen
        : verdict;

    /// <summary>
    /// Whether the exit turns a scene with every step green red: an error in the pace after the last step, a kill by the stop, a
    /// non-zero code, a leak, or a matching line after the steps.
    /// </summary>
    private static bool ExitFails(ScratchObservation seen, IReadOnlyList<Regex> patterns) =>
        seen.AfterError is not null
        || seen.StopKillReason is not null
        || seen.ExitCode is not (null or 0)
        || Leaked(seen.ExitLines) is not null
        || seen.ExitLines.Any(line => Matches(line, patterns));

    private static ScratchExit ExitOf(ScratchObservation seen, IReadOnlyList<Regex> patterns)
    {
        List<string> matched = [.. seen.ExitLines.Where(line => Matches(line, patterns))];
        return new ScratchExit(seen.ExitCode)
        {
            Leaked = Leaked(seen.ExitLines),
            Lines = matched.Count > 0 ? Capped(matched) : null,
            Error = seen.AfterError,
            Killed = seen.StopKillReason is null ? null : true,
            KillReason = seen.StopKillReason,
            Warning = seen.StopWarning,
        };
    }

    /// <summary>A step as it is listed; a red or killed scene's step keeps its [scratch] notes beside its matching lines.</summary>
    private static ScratchStepResult StepResult(ScratchStep step, IReadOnlyList<Regex> patterns, bool keepNotes)
    {
        List<string> lines =
        [
            .. step.Lines.Where(line => Matches(line, patterns) || (keepNotes && line.StartsWith(ScratchNotePrefix, StringComparison.Ordinal))),
        ];
        List<ScratchError> errors =
        [
            .. step.Errors.Where(entry => entry.IsError).Select(entry => new ScratchError(entry.Message, entry.File, entry.Line)),
        ];
        if (step.Dropped > 0)
        {
            lines.Insert(0, DroppedNote(step.Dropped));
        }

        return new ScratchStepResult(step.Index, step.Name, StepError(step, patterns) is null, step.GameMs, step.Status, errors, Capped(lines));
    }

    /// <summary>The entry a step lists first when its window lost lines to the output buffer's cap.</summary>
    internal static string DroppedNote(long dropped) =>
        string.Create(CultureInfo.InvariantCulture, $"… {dropped} earlier lines were no longer in the output buffer");

    /// <summary>The first <see cref="MaxLines"/> lines, followed by "… N more" when there are more.</summary>
    internal static List<string> Capped(List<string> lines)
    {
        if (lines.Count <= MaxLines)
        {
            return lines;
        }

        List<string> kept = lines[..MaxLines];
        kept.Add(string.Create(CultureInfo.InvariantCulture, $"… {lines.Count - MaxLines} more"));
        return kept;
    }

    /// <summary>Whether a pattern matches the line; a pattern that ran past its timeout on it counts as matching.</summary>
    private static bool Matches(string line, IReadOnlyList<Regex> patterns) => LineError(line, patterns) is not null;

    /// <summary>
    /// Why the line fails a step: the line itself when a pattern matches it, the pattern's refusal when it ran past its timeout
    /// on it, else null.
    /// </summary>
    private static string? LineError(string line, IReadOnlyList<Regex> patterns)
    {
        foreach (Regex pattern in patterns)
        {
            try
            {
                if (pattern.IsMatch(line))
                {
                    return line;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                string seconds = pattern.MatchTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
                return $"pattern '{pattern}' took over {seconds} s on a line; simplify it in scratch.patterns";
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?<count>\d+) ObjectDB instances? (was|were) leaked at exit", RegexOptions.CultureInvariant)]
    private static partial Regex LeakLine();
}
