using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace GodotMcp.Server.Session;

/// <summary>
/// A scene's ceiling given rather than derived from its paces: how many load-adjusted seconds from the scene's first step, and
/// what set it, options.ceilings or the profile's scratch.ceilings.
/// </summary>
internal sealed record ScratchCeiling(double Seconds, string Source)
{
    /// <summary>The source a call's options.ceilings sets a ceiling under.</summary>
    public const string OptionsSource = "options.ceilings";

    /// <summary>The source the profile's scratch.ceilings sets a ceiling under.</summary>
    public const string ProfileSource = "the profile's scratch.ceilings";
}

/// <summary>
/// A scene's memory ceiling given rather than left unbounded: how many megabytes of private memory its game may use before it is
/// killed, and what set it, options.memoryCeilings or the profile's scratch.memoryCeilings.
/// </summary>
internal sealed record ScratchMemoryCeiling(double Megabytes, string Source)
{
    /// <summary>The source a call's options.memoryCeilings sets a ceiling under.</summary>
    public const string OptionsSource = "options.memoryCeilings";

    /// <summary>The source the profile's scratch.memoryCeilings sets a ceiling under.</summary>
    public const string ProfileSource = "the profile's scratch.memoryCeilings";
}

/// <summary>
/// A scratch scene to play: its name (the file's, without its extension), its res:// path, its pace in seconds, the user arguments it
/// starts with, the engine arguments its game launches with, the ceiling the call or the profile gives it, if any, the reason the
/// profile knows it to fail, if any, the profile's reason for its pace, if any, the paces of single steps, keyed by a step's index
/// or its name, the setup steps the profile declares for it, keyed the same way, the patterns its output must match a line of,
/// and the time scale its game runs at.
/// </summary>
internal sealed record ScratchScenePlan(string Name, string ResPath, double Pace, IReadOnlyList<string> UserArgs)
{
    public string? Known { get; init; }

    public string? PaceReason { get; init; }

    /// <summary>
    /// The paces of single steps and why, keyed by a step's index or its name, resolved once the scene has named its steps.
    /// </summary>
    public IReadOnlyDictionary<string, ScratchPace> StepPaces { get; init; } = ReadOnlyDictionary<string, ScratchPace>.Empty;

    /// <summary>
    /// The profile pace's "exitHold", the seconds waited after the last played step in place of that step's pace, kept beside
    /// options.pace; null when the profile sets none.
    /// </summary>
    public double? ExitHold { get; init; }

    /// <summary>
    /// The profile pace's "guard", the hang guard in seconds of each of the scene's until steps whose step pace sets none, kept
    /// beside options.pace; null when the profile sets none, which keeps <see cref="SessionRegistry.ScratchUntilGuard"/>.
    /// </summary>
    public double? Guard { get; init; }

    /// <summary>
    /// The setup steps the profile declares for the scene: its scene-wide list, which a partial run adds to the ones options.steps
    /// selects, and each step's own list, added only when the run selects it; both resolved only then, and none when the profile
    /// declares none.
    /// </summary>
    public ScratchSetup Setup { get; init; } = ScratchSetup.None;

    /// <summary>
    /// The patterns the scene's output must each match a line of: the profile's scratch.expect, then its scratch.expectFor entry
    /// for the scene; empty when the profile expects none.
    /// </summary>
    public IReadOnlyList<ScratchPattern> Expect { get; init; } = [];

    /// <summary>The time scale the scene's game runs at: the call's options.timeScale, or 1 when its profile pace opts out.</summary>
    public required double TimeScale { get; init; }

    /// <summary>The scene's own engine arguments, after the runner's own --headless and --time-scale.</summary>
    public required IReadOnlyList<string> EngineArgs { get; init; }

    /// <summary>The largest pace the scene holds: its own, one of its steps', or its exit hold.</summary>
    public double LargestPace => StepPaces.Values.Select(step => step.Seconds).Append(Pace).Append(ExitHold ?? 0).Max();

    /// <summary>
    /// The scene's own ceiling of load-adjusted time from its first step, given by options.ceilings or the profile's
    /// scratch.ceilings; null derives it from the played steps' paces.
    /// </summary>
    public ScratchCeiling? Ceiling { get; init; }

    /// <summary>
    /// The megabytes of private memory the scene's game may use before it is killed, given by options.memoryCeilings or the
    /// profile's scratch.memoryCeilings; null leaves it unbounded.
    /// </summary>
    public ScratchMemoryCeiling? MemoryCeiling { get; init; }

    /// <summary>The batch's place among the scene's batches, 0-based, when options.steps gave it several; null otherwise.</summary>
    public int? Batch { get; init; }

    /// <summary>How many batches the scene's options.steps entry gave it; null when it gave none.</summary>
    public int? Batches { get; init; }

    /// <summary>The batch's own options.steps tokens; null when the scene plays a plan-wide list, a map list or every step.</summary>
    public IReadOnlyList<ScratchStepToken>? StepTokens { get; init; }
}

/// <summary>
/// One options.steps token as the call wrote it: a number, which is a step's index; or a string, which is an index spelled
/// plainly, a step's exact name, or an inclusive range of indices such as "2-5", read in that order once the scene has named
/// its steps.
/// </summary>
internal readonly partial record struct ScratchStepToken(string Text, bool IsNumber)
{
    /// <summary>The token as the call wrote it: a number bare, a string quoted.</summary>
    public override string ToString() => IsNumber ? Text : $"\"{Text}\"";

    /// <summary>Whether the token is a string written as a range, two runs of digits joined by '-', with each bound's digits.</summary>
    public bool IsRange(out string first, out string last)
    {
        Match range = IsNumber ? Match.Empty : RangeText().Match(Text);
        first = range.Success ? range.Groups[1].Value : string.Empty;
        last = range.Success ? range.Groups[2].Value : string.Empty;
        return range.Success;
    }

    [GeneratedRegex(@"^([0-9]+)-([0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex RangeText();
}

/// <summary>
/// A checked run_scratches call: the project, its scenes in order, the patterns, whether to prepare and list every step, how
/// many scenes play at once, whether the call listed its scenes rather than leaving them to the folder, whether it reads every
/// scene's steps without playing one, the prefix each scene's session is named with, whether a scene plays on past a failed
/// step, the steps options.steps selects, the call's time scale, and how many characters a step's status and each of its
/// lines keep.
/// </summary>
internal sealed record ScratchPlan(
    string ProjectDir,
    IReadOnlyList<ScratchScenePlan> Scenes,
    IReadOnlyList<ScratchPattern> Patterns,
    bool Prepare,
    ScratchDetails Details
)
{
    public required int DetailChars { get; init; }

    public required int Parallel { get; init; }

    public required bool Listed { get; init; }

    /// <summary>options.listSteps: every scene is booted and its steps read, none played; false when left out.</summary>
    public bool ListSteps { get; init; }

    public required string SessionPrefix { get; init; }

    /// <summary>Whether a scene plays on past a step that failed on a pushed error or a matching line.</summary>
    public required bool KeepGoing { get; init; }

    /// <summary>
    /// options.steps' tokens when it is a list, which every scene resolves against its own step names; null when it is left out,
    /// which plays every step, or a map (<see cref="StepsByScene"/>).
    /// </summary>
    public required IReadOnlyList<ScratchStepToken>? Steps { get; init; }

    /// <summary>
    /// options.steps' tokens when it is a map, keyed by the scene they apply to; a scene the map leaves out plays every step.
    /// Empty when options.steps is a list or left out.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ScratchStepToken>> StepsByScene { get; init; } =
        ReadOnlyDictionary<string, IReadOnlyList<ScratchStepToken>>.Empty;

    /// <summary>The tokens the scene named <paramref name="scene"/> plays; null plays every step.</summary>
    public IReadOnlyList<ScratchStepToken>? StepsFor(string scene) => Steps ?? StepsByScene.GetValueOrDefault(scene);

    /// <summary>options.timeScale, the time scale the scenes' games run at unless a scene's profile pace opts out; 1 by default.</summary>
    public required double TimeScale { get; init; }

    /// <summary>
    /// The profile's scratch.warnings "red": every window takes the feed's warnings as well as its errors, so a warning fails
    /// the boot, a step or the exit as an error does; false when the key is left out.
    /// </summary>
    public required bool WarningsRed { get; init; }

    /// <summary>options.watch, checked: the watch armed on every scene's session before its first step; null arms none.</summary>
    public ScratchWatchOptions? Watch { get; init; }

    /// <summary>options.windowed: the scenes' games launch in a hidden, off-screen window instead of headless.</summary>
    public bool Windowed { get; init; }

    /// <summary>options.audible: a windowed scene's game keeps the project's audio driver instead of the Dummy one.</summary>
    public bool Audible { get; init; }

    /// <summary>The scratch user:// every scene's game gets a folder of its own of, seeded alike; null keeps the real one.</summary>
    public ScratchUserDataRequest? UserData { get; init; }

    /// <summary>options.capture, checked: the steps each scene takes a screenshot at; null takes none.</summary>
    public ScratchCapture? Capture { get; init; }
}

/// <summary>
/// options.capture as a plan holds it: the tokens naming the steps to shoot, by index or name, for every scene (Steps) or per scene
/// (StepsByScene); a scene with no tokens has every played step shot.
/// </summary>
internal sealed record ScratchCapture(IReadOnlyList<ScratchStepToken>? Steps)
{
    /// <summary>
    /// options.capture.steps' tokens when it is a map, keyed by the scene they apply to; a scene the map leaves out shoots every
    /// played step. Empty when options.capture.steps is a list or left out.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ScratchStepToken>> StepsByScene { get; init; } =
        ReadOnlyDictionary<string, IReadOnlyList<ScratchStepToken>>.Empty;

    /// <summary>The tokens the scene named <paramref name="scene"/> shoots; null shoots every played step.</summary>
    public IReadOnlyList<ScratchStepToken>? StepsFor(string scene) => Steps ?? StepsByScene.GetValueOrDefault(scene);
}

/// <summary>
/// A scene's step paces by index, the steps a run plays of them, ascending, the time scale its game runs at and its exit hold:
/// what its ceiling, its wait after the last step played and its kill reason count. A paced step waits its pace in game time, so
/// at time scale N it takes pace / N of wall time; an until step waits for its condition however long it takes, up to its hang
/// guard in load-adjusted time (<see cref="GuardOf"/>). The wait after the last step played, the exit hold else that step's
/// pace, counts in wall time above time scale 1 and in game time below it.
/// </summary>
internal sealed record ScratchWalk(IReadOnlyList<ScratchPace> Paces, IReadOnlyList<int> Played, double TimeScale)
{
    public static readonly ScratchWalk None = new([], [], 1);

    /// <summary>The ceiling the call or the profile gives the scene; null derives it from the played steps' paces.</summary>
    public ScratchCeiling? Given { get; init; }

    /// <summary>
    /// The played steps the ceiling counts at their guard (<see cref="GuardOf"/>) in place of their pace: those whose GetStepUntil
    /// gave a condition, or whose read of it failed, before the ceiling's clock started.
    /// </summary>
    public IReadOnlySet<int> UntilSteps { get; init; } = new HashSet<int>();

    /// <summary>
    /// An until step's hang guard in load-adjusted time when its pace sets none: <see cref="SessionRegistry.ScratchUntilGuard"/>.
    /// </summary>
    public TimeSpan UntilGuard { get; init; }

    /// <summary>
    /// Step <paramref name="step"/>'s hang guard in load-adjusted time: its pace's "guard", its step entry's else its scene's
    /// (<see cref="ScratchRun.StepPaces"/>), else <see cref="UntilGuard"/>.
    /// </summary>
    public TimeSpan GuardOf(int step) => Paces[step].Guard is { } seconds ? TimeSpan.FromSeconds(seconds) : UntilGuard;

    /// <summary>
    /// The profile's exit hold for the scene: the seconds waited after the last step played in place of that step's pace; null
    /// waits that step's pace.
    /// </summary>
    public double? ExitHold { get; init; }

    /// <summary>The least the ceiling the played steps give is: <see cref="SessionRegistry.ScratchBoundFloor"/>.</summary>
    public TimeSpan Floor { get; init; }

    /// <summary>
    /// The scene's ceiling: <see cref="Given"/> when the call or the profile sets one, else that of the played steps, from each
    /// one's pace, or an until step's guard, and the exit hold (<see cref="ScratchRun.Ceiling"/>), at least <see cref="Floor"/>.
    /// </summary>
    public TimeSpan Ceiling => Given is { } given ? TimeSpan.FromSeconds(given.Seconds) : ScratchBounds.Floored(PlayedCeiling, Floor);

    /// <summary>The ceiling the played steps give, unfloored.</summary>
    private TimeSpan PlayedCeiling =>
        ScratchRun.Ceiling([.. Played.Select(step => Paces[step].Seconds)], [.. Played.Select(PlayedGuard)], TimeScale, ExitHold);

    /// <summary>A played step's guard when it is an until step; null for a paced step.</summary>
    private TimeSpan? PlayedGuard(int step) => UntilSteps.Contains(step) ? GuardOf(step) : null;

    /// <summary>
    /// The game milliseconds waited after the last step played: the exit hold, else that step's own pace, in game time or in wall
    /// time, whichever is longer, so the seconds × the time scale above 1.
    /// </summary>
    public int AfterGameMs => ScratchRun.GameMs(AfterSeconds * Math.Max(1, TimeScale));

    /// <summary>
    /// The window of a watch options.watch arms without one: the played steps' paces and the wait after the last step played
    /// (<see cref="AfterGameMs"/>), in game ms, at most the watch's 120000.
    /// </summary>
    public WatchWindow DefaultWatchWindow =>
        new(GameMs: (int)Math.Min(Played.Sum(step => (long)ScratchRun.GameMs(Paces[step].Seconds)) + AfterGameMs, RuntimeTools.MaxWaitGameMs));

    /// <summary>
    /// The most wall seconds the scene's steps can still take once <paramref name="step"/> starts: the pace of each played step
    /// not yet started and the step's own, each divided by the time scale, or an until step's own guard, and the wait after the
    /// last step played, in wall time above time scale 1 and in game time below it; the launch and the prep are left out.
    /// </summary>
    public double RemainingSecondsAtMost(int step)
    {
        IEnumerable<int> remaining = Played.Where(played => played >= step);
        double paced = remaining.Where(played => !UntilSteps.Contains(played)).Sum(played => Paces[played].Seconds) / TimeScale;
        double guarded = remaining.Where(UntilSteps.Contains).Sum(played => GuardOf(played).TotalSeconds);
        return paced + guarded + (AfterSeconds / Math.Min(1, TimeScale));
    }

    /// <summary>The seconds of the wait after the last step played: the exit hold, else that step's own pace.</summary>
    private double AfterSeconds => ExitHold ?? Paces[Played[^1]].Seconds;

    /// <summary>Why a scene was killed at its ceiling, with <paramref name="backstop"/> appended to the budget's terms.</summary>
    public string CeilingReason(string backstop) => Given is { } given ? GivenCeilingReason(given, backstop) : PacedCeilingReason(backstop);

    /// <summary>Why a scene was killed at a ceiling the call or the profile gave it, naming the seconds and what set them.</summary>
    private static string GivenCeilingReason(ScratchCeiling given, string backstop) =>
        $"the scene passed its ceiling of {GivenSeconds(given.Seconds)} s of load-adjusted time, set by {given.Source}{backstop}, "
        + "so its game was stopped";

    /// <summary>
    /// Why a scene was killed at the ceiling its paces give, with the terms that ceiling sums, an until step's guard the largest
    /// among the played until steps'.
    /// </summary>
    private string PacedCeilingReason(string backstop)
    {
        string each = Played.Count == 1 ? "its 1 step's" : $"each of its {Played.Count} steps'";
        string scaled = TimeScale == 1 ? string.Empty : $" at time scale {TimeScale.ToString(CultureInfo.InvariantCulture)}";
        string wall = TimeScale > 1 ? " in wall time" : string.Empty;
        string after = ExitHold is { } hold ? $"the exit hold of {GivenSeconds(hold)} s{wall}" : $"the last step's pace once more{wall}";
        string guard = Played.Any(UntilSteps.Contains)
            ? $", an until step's guard of {Seconds(Played.Where(UntilSteps.Contains).Max(GuardOf))} s in place of its pace"
            : string.Empty;
        return $"the scene passed its ceiling of {Seconds(Ceiling)} s of load-adjusted time "
            + $"({each} pace{scaled} + 10 s{guard}, {after}, and 10 s for the markers){backstop}, so its game was stopped";
    }

    private static string Seconds(TimeSpan ceiling) => ceiling.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Seconds a profile or a call wrote, to the thousandth.</summary>
    private static string GivenSeconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// The bounds a scene's walk holds its game to besides its ceiling: a bridge call's timeout, a window's marker wait and a pace
/// wait's release, each the longer of its own value and <paramref name="Floor"/> (<see cref="SessionRegistry.ScratchBoundFloor"/>),
/// and an until wait's release, <paramref name="UntilGuard"/> (<see cref="SessionRegistry.ScratchUntilGuard"/>) floored the same
/// way, the guard itself never floored.
/// </summary>
internal sealed record ScratchBounds(TimeSpan Floor, TimeSpan UntilGuard)
{
    /// <summary>How long a bridge call the walk makes runs before the bridge cancels it.</summary>
    public TimeSpan Call => Floored(ScratchRun.CallTimeout, Floor);

    /// <summary>How long a window's end markers are waited for, in load-adjusted time.</summary>
    public TimeSpan Markers => Floored(ScratchRun.StepAllowance, Floor);

    /// <summary>When a wait of <paramref name="gameMs"/> at <paramref name="timeScale"/> is released; see <see cref="ScratchRun.Release"/>.</summary>
    public TimeSpan Release(int gameMs, double timeScale) => Floored(ScratchRun.Release(gameMs, timeScale), Floor);

    /// <summary>
    /// When the until wait of a step whose hang guard is <paramref name="guard"/> (<see cref="ScratchWalk.GuardOf"/>) is released,
    /// not met: that guard in load-adjusted time, whatever its pace, which a wait whose condition holds never reaches, at least
    /// <see cref="Floor"/>. Only this release is floored: the guard itself, and so the bridge's game-time limit and the guard a
    /// hung until's error names, never is.
    /// </summary>
    public TimeSpan UntilReleaseOf(TimeSpan guard) => Floored(guard, Floor);

    /// <summary>The longer of <paramref name="bound"/> and <paramref name="floor"/>.</summary>
    internal static TimeSpan Floored(TimeSpan bound, TimeSpan floor) => bound > floor ? bound : floor;
}

/// <summary>A line number in each of a session's stdout and stderr: the edge of a step's window.</summary>
internal readonly record struct LineMark(long Stdout, long Stderr);

/// <summary>
/// A scene's steps as a run resolves them: the indices options.steps selected (null when the call gave no steps), the setup steps
/// the run added to that selection, and the union it plays, ascending.
/// </summary>
internal sealed record ScratchSelection(int[]? Selected, int[] Setup, int[] Played);

/// <summary>
/// A step shot's reply as its sizes and warning read it: the window's and the viewport's size it names, and the warning a shot
/// that is not its window's size gives; each null for a reply that names none.
/// </summary>
internal sealed record StepShotSizes(CaptureSize? Window, CaptureSize? Viewport, string? Warning);

/// <summary>
/// A step's shot: its path in the run's folder, the window's and the viewport's size it was taken in, and the warning it gave
/// or why it was not kept; all null for a step that took none.
/// </summary>
internal sealed record StepShot(string? Path, CaptureSize? Window, CaptureSize? Viewport, string? Warning);

/// <summary>
/// A scene's step names by index, and the indices its root's optional GetStepHidden marks hidden: a mark only, a hidden step
/// keeping its index and playing by index or name as any other. A collection expression builds names none of which is hidden.
/// </summary>
[CollectionBuilder(typeof(ScratchStepNames), nameof(Create))]
internal sealed class ScratchStepNames(IReadOnlyList<string> names, IReadOnlySet<int> hidden) : IReadOnlyList<string>
{
    /// <summary>The indices of the steps marked hidden.</summary>
    public IReadOnlySet<int> Hidden => hidden;

    public int Count => names.Count;

    public string this[int index] => names[index];

    /// <summary>Names none of which is hidden.</summary>
    public static ScratchStepNames Create(ReadOnlySpan<string> names) => new([.. names], new HashSet<int>());

    /// <summary>One line per step, "&lt;index&gt; &lt;name&gt;", with " (hidden)" after the index of a hidden step.</summary>
    public IReadOnlyList<string> Lines() =>
        [.. names.Select((name, index) => hidden.Contains(index) ? $"{index} (hidden) {name}" : $"{index} {name}")];

    public IEnumerator<string> GetEnumerator() => names.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Plays one scratch scene in a headless game session of its own, as an agent would with the runtime tools: launches it,
/// asks the game for its current scene's path, checks that root for the scratch protocol, reads its step names and takes the
/// error feed's entries so far, the boot's: an error among them, or a line printed so far that a pattern matches, fails the scene
/// before any step plays. A step pace key naming two steps, two keys naming one step, or an options.steps token naming none,
/// refuses the scene before any step plays; a step pace key naming no step is skipped and named in the scene's warnings. Then
/// per step it plays (every step, or a partial run's selected steps with the setup steps its profile adds to them,
/// in ascending order) it calls PlayStep, waits the step's pace in game time (for a root that has GetStepUntil and a step it gives
/// a condition, plays the step inside one wait_for on that condition instead, PlayStep the wait's call, until it holds, however
/// long past its pace that is, with a hang guard of <see cref="UntilHangGuard"/> in load-adjusted time; those answers are read
/// once for every played step before the ceiling's clock starts), reads GetStatus and takes the feed's entries since
/// the last window, stopping at the first failed step, or with the plan's keep-going only at one whose call failed; after the last
/// step played it holds the exit, that step's pace once more (the longer of its game time and its wall time) or the scene's exit
/// hold, measured by the bridge in game time from that step's cut, which prints the hold's end marker itself; then it stops the
/// game gracefully and reads the lines it printed after the steps.
/// Each window of output (the launch, each step, the pace after the last) ends at a marker line the game prints to stdout
/// and to stderr, since the two streams arrive apart. The session stays in the registry, stopped, so get_debug_output can
/// read it.
/// </summary>
internal sealed class ScratchRun
{
    /// <summary>The scratch protocol: the methods a scratch scene's root implements.</summary>
    public static readonly IReadOnlyList<string> Protocol = ["PlayStep", "GetStepCount", "GetStepName", "GetStatus"];

    /// <summary>
    /// The protocol's optional method: a root that has it gives each step a wait_for condition (empty or null for a paced step),
    /// and the runner plays such a step inside one wait on it.
    /// </summary>
    internal const string UntilMethod = "GetStepUntil";

    /// <summary>
    /// The protocol's optional shot method: a root that has it gives a paced step options.capture shoots a wait_for condition
    /// (null or empty for a shot at the end of its pace), and the runner shoots the frame it first holds within the pace.
    /// </summary>
    internal const string ShotUntilMethod = "GetStepShotUntil";

    /// <summary>
    /// The protocol's other optional method: a root that has it answers, per step, whether the step is hidden, which marks the
    /// step in the scene's step list and changes nothing else.
    /// </summary>
    internal const string HiddenMethod = "GetStepHidden";

    private const string BridgeRefusalPrefix = "The bridge refused 'wait_for': ";

    /// <summary>The warning of a scene whose watch timeline ends at the shutdown, the bridge's final one not having come.</summary>
    internal const string WatchNotFinal = "options.watch could not be collected at the game's exit; the timeline ends at the shutdown";

    /// <summary>The stopped of a scene's watch timeline that ends at the shutdown (<see cref="WatchNotFinal"/>).</summary>
    private const string StoppedAtShutdown = "shutdown";

    /// <summary>The until wait's parameter, sent by the runner alone, of the game milliseconds the bridge ends it at, not met.</summary>
    private const string LimitGameMsField = "limitGameMs";
    private const string PreviewMaxWidthField = "previewMaxWidth";

    /// <summary>The start of every marker line the runner has the game print; such lines are never judged or listed.</summary>
    internal const string MarkerPrefix = "[godot-mcp] scratch ";

    private const string WatchCommand = "watch";

    private const string PaceKeyLabel = "pace.steps key";
    private const string SetupLabel = "setup";
    private const string SetupStepLabel = "setup for step";
    private const string StepsTokenLabel = "options.steps token";
    private const string CaptureTokenLabel = "options.capture.steps token";
    private const int MaxShotNameChars = 64;

    private const string CurrentSceneScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\tvar scene: Node = scene_tree.current_scene\n"
        + "\treturn \"\" if scene == null else str(scene.get_path())\n";

    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReplyAllowance = TimeSpan.FromSeconds(5);

    /// <summary>The text a request's failure gives when the bridge closes the connection (<see cref="Wire.BridgeConnection"/>).</summary>
    private const string BridgeClosedMessage = "The bridge closed the connection.";
    internal static readonly TimeSpan StepAllowance = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The hang guard of a step's until, in load-adjusted time: a passing run never reaches it, since the wait ends when the
    /// condition holds, however long past the step's pace; only a condition that never holds waits it out. Sized by the slow
    /// path it rules out, not by any pace.
    /// </summary>
    internal static readonly TimeSpan UntilHangGuard = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A judged entry (an error, or a warning when scratch.warnings is "red") the feed logged across a step's GetStepUntil read
    /// before the walk, which no step's window takes; Kind is "an error" or "a warning".
    /// </summary>
    private static readonly Action<ILogger, int, string, string, Exception?> EarlyUntilReadLogged = LoggerMessage.Define<int, string, string>(
        LogLevel.Warning,
        default,
        "Scratch step {Index}'s GetStepUntil read before the walk logged {Kind}, passed over there: {Message}; the step re-reads its until at play."
    );

    /// <summary>
    /// A line the game printed across the GetStepUntil reads before the walk, one of which logged a judged entry (an error, or a
    /// warning when scratch.warnings is "red").
    /// </summary>
    private static readonly Action<ILogger, string, Exception?> EarlyUntilLinePassedOver = LoggerMessage.Define<string>(
        LogLevel.Warning,
        default,
        "A scratch scene printed this across its GetStepUntil reads before the walk, one of which logged an error; passed over there: {Line}"
    );

    /// <summary>Why the output window over the GetStepUntil reads before the walk did not close on its markers.</summary>
    private static readonly Action<ILogger, string, Exception?> EarlyUntilWindowUnclosed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        default,
        "The output window over a scratch scene's GetStepUntil reads before the walk did not close on its markers: {Error}"
    );

    /// <summary>A window whose last wait carried its end marker that still closed at the runner's own marker, and why.</summary>
    private static readonly Action<ILogger, string, string, Exception?> WindowFellBack = LoggerMessage.Define<string, string>(
        LogLevel.Warning,
        default,
        "scratch: {Label} window fell back to the runner's marker: {Why}"
    );

    private static readonly TimeSpan MarkerPoll = TimeSpan.FromMilliseconds(10);
    private static readonly LineMark EveryLine = new(long.MaxValue, long.MaxValue);

    private readonly SessionRegistry _registry;
    private readonly ScratchBounds _bounds;
    private readonly CSharpBridge _csharp;
    private readonly ScratchPlan _plan;
    private readonly ScratchScenePlan _scene;
    private readonly IScratchRunObserver? _observer;
    private readonly int _index;
    private readonly string _runId;
    private readonly string _nonce = Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _names = [];
    private readonly HashSet<int> _hidden = [];
    private readonly Dictionary<int, JsonNode?> _untilAnswers = [];
    private HashSet<int> _shots = [];
    private readonly string _shotFolder;
    private OutputPlaces? _places;
    private readonly List<ScratchStep> _steps = [];
    private ScratchWalk _walk = ScratchWalk.None;
    private int[]? _selected;
    private int[] _setupOnly = [];
    private GodotSession? _session;
    private LoadDeadline? _deadline;
    private string? _memoryKill;
    private int? _memoryPeak;
    private int? _freeMemoryMb;
    private LineMark _mark;
    private int _inFlight = -1;
    private string _rootPath = string.Empty;
    private string? _logPath;
    private List<string> _launchLines = [];
    private IReadOnlyList<ErrorEntry> _bootErrors = [];
    private readonly List<string> _warnings = [];
    private List<string> _paceLines = [];
    private string? _afterError;
    private bool _afterClosed;
    private long _feedMark;

    /// <summary>
    /// The judged entries each step's GetStepUntil read before the walk passed over, by step index: the step's own read at its
    /// play takes the ones it does not log again (<see cref="Unreproduced"/>), and a claim is dropped there so no window takes
    /// them twice.
    /// </summary>
    private readonly Dictionary<int, List<ErrorEntry>> _earlySkipped = [];
    private bool _hasUntil;
    private bool _hasShotUntil;
    private bool _hasHidden;
    private bool _watching;
    private JsonObject? _watch;
    private SessionClock? _watchClock;
    private JsonObject? _finalWatch;

    private ScratchRun(RunContext context, int index, bool alone)
    {
        _registry = context.Registry;
        _bounds = new ScratchBounds(context.Registry.ScratchBoundFloor, context.Registry.ScratchUntilGuard);
        _csharp = context.CSharp;
        _plan = context.Plan;
        _scene = context.Plan.Scenes[index];
        _observer = context.Observer;
        _index = index;
        _runId = context.RunId;
        _shotFolder = alone ? ReplayFolder(context.ShotFolders[index]) : context.ShotFolders[index];
    }

    /// <summary>
    /// What every scene of one run shares: the registry, the C# bridge, the plan, who is told as the run plays, the run's id, and
    /// the folder each scene shoots into by its place in the plan (<see cref="ShotFolders"/>).
    /// </summary>
    private sealed record RunContext(
        SessionRegistry Registry,
        CSharpBridge CSharp,
        ScratchPlan Plan,
        IScratchRunObserver? Observer,
        string RunId,
        IReadOnlyList<string> ShotFolders
    );

    /// <summary>
    /// Prepares the project once when the plan says so, then plays the scenes, starting them in <see cref="StartOrder"/> at most
    /// the plan's parallel at once, and judges each; a scene red or killed beside others is then played once more alone, one at a time in
    /// order. The result lists the scenes in the plan's order, whatever finished first, with what the prep did.
    /// <paramref name="csharp"/> readies the C# helper a state until reads a C# node's state with. <paramref name="observer"/>,
    /// a background run's, is told each scene's start, session, steps and verdict, and each replay's start; a synchronous run
    /// passes null. <paramref name="runId"/> is the run's id, which names each scene's game log, and is the background run's own;
    /// null mints one. The log folder keeps the newest <see cref="ScratchRunLogs.Kept"/> scratch run logs and shot folders, the
    /// rest pruned before the first scene launches.
    /// </summary>
    /// <exception cref="SessionException">The prep failed.</exception>
    public static async Task<ScratchRunResult> RunAsync(
        SessionRegistry registry,
        CSharpBridge csharp,
        ScratchPlan plan,
        IScratchRunObserver? observer,
        string? runId,
        CancellationToken cancellationToken
    )
    {
        RunContext context = new(
            registry,
            csharp,
            plan,
            observer,
            runId ?? ScratchBackgroundRun.NewRunId(DateTimeOffset.UtcNow),
            ShotFolders(plan.Scenes)
        );
        ScratchRunLogs.Prune(ProjectPrep.LogFolder(plan.ProjectDir), ScratchRunLogs.Kept, registry.Logger);
        ScratchRunLogs.PruneShots(ProjectPrep.LogFolder(plan.ProjectDir), ScratchRunLogs.Kept, registry.Logger);
        if (plan.Capture is not null)
        {
            CreateShotRoot(ProjectPrep.LogFolder(plan.ProjectDir), context.RunId, registry.Logger);
        }

        PrepResult prep = plan.Prepare ? await registry.PrepareFolderAsync(plan.ProjectDir, cancellationToken) : PrepResult.Skipped;
        ScratchSceneResult[] scenes = await PlayBesideAsync(context, cancellationToken);
        for (int index = 0; index < scenes.Length; index++)
        {
            if (ScratchVerdict.PlaysAgainAlone(scenes[index], plan.Parallel))
            {
                observer?.ReplayStarted(index);
                ScratchSceneResult replay = await PlayOneAsync(context, index, alone: true, cancellationToken);
                scenes[index] = ScratchVerdict.Alone(scenes[index], replay);
                observer?.SceneEnded(index, scenes[index], playsAgain: false);
            }
        }

        return ScratchVerdict.Summarise(scenes, prep, plan.TimeScale);
    }

    /// <summary>
    /// Plays every scene, each started once a slot is free, in <see cref="StartOrder"/>, its result kept at its place in the
    /// plan; waits for every scene started, even when a scene throws or the call is cancelled, so no game outlives the call.
    /// </summary>
    private static async Task<ScratchSceneResult[]> PlayBesideAsync(RunContext context, CancellationToken cancellationToken)
    {
        ScratchPlan plan = context.Plan;
        var results = new ScratchSceneResult[plan.Scenes.Count];
        using SemaphoreSlim slots = new(plan.Parallel, plan.Parallel);
        List<Task> playing = [];
        try
        {
            foreach (int index in StartOrder(plan.Scenes, plan.Listed))
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
                context.Observer?.SceneStarted(index);
                results[index] = await PlayOneAsync(context, index, alone: false, cancellationToken);
                context.Observer?.SceneEnded(index, results[index], ScratchVerdict.PlaysAgainAlone(results[index], plan.Parallel));
            }
            finally
            {
                slots.Release();
            }
        }
    }

    /// <summary>
    /// The places in <paramref name="scenes"/> in the order the scenes start: a listed run's own order; a folder run's by the
    /// wall time of the largest pace a scene holds, its own or a step's, divided by the scene's time scale, longest first, so the
    /// slowest scenes do not start last, with equal times in the folder's name order.
    /// </summary>
    internal static int[] StartOrder(IReadOnlyList<ScratchScenePlan> scenes, bool listed)
    {
        IEnumerable<int> places = Enumerable.Range(0, scenes.Count);
        return listed ? [.. places] : [.. places.OrderByDescending(place => scenes[place].LargestPace / scenes[place].TimeScale)];
    }

    /// <summary>
    /// The name a scene's session is named after: its own, and the batch's place when options.steps gave the scene several
    /// batches, so each batch keeps a session of its own.
    /// </summary>
    internal static string SessionScene(ScratchScenePlan scene) =>
        scene.Batch is int batch ? string.Create(CultureInfo.InvariantCulture, $"{scene.Name}.batch{batch}") : scene.Name;

    /// <summary>Plays and judges the scene at <paramref name="index"/>; <paramref name="alone"/> for its replay alone.</summary>
    private static async Task<ScratchSceneResult> PlayOneAsync(RunContext context, int index, bool alone, CancellationToken cancellationToken)
    {
        ScratchPlan plan = context.Plan;
        ScratchScenePlan scene = plan.Scenes[index];
        ScratchObservation seen = await new ScratchRun(context, index, alone).PlayAsync(cancellationToken);
        ScratchRules rules = new(plan.Patterns, scene.Known, scene.PaceReason, plan.Details, plan.DetailChars) { Expect = scene.Expect };
        return ScratchVerdict.Judge(seen, rules);
    }

    /// <summary>
    /// The ceiling of a scene's steps, load-adjusted, from each step's pace in game time at <paramref name="timeScale"/>: each
    /// step's pace / timeScale, or for an until step its own hang guard, the entry of <paramref name="untilGuards"/> for it (one
    /// entry per pace, null for a paced step), and 10 s, the wait after the last step (<paramref name="exitHold"/> when set, else
    /// that step's pace once more), in wall time above time scale 1 and in game time below it, and 10 s more for the launch and
    /// pace markers.
    /// </summary>
    internal static TimeSpan Ceiling(IReadOnlyList<double> paces, IReadOnlyList<TimeSpan?> untilGuards, double timeScale, double? exitHold) =>
        TimeSpan.FromSeconds(
            paces.Select((pace, step) => (untilGuards[step]?.TotalSeconds ?? (pace / timeScale)) + StepAllowance.TotalSeconds).Sum()
                + (paces.Count > 0 ? (exitHold ?? paces[^1]) / Math.Min(1, timeScale) : 0)
                + StepAllowance.TotalSeconds
        );

    /// <summary>
    /// The engine arguments a scene's game launches with: --headless unless <paramref name="windowed"/>, at a
    /// <paramref name="timeScale"/> other than 1 Godot's --time-scale, which scales every process and physics step, and then
    /// the scene's own <paramref name="engineArgs"/>.
    /// </summary>
    internal static string[] EngineArgs(double timeScale, IReadOnlyList<string> engineArgs, bool windowed)
    {
        string[] headless = windowed ? [] : ["--headless"];
        return timeScale == 1
            ? [.. headless, .. engineArgs]
            : [.. headless, "--time-scale", timeScale.ToString(CultureInfo.InvariantCulture), .. engineArgs];
    }

    /// <summary>
    /// The real time a wait of <paramref name="gameMs"/> game milliseconds is released at when the game's clock stands still: that
    /// game time at <paramref name="timeScale"/>, plus the step allowance.
    /// </summary>
    internal static TimeSpan Release(int gameMs, double timeScale) => TimeSpan.FromMilliseconds(gameMs / timeScale) + StepAllowance;

    /// <summary>
    /// Each of the steps <paramref name="names"/> lists' pace and why: the scene's, or a step pace keyed by the step's index, else
    /// by its name; or why a key names two steps, or two keys name one step. A key naming no step sets no step's pace and is
    /// named in the warnings, unless its pace is optional. Each pace carries the step's guard: its step pace's, else the scene's.
    /// </summary>
    internal static (ScratchPace[] Paces, string? Error, IReadOnlyList<string> Warnings) StepPaces(ScratchScenePlan scene, ScratchStepNames names)
    {
        ScratchPace scenePace = new(scene.Pace, scene.PaceReason) { Guard = scene.Guard };
        ScratchPace[] paces = [.. names.Select(_ => scenePace)];
        Dictionary<int, string> keyOf = [];
        List<string> warnings = [];
        foreach ((string key, ScratchPace pace) in scene.StepPaces)
        {
            if (!IsIndex(key, names.Count, out _) && !names.Contains(key, StringComparer.Ordinal))
            {
                if (!pace.Optional)
                {
                    warnings.Add($"scratch.pace.steps key \"{key}\" matches no step of {scene.Name}, so it sets no step's pace");
                }

                continue;
            }

            (int step, string? error) = StepOf(key, PaceKeyLabel, scene.Name, names);
            error ??= keyOf.TryGetValue(step, out string? other)
                ? $"pace.steps keys \"{other}\" and \"{key}\" of {scene.Name} both name step {step}; keep one"
                : null;
            if (error is not null)
            {
                return ([], error, warnings);
            }

            keyOf[step] = key;
            paces[step] = pace with { Guard = pace.Guard ?? scene.Guard };
        }

        return (paces, null, warnings);
    }

    /// <summary>
    /// The steps <paramref name="tokens"/> select among those <paramref name="names"/> lists, ascending, each once; or why a token
    /// selects no step. A number is an index; a string is an index spelled plainly, else a step's exact name, else a range.
    /// </summary>
    internal static (int[] Indices, string? Error) ResolveSteps(IReadOnlyList<ScratchStepToken> tokens, string scene, ScratchStepNames names)
    {
        SortedSet<int> played = [];
        foreach (ScratchStepToken token in tokens)
        {
            (IEnumerable<int> steps, string? error) = TokenSteps(token, scene, names);
            if (error is not null)
            {
                return ([], error);
            }

            played.UnionWith(steps);
        }

        return ([.. played], null);
    }

    /// <summary>
    /// The steps <paramref name="capture"/> shoots among those <paramref name="names"/> lists, ascending, each once, null for every
    /// played step; or why a token names no step, as an options.steps token naming none is refused. A number is an index; a string
    /// is an index spelled plainly, else a step's exact name.
    /// </summary>
    internal static (int[]? Steps, string? Error) CaptureSteps(ScratchCapture capture, string scene, ScratchStepNames names)
    {
        if (capture.StepsFor(scene) is not { } tokens)
        {
            return (null, null);
        }

        SortedSet<int> steps = [];
        foreach (ScratchStepToken token in tokens)
        {
            (int step, string? error) = token.IsNumber ? CaptureIndex(token, scene, names) : StepOf(token.Text, CaptureTokenLabel, scene, names);
            if (error is not null)
            {
                return (null, error);
            }

            steps.Add(step);
        }

        return ([.. steps], null);
    }

    /// <summary>A number token of options.capture.steps as its step, or why the scene has no step of that index.</summary>
    private static (int Step, string? Error) CaptureIndex(ScratchStepToken token, string scene, ScratchStepNames names)
    {
        int index = int.Parse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture);
        return index < names.Count ? (index, null) : (-1, NoStep($"{CaptureTokenLabel} {token}", scene, names));
    }

    private static (IEnumerable<int> Steps, string? Error) TokenSteps(ScratchStepToken token, string scene, ScratchStepNames names)
    {
        if (token.IsNumber)
        {
            int index = int.Parse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture);
            return index < names.Count ? ([index], null) : ([], NoStep($"{StepsTokenLabel} {token}", scene, names));
        }

        bool named = IsIndex(token.Text, names.Count, out _) || names.Contains(token.Text, StringComparer.Ordinal);
        if (!named && token.IsRange(out string first, out string last))
        {
            return RangeSteps(token, (first, last), scene, names);
        }

        (int step, string? error) = StepOf(token.Text, StepsTokenLabel, scene, names);
        return error is null ? ([step], null) : ([], error);
    }

    /// <summary>The steps from the range's first bound to its last, or why its last bound is past the scene's last step.</summary>
    private static (IEnumerable<int> Steps, string? Error) RangeSteps(
        ScratchStepToken token,
        (string First, string Last) bounds,
        string scene,
        ScratchStepNames names
    )
    {
        if (!int.TryParse(bounds.Last, NumberStyles.None, CultureInfo.InvariantCulture, out int last) || last >= names.Count)
        {
            return ([], $"{StepsTokenLabel} {token} of {scene} reaches past its last step; {StepList(names)}");
        }

        int first = int.Parse(bounds.First, NumberStyles.None, CultureInfo.InvariantCulture);
        return (Enumerable.Range(first, Math.Max(0, last - first + 1)), null);
    }

    /// <summary>
    /// The step a key names: the index it spells, written without a sign or a leading zero, when there is such a step; else the
    /// one step of that name. <paramref name="label"/> names the key in a refusal.
    /// </summary>
    private static (int Step, string? Error) StepOf(string key, string label, string scene, ScratchStepNames names)
    {
        if (IsIndex(key, names.Count, out int index))
        {
            return (index, null);
        }

        int[] named = [.. Enumerable.Range(0, names.Count).Where(step => string.Equals(names[step], key, StringComparison.Ordinal))];
        return named.Length switch
        {
            1 => (named[0], null),
            0 => (-1, NoStep($"{label} \"{key}\"", scene, names)),
            _ => (-1, $"{label} \"{key}\" of {scene} names {named.Length} steps ({string.Join(", ", named)}); key it by index"),
        };
    }

    /// <summary>Whether a key spells an index below <paramref name="count"/>, without a sign or a leading zero.</summary>
    private static bool IsIndex(string key, int count, out int index) =>
        int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out index)
        && index < count
        && string.Equals(index.ToString(CultureInfo.InvariantCulture), key, StringComparison.Ordinal);

    private static string NoStep(string key, string scene, ScratchStepNames names) => $"{key} of {scene} matches no step; {StepList(names)}";

    /// <summary>The scene's steps as a refusal lists them, each as <see cref="ScratchStepNames.Lines"/> writes it.</summary>
    private static string StepList(ScratchStepNames names) =>
        names.Count == 0 ? "it has no steps" : "its steps are " + string.Join(", ", names.Lines());

    /// <summary>
    /// GetStepHidden(index)'s answer: true marks the step hidden; false or null leaves it shown, as a GetStepUntil answer of null
    /// is a paced step (a root whose GetStepHidden threw answers null, its error then the boot's); any other answer is why the
    /// scene is refused.
    /// </summary>
    internal static (bool Hidden, string? Error) HiddenAnswer(JsonNode? answer, int index) =>
        answer switch
        {
            null => (false, null),
            JsonValue value when value.TryGetValue(out bool hidden) => (hidden, null),
            _ => (false, $"{HiddenMethod}({index}) returned {answer.ToJsonString()}, not true or false"),
        };

    /// <summary>
    /// Whether the scene stops after <paramref name="step"/>: it failed, and either the call does not keep going or the step's
    /// own call failed (PlayStep, the wait, GetStatus or the marker: the game is likely gone or stuck).
    /// </summary>
    internal static bool StopsAfter(ScratchStep step, IReadOnlyList<ScratchPattern> patterns, bool keepGoing) =>
        ScratchVerdict.StepError(step, patterns) is not null && (!keepGoing || step.CallError is not null);

    /// <summary>A pace as the game milliseconds a step waits: at least 1.</summary>
    internal static int GameMs(double pace) => (int)Math.Max(1, Math.Round(pace * 1000));

    /// <summary>
    /// The bridge's wait_for parameters for step <paramref name="index"/>'s GetStepUntil answer: its condition checked as wait_for
    /// checks one, <paramref name="paceMs"/> its timeoutMs and its limitGameMs <see cref="LoadClock.BackstopFactor"/> times
    /// <paramref name="play"/>'s hang guard in game time at its time scale, the game time the bridge ends the wait at, not met: a
    /// backstop behind the caller's load-adjusted release, which neither a loaded machine nor a time scale above 1 reaches first
    /// (a parameter only the runner sends; the bridge bounds the wait by the caller's backstop, not by timeoutMs),
    /// PlayStep(index) on <paramref name="play"/>'s root its call, and options.screenshot
    /// when <paramref name="play"/> captures the step, so the met frame is shot. Neither parameters nor an error for null or an
    /// empty dictionary, a paced step; an error for any other answer, or a condition wait_for refuses.
    /// </summary>
    internal static (JsonObject? Wait, string? Error) UntilWait(JsonNode? answer, int index, string name, int paceMs, UntilPlay play)
    {
        if (answer is null || answer is JsonObject { Count: 0 })
        {
            return (null, null);
        }

        if (answer is not JsonObject condition)
        {
            return (null, NotACondition(UntilMethod, index, answer));
        }

        try
        {
            JsonObject wait = PlayStepWait(condition, index, paceMs, play.RootPath, play.Screenshot);
            wait[LimitGameMsField] = GameMs(play.Guard.TotalSeconds * LoadClock.BackstopFactor * play.TimeScale);
            return (wait, null);
        }
        catch (Exception e) when (e is McpException or JsonException)
        {
            return (null, UntilRefused(index, name, e.Message));
        }
    }

    /// <summary>
    /// The bridge's wait_for parameters for step <paramref name="index"/>'s GetStepShotUntil answer, for a paced step
    /// options.capture shoots: its condition checked as wait_for checks one, PlayStep(index) on <paramref name="rootPath"/> its
    /// call, options.screenshot set so the frame the condition first holds is the step's shot, and the step's pace of
    /// <paramref name="paceMs"/> game ms both its timeoutMs and its limitGameMs, so the bridge ends the wait unmet once the
    /// pace has run in game time from PlayStep, as a paced step's wait does. Neither parameters nor a refusal for null or an
    /// empty dictionary, a step shot at the end of its pace; the refusal, the shot's failure, for any other answer or a
    /// condition wait_for refuses.
    /// </summary>
    internal static (JsonObject? Wait, string? Refusal) ShotUntilWait(JsonNode? answer, int index, int paceMs, string rootPath)
    {
        if (answer is null || answer is JsonObject { Count: 0 })
        {
            return (null, null);
        }

        if (answer is not JsonObject condition)
        {
            return (null, NotACondition(ShotUntilMethod, index, answer));
        }

        try
        {
            JsonObject wait = PlayStepWait(condition, index, paceMs, rootPath, screenshot: true);
            wait[LimitGameMsField] = paceMs;
            return (wait, null);
        }
        catch (Exception e) when (e is McpException or JsonException)
        {
            return (null, $"{ShotUntilMethod}({index}) was refused: {e.Message}");
        }
    }

    /// <summary>Why a step's shot failed when its shot condition did not hold before its pace had run.</summary>
    internal const string ShotNotHeld = "its shot condition did not hold within its pace";

    /// <summary>A step method's refusal of an answer that is not a wait_for condition.</summary>
    private static string NotACondition(string method, int index, JsonNode answer) =>
        $"{method}({index}) returned {answer.ToJsonString()}, not a wait_for condition";

    /// <summary>
    /// wait_for parameters for <paramref name="condition"/> with PlayStep(index) on <paramref name="rootPath"/> as its call,
    /// <paramref name="paceMs"/> its timeoutMs and options.screenshot when <paramref name="screenshot"/>, without a preview.
    /// </summary>
    /// <exception cref="McpException">wait_for refuses the condition.</exception>
    /// <exception cref="JsonException">The condition does not read as one.</exception>
    private static JsonObject PlayStepWait(JsonObject condition, int index, int paceMs, string rootPath, bool screenshot)
    {
        WaitCondition until = condition.Deserialize<WaitCondition>(ToolJson.Options)!;
        MethodCall call = new(rootPath, "PlayStep", JsonSerializer.SerializeToElement(new[] { index }));
        WaitOptions options = new(Screenshot: screenshot ? true : null, Call: call);
        JsonObject wait = RuntimeTools.BuildWaitParameters(until, paceMs, options);
        wait.Remove(PreviewMaxWidthField);
        return wait;
    }

    /// <summary>
    /// How a step's until plays it: PlayStep on the scene's root, whether the met frame is shot (options.capture), the until's
    /// hang guard (<see cref="ScratchWalk.GuardOf"/>) and the time scale the scene's game runs at.
    /// </summary>
    internal readonly record struct UntilPlay(string RootPath, bool Screenshot, TimeSpan Guard, double TimeScale);

    /// <summary>
    /// Where a step's shot is kept: &lt;logFolder&gt;/scratch-&lt;runId&gt;/&lt;folder&gt;/&lt;index&gt;-&lt;step&gt;.png, the folder
    /// the scene's run shoots into (<see cref="ShotFolders"/>) and the step name made file-safe (<see cref="FileSafe"/>).
    /// </summary>
    internal static string ShotPath(string logFolder, string runId, string folder, int index, string step) =>
        Path.Combine(ShotRoot(logFolder, runId), folder, string.Create(CultureInfo.InvariantCulture, $"{index}-{FileSafe(step)}.png"));

    /// <summary>The folder every scene of a run keeps its shots under: &lt;logFolder&gt;/scratch-&lt;runId&gt;.</summary>
    internal static string ShotRoot(string logFolder, string runId) => Path.Combine(logFolder, $"scratch-{runId}");

    /// <summary>
    /// Creates the run's shot folder (<see cref="ShotRoot"/>) once before any scene starts, so no scene's failed copy owns it
    /// and removes it under the others; a failure is logged and the run goes on, each shot then failing as a step warning.
    /// </summary>
    internal static void CreateShotRoot(string logFolder, string runId, ILogger logger)
    {
        string root = ShotRoot(logFolder, runId);
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.ScratchShotFolderFailed(logger, e, root);
        }
    }

    /// <summary>
    /// The folder each scene of a run's plan shoots into, by its place in the plan: its name made file-safe, with .&lt;place&gt;
    /// added when another scene of the plan makes the same folder, compared without case as Windows compares folder names. The
    /// '.' keeps a numbered folder apart from every plain one, as <see cref="FileSafe"/> never gives a dot.
    /// </summary>
    internal static string[] ShotFolders(IReadOnlyList<ScratchScenePlan> scenes)
    {
        string[] safe = [.. scenes.Select(scene => FileSafe(scene.Name))];
        return
        [
            .. safe.Select(
                (folder, place) =>
                    safe.Count(other => string.Equals(other, folder, StringComparison.OrdinalIgnoreCase)) > 1
                        ? string.Create(CultureInfo.InvariantCulture, $"{folder}.{place}")
                        : folder
            ),
        ];
    }

    /// <summary>
    /// The folder a scene's replay alone shoots into, beside its first run's, so each run's result names its own files; the '.'
    /// keeps it apart from every scene's folder, as <see cref="FileSafe"/> never gives a dot.
    /// </summary>
    internal static string ReplayFolder(string folder) => folder + ".alone";

    /// <summary>
    /// A name as one safe path part: each character other than a letter, a digit, '-' or '_' becomes '_', cut to
    /// <see cref="MaxShotNameChars"/>; "step" for an empty name; a Windows device name (CON, NUL, COM1 and the like, in any
    /// case) gains a trailing '_'.
    /// </summary>
    internal static string FileSafe(string name)
    {
        string safe = new([.. name.Take(MaxShotNameChars).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        return safe.Length == 0 ? "step"
            : IsDeviceName(safe) ? safe + "_"
            : safe;
    }

    /// <summary>Whether <paramref name="name"/> is a Windows device name: CON, PRN, AUX, NUL, COM1-COM9 or LPT1-LPT9, in any case.</summary>
    private static bool IsDeviceName(string name)
    {
        string upper = name.ToUpperInvariant();
        return upper is "CON" or "PRN" or "AUX" or "NUL"
            || (
                upper.Length == 4
                && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                && upper[3] is >= '1' and <= '9'
            );
    }

    /// <summary>
    /// The warning naming the steps options.capture names that the run does not play, by index and name, ascending; null when
    /// it plays every one.
    /// </summary>
    internal static string? CaptureNotPlayed(IEnumerable<int> shots, IReadOnlyList<int> played, IReadOnlyList<string> names)
    {
        string[] unplayed = [.. shots.Except(played).Order().Select(step => string.Create(CultureInfo.InvariantCulture, $"{step} '{names[step]}'"))];
        return unplayed.Length == 0 ? null : $"options.capture names steps this run did not play: {string.Join(", ", unplayed)}";
    }

    /// <summary>
    /// The capture an until's wait_for reply names as screenshot.path, or why there is none: the bridge's warning (a frame not
    /// drawn, a headless game), else that the reply carried no screenshot.
    /// </summary>
    internal static (string? Capture, string? Reason) UntilShot(JsonNode? reply)
    {
        if (reply?["screenshot"]?["path"] is JsonValue path && path.TryGetValue(out string? capture))
        {
            return (capture, null);
        }

        return (
            null,
            reply?["warning"] is JsonValue warning && warning.TryGetValue(out string? text) ? text : "the until's reply carried no screenshot"
        );
    }

    /// <summary>
    /// A step shot's sizes and warning: the window's and the viewport's size its reply names, and for a shot that is not its
    /// window's size <see cref="PreviewTools.DescribeLetterbox"/> as <see cref="ScratchVerdict.ShotLetterboxed"/> says it. A step
    /// shot is never cropped or scaled, so no crop or scale check applies; all three are null for a reply that names none.
    /// </summary>
    internal static StepShotSizes ShotSizes(JsonNode? shot, int index, string name)
    {
        CaptureSize? window = RuntimeTools.ReadSize(shot?["window"]);
        return new StepShotSizes(window, RuntimeTools.ReadSize(shot?["viewport"]), LetterboxWarning(shot, window, index, name));
    }

    /// <summary>
    /// The warning of a step shot that is not its window's size, as <see cref="ScratchVerdict.ShotLetterboxed"/> says it; null
    /// when the reply names no window or no size, or the shot is the window's.
    /// </summary>
    private static string? LetterboxWarning(JsonNode? shot, CaptureSize? window, int index, string name) =>
        window is { } size
        && RuntimeTools.ReadInt(shot?["width"]) is { } width
        && RuntimeTools.ReadInt(shot?["height"]) is { } height
        && PreviewTools.DescribeLetterbox(width, height, size.Width, size.Height) is { } letterbox
            ? ScratchVerdict.ShotLetterboxed(index, name, letterbox)
            : null;

    /// <summary>
    /// Why a step failed when its until hung: its condition never held before its hang guard of <paramref name="guardSeconds"/>
    /// of load-adjusted time.
    /// </summary>
    internal static string UntilNotMet(int index, string name, double guardSeconds) =>
        $"step {index} '{name}': its until hung at its guard of {guardSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s of "
        + $"load-adjusted time (its condition never held); check the condition {UntilMethod} gives";

    /// <summary>
    /// Why a step whose until's wait ended unmet failed: <see cref="UntilNotMet"/> when it ended at its hang guard; else less than
    /// half its <paramref name="guardSeconds"/> at the scene's <paramref name="timeScale"/> ran in game time, so the game's clock
    /// stood still.
    /// </summary>
    internal static string UntilMissed(int index, string name, ScratchUntil until, double guardSeconds, double timeScale) =>
        until.GameMs * 2 < GameMs(guardSeconds * timeScale)
            ? $"step {index} '{name}': its until was not met within "
                + $"{guardSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s: the game's clock stood still (is it paused?)"
            : UntilNotMet(index, name, guardSeconds);

    /// <summary>Why a step failed when its until was refused, by the server's checks or the bridge.</summary>
    internal static string UntilRefused(int index, string name, string refusal) => $"step {index} '{name}': its until was refused: {refusal}";

    /// <summary>Why a step failed when its until's wait failed on the connection or the session rather than a refusal.</summary>
    internal static string UntilWaitFailed(int index, string name, string message) => $"step {index} '{name}': its until's wait failed: {message}";

    /// <summary>Why a step failed when the bridge refused its shot condition's wait.</summary>
    internal static string ShotRefused(int index, string name, string refusal) =>
        $"step {index} '{name}': its shot condition's wait was refused: {refusal}";

    /// <summary>Why a step failed when its shot condition's wait failed on the connection or the session rather than a refusal.</summary>
    internal static string ShotWaitFailed(int index, string name, string message) =>
        $"step {index} '{name}': its shot condition's wait failed: {message}";

    /// <summary>
    /// Why a step failed when the bridge refused its until's wait with <paramref name="error"/>: when the step's feed
    /// <paramref name="errors"/> logged that same error (PlayStep, the wait's call, raised it), that feed entry as a paced step's
    /// thrown PlayStep reads (its message, then its file and line when it has them); else the until's refusal, the bridge's
    /// wrapper cut from the text.
    /// </summary>
    /// <returns>The step's error, and the feed entry it was read from, or null when it is the until's refusal.</returns>
    internal static (string Error, ErrorEntry? Raised) UntilFailure(int index, string name, string error, IReadOnlyList<ErrorEntry> errors) =>
        RefusalFailure(error, errors, said => UntilRefused(index, name, said));

    /// <summary>
    /// Why a step failed when the bridge refused its shot condition's wait with <paramref name="error"/>, judged as an until's
    /// refusal is (<see cref="UntilFailure"/>): the feed entry that logged the same error, else the shot condition's refusal.
    /// </summary>
    /// <returns>The step's error, and the feed entry it was read from, or null when it is the shot condition's refusal.</returns>
    internal static (string Error, ErrorEntry? Raised) ShotWaitFailure(int index, string name, string error, IReadOnlyList<ErrorEntry> errors) =>
        RefusalFailure(error, errors, said => ShotRefused(index, name, said));

    /// <summary>
    /// A wait's refusal <paramref name="error"/>, the bridge's wrapper cut: the feed entry in <paramref name="errors"/> that
    /// logged that same error, as a paced step's thrown PlayStep reads; else <paramref name="refused"/> of the cut text.
    /// </summary>
    private static (string Error, ErrorEntry? Raised) RefusalFailure(string error, IReadOnlyList<ErrorEntry> errors, Func<string, string> refused)
    {
        string said = error.StartsWith(BridgeRefusalPrefix, StringComparison.Ordinal) ? error[BridgeRefusalPrefix.Length..] : error;
        ErrorEntry? raised = errors.FirstOrDefault(entry => entry.IsError && entry.Message == said);
        return raised is null ? (refused(said), null) : (ScratchVerdict.FeedError([raised])!, raised);
    }

    /// <summary>
    /// A step's failure and the feed entry it was read from, for a play the bridge did not refuse: when its until's wait ran and
    /// was not met and the step's window took an entry from the feed, the first entry the window took (an error, or a warning when
    /// scratch.warnings is "red"), as a paced step's thrown PlayStep reads; else the play's own error and no entry.
    /// </summary>
    internal static (string? Error, ErrorEntry? Raised) UnmetFailure(ScratchUntil? until, string? error, IReadOnlyList<ErrorEntry> errors) =>
        until is { Met: false } && errors.Count > 0 ? (ScratchVerdict.FeedError([errors[0]]), errors[0]) : (error, null);

    /// <summary>
    /// An exception from the until's wait as the step's play takes it: a connection or session failure (a disposed connection
    /// included) as why the step failed (<see cref="UntilWaitFailed"/>); any other refusal as the bridge's text, which the caller
    /// judges once the step's feed entries are in (<see cref="UntilFailure"/>).
    /// </summary>
    internal static (string? Error, string? Refusal) UntilCaught(int index, string name, Exception e) =>
        e is ObjectDisposedException or IOException or SessionException ? (UntilWaitFailed(index, name, e.Message), null) : (null, e.Message);

    /// <summary>
    /// An exception from the shot condition's wait as the step's play takes it: a connection or session failure (a disposed
    /// connection included) as why the step failed (<see cref="ShotWaitFailed"/>); any other refusal as the bridge's text, which
    /// the caller judges once the step's feed entries are in (<see cref="ShotWaitFailure"/>).
    /// </summary>
    internal static (string? Error, string? Refusal) ShotCaught(int index, string name, Exception e) =>
        e is ObjectDisposedException or IOException or SessionException ? (ShotWaitFailed(index, name, e.Message), null) : (null, e.Message);

    /// <summary>A met or timed-out wait_for reply's figures as a step's until carries them.</summary>
    internal static ScratchUntil UntilFigures(JsonNode? reply) =>
        new(
            reply?["met"] is JsonValue met && met.TryGetValue(out bool isMet) && isMet,
            Figure(reply, "gameMs"),
            Figure(reply, "elapsedMs"),
            Figure(reply, "frames")
        );

    /// <summary>A whole-number field of a wait_for reply; 0 when the reply lacks it.</summary>
    private static int Figure(JsonNode? reply, string field) => reply?[field] is JsonValue value && value.TryGetValue(out int figure) ? figure : 0;

    /// <summary>
    /// The status of GetStatus's text: its second line, or the text whole when it has one line; empty when the value is not a
    /// string.
    /// </summary>
    internal static string StatusLine(JsonNode? status)
    {
        string text = status is JsonValue value && value.TryGetValue(out string? held) ? held : string.Empty;
        string[] lines = text.Split('\n');
        return (lines.Length > 1 ? lines[1] : lines[0]).TrimEnd('\r');
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
    /// The feed's errors after <paramref name="mark"/>, and its warnings too when <paramref name="warnings"/> (the profile's
    /// scratch.warnings "red"), which the window takes, moving the mark past the last of them, so every entry it judges lands in
    /// exactly one window however late the bridge flushes it.
    /// </summary>
    internal static IReadOnlyList<ErrorEntry> TakeErrors(ErrorFeed feed, ref long mark, bool warnings)
    {
        IReadOnlyList<ErrorEntry> errors = feed.JudgedSince(mark, warnings);
        mark = errors.Count == 0 ? mark : Math.Max(mark, errors.Max(entry => entry.Seq));
        return errors;
    }

    /// <summary>
    /// The window's take through the bridge's end mark <paramref name="endMark"/> (<see cref="ErrorFeed.TakeThrough"/>): the
    /// entries after <paramref name="mark"/> up to the end mark, moving <paramref name="mark"/> to the end mark itself, not to
    /// the last entry taken, so the entries after it stay for the next window. Null, with <paramref name="mark"/> left where it
    /// was, when the feed never received the end mark: the window then takes everything
    /// (<see cref="TakeErrors(ErrorFeed, ref long, bool)"/>).
    /// </summary>
    internal static IReadOnlyList<ErrorEntry>? TakeErrors(ErrorFeed feed, ref long mark, bool warnings, string endMark)
    {
        (IReadOnlyList<ErrorEntry> errors, long? end) = feed.TakeThrough(mark, endMark, warnings);
        if (end is not { } at)
        {
            return null;
        }

        mark = Math.Max(mark, at);
        return errors;
    }

    /// <summary>
    /// A step that stops the walk with the feed's entries after its window's end (<paramref name="mark"/>, which moves past them)
    /// as its errorsAfterCut, since no later window takes them: what the runner's calls after the cut (its GetStatus) raised.
    /// They are shown only, never judged, so the step's stop reason stays its own.
    /// </summary>
    internal static ScratchStep WithErrorsAfterCut(ScratchStep step, ErrorFeed feed, ref long mark, bool warnings) =>
        TakeErrors(feed, ref mark, warnings) is { Count: > 0 } after ? step with { ErrorsAfterCut = after } : step;

    /// <summary>
    /// The entries of <paramref name="skipped"/>, in their order, that <paramref name="window"/> does not hold again: each matched
    /// on its type, message, file, line and function, not its sequence number, which the same raise gets anew the second time, one
    /// window entry matching at most one skipped entry; so two identical entries the read before the walk logged and the step's
    /// own read logged once leave the second over.
    /// </summary>
    internal static IReadOnlyList<ErrorEntry> Unreproduced(IReadOnlyList<ErrorEntry> skipped, IReadOnlyList<ErrorEntry> window)
    {
        List<ErrorEntry> remaining = [.. window];
        List<ErrorEntry> notReproduced = [];
        foreach (ErrorEntry entry in skipped)
        {
            int match = remaining.FindIndex(other => SameRaise(entry, other));
            if (match < 0)
            {
                notReproduced.Add(entry);
            }
            else
            {
                remaining.RemoveAt(match);
            }
        }

        return notReproduced;
    }

    /// <summary>Whether two feed entries say the same raise: their type, message, file, line and function, not their sequence number.</summary>
    private static bool SameRaise(ErrorEntry first, ErrorEntry second) =>
        first.Type == second.Type
        && first.Message == second.Message
        && first.File == second.File
        && first.Line == second.Line
        && first.Function == second.Function;

    /// <summary>Why a window's markers did not both arrive in time; a stdout marker missing beside stderr's points at buffering.</summary>
    internal static string MarkerTimeout(string label, TimeSpan allowance, bool stdoutArrived, bool stderrArrived)
    {
        string text = $"the {label} end marker reached stdout and stderr not both within {allowance.TotalSeconds:0} s";
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
            session = await _registry.ReserveScratchAsync(
                _plan.ProjectDir,
                SessionScene(_scene),
                _scene.ResPath,
                _plan.SessionPrefix,
                held: _observer is not null
            );
            _session = session;
            _observer?.SessionHeld(_index, session);
            LaunchRequest request = LaunchRequestFor(_plan, _scene, session.Quiet, session.ShutOutRealGamepads, session.Mute) with
            {
                OutputLogPath = ScratchRunLogs.PathFor(ProjectPrep.LogFolder(_plan.ProjectDir), session.Name, _runId),
            };
            _freeMemoryMb = FreeMemory.AvailableMb();
            LaunchResult launched = await session.LaunchAsync(request, cancellationToken);
            _logPath = launched.Log;
        }
        catch (SessionException e)
        {
            string name = _session?.Name ?? SessionRegistry.ScratchName(_plan.SessionPrefix, SessionScene(_scene));
            return Seen(name) with { Refusal = $"{_scene.ResPath} did not start: {e.Message}" };
        }

        ScratchObservation seen = await WalkAsync(session, cancellationToken);
        StopResult stopped = await StopTakingWatchAsync(session);
        (OutputPage stdout, OutputPage stderr, _) = Output(session);
        return seen with
        {
            ExitCode = stopped.GameExitCode ?? stopped.ExitCode,
            StopKillReason = stopped.Killed ? stopped.KillReason ?? "the stop had to kill the game" : null,
            StopWarning = stopped.Warning,
            ExitLines = [.. _paceLines, .. Window(stdout, stderr, _mark, EveryLine, _nonce)],
            Warnings = [.. _warnings],
            Watch = _watch,
        };
    }

    /// <summary>
    /// Stops the scene's game; with a watch peeked at (<see cref="CollectWatchAsync"/>, which set the handler for the bridge's
    /// watchFinal frame), keeps the watch <see cref="FinalWatch"/> gives once the stop returns.
    /// </summary>
    private async Task<StopResult> StopTakingWatchAsync(GodotSession session)
    {
        var stopped = (StopResult)await session.StopAsync(CancellationToken.None);
        if (_watchClock is { } clock)
        {
            // The stop disposed the connection, awaiting its read loop, so a frame the bridge sent has been handed on by now; with
            // no handler set (the game ended before the peek's handler could be), none was.
            _watch = FinalWatch(_watch, Volatile.Read(ref _finalWatch), clock, _warnings);
        }

        return stopped;
    }

    /// <summary>
    /// The scene's watch once its game has stopped: the timeline of <paramref name="final"/>, the watchFinal frame the bridge
    /// sent as the game quit, stamped with <paramref name="clock"/> (the game clock read at the shutdown's peek) and shaped as
    /// the watch tool's stop returns it; without that frame (a game that hung in its quit, a closed connection, a kill, a
    /// handler that could not be set) the timeline peeked at the shutdown, <paramref name="peeked"/>, its stopped set to
    /// "shutdown", and <see cref="WatchNotFinal"/> in <paramref name="warnings"/>.
    /// </summary>
    internal static JsonObject? FinalWatch(JsonObject? peeked, JsonObject? final, SessionClock clock, ICollection<string> warnings)
    {
        if (final?["timeline"] is not JsonObject timeline)
        {
            warnings.Add(WatchNotFinal);
            peeked?["stopped"] = StoppedAtShutdown;

            return peeked;
        }

        var copy = (JsonObject)timeline.DeepClone();
        WatchStamps.Stamp(copy, clock);
        return WatchTimeline.Shape(copy, WatchTimeline.MaxResultLength);
    }

    /// <summary>The request a scene's game launches with: its engine arguments, then its user arguments after --.</summary>
    internal static LaunchRequest LaunchRequestFor(ScratchPlan plan, ScratchScenePlan scene, bool quiet, bool shutOutRealGamepads, bool mute) =>
        new(
            plan.ProjectDir,
            scene.ResPath,
            EngineArgs(scene.TimeScale, scene.EngineArgs, plan.Windowed),
            scene.UserArgs,
            quiet,
            shutOutRealGamepads,
            Prepare: false
        )
        {
            Mute = mute,
            Audible = plan.Audible,
            ScratchUserData = plan.UserData,
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
            ScratchFailure kill = new(_inFlight, name, KillReason(e), string.Empty);
            await CollectWatchAsync(session, ReplyAllowance, CancellationToken.None);
            return Seen(session.Name) with { Kill = kill };
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

        if (!_plan.ListSteps && _names.Count > 0 && !ScratchVerdict.RedAtBoot(_bootErrors, _launchLines, _plan.Patterns))
        {
            await ReadUntilsAsync(session, cancellationToken);
            await StartWatchAsync(session, cancellationToken);
            LoadDeadline deadline = _registry.Clock.Start(_walk.Ceiling, cancellationToken);
            _deadline = deadline;
            await PlayToCeilingAsync(session, deadline);
            await CollectWatchAsync(session, _bounds.Call, cancellationToken);
        }

        return Seen(session.Name);
    }

    /// <summary>
    /// Plays the scene's steps on a token its ceiling or its memory watch cancels, with the watch read from the game's private
    /// memory while they play and stopped and disposed before the play ends, on every path.
    /// </summary>
    private async Task PlayToCeilingAsync(GodotSession session, LoadDeadline deadline)
    {
        using var play = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        ScratchMemoryWatch? memory = MemoryWatch(session, play);
        try
        {
            await PlayStepsAsync(session, play.Token);
        }
        finally
        {
            _memoryKill = memory?.KillReason;
            if (memory is not null)
            {
                await memory.DisposeAsync();
                memory.FinalRead();
            }

            _memoryPeak = memory?.PeakMegabytes;
        }
    }

    /// <summary>
    /// The watch of the scene's game, started: it tracks the game's peak memory for every scene, and checks its memory ceiling
    /// when it has one. Null only when the game reported no process id, which a scene with a ceiling is warned of.
    /// </summary>
    private ScratchMemoryWatch? MemoryWatch(GodotSession session, CancellationTokenSource play)
    {
        if (session.GameProcessId is not { } game)
        {
            if (_scene.MemoryCeiling is { } ceiling)
            {
                _warnings.Add(
                    "the scene's game reported no process id, so its memory ceiling of "
                        + $"{ceiling.Megabytes.ToString(CultureInfo.InvariantCulture)} MB was not watched"
                );
            }

            return null;
        }

        ScratchMemoryWatch watch = new(_scene.MemoryCeiling, _registry.Clock.Time, play);
        watch.Start(game);
        return watch;
    }

    /// <summary>
    /// Starts options.watch on the scene's session, its window left out the walk's <see cref="ScratchWalk.DefaultWatchWindow"/>;
    /// a start the bridge refuses or that fails on the connection is named in the scene's warnings, and the walk goes on.
    /// </summary>
    private async Task StartWatchAsync(GodotSession session, CancellationToken cancellationToken)
    {
        if (_plan.Watch is not { } watch)
        {
            return;
        }

        JsonObject parameters = RuntimeTools.BuildWatchParameters("start", watch.Tracks, watch.Window ?? _walk.DefaultWatchWindow, watch.Options);
        try
        {
            await session.SendAsync(WatchCommand, parameters, _bounds.Call + ReplyAllowance, cancellationToken, _bounds.Call);
            _watching = true;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or SessionException)
        {
            _warnings.Add($"options.watch did not start: {e.Message}");
        }
    }

    /// <summary>
    /// Peeks at the watch the walk started, leaving it running or held for the bridge to finish as the game quits, and keeps
    /// its timeline so far, stamped and shaped as the watch tool's stop returns it, within <paramref name="timeout"/>, and the
    /// clock it was stamped with, which the final timeline is stamped with too (<see cref="FinalWatch"/>); a peek that fails or
    /// times out is named in the scene's warnings. Nothing when no watch runs.
    /// </summary>
    private async Task CollectWatchAsync(GodotSession session, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_watching)
        {
            return;
        }

        _watching = false;
        try
        {
            JsonNode? reply = await session.SendAsync(WatchCommand, new JsonObject { ["action"] = "peek" }, timeout, cancellationToken);
            JsonObject timeline =
                reply?.DeepClone() as JsonObject
                ?? throw new InvalidOperationException($"the bridge's watch reply is not an object: {reply?.ToJsonString() ?? "null"}");
            SessionClock clock = await session.RefreshClockAsync(cancellationToken);
            WatchStamps.Stamp(timeline, clock);
            _watch = WatchTimeline.Shape(timeline, WatchTimeline.MaxResultLength);
            _watchClock = clock;
            // Set before the shutdown is sent: the bridge sends the frame in its exit work, and the read loop hands it on at once.
            // A game that ended since the peek takes no handler, and FinalWatch then keeps the peeked timeline with its warning.
            _ = session.OnWatchFinal(frame => Volatile.Write(ref _finalWatch, frame));
        }
        catch (Exception e)
            when (e is InvalidOperationException or IOException or SessionException or TimeoutException
                || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            )
        {
            _warnings.Add($"options.watch could not be collected: {e.Message}");
        }
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

    /// <summary>
    /// Null when the scene's root has every protocol method, noting whether it also has GetStepUntil, GetStepShotUntil (asked only
    /// for a run that captures) and GetStepHidden; else why not, naming the root and what it lacks.
    /// </summary>
    private async Task<string?> CheckProtocolAsync(GodotSession session, CancellationToken cancellationToken)
    {
        List<string> missing = [];
        foreach (string method in Protocol)
        {
            (bool found, string? error) = await HasMethodAsync(session, method, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            if (!found)
            {
                missing.Add(method);
            }
        }

        if (missing.Count > 0)
        {
            return $"the scene root {_rootPath} lacks {string.Join(", ", missing)}; a scratch scene implements PlayStep(int), GetStepCount(), "
                + "GetStepName(int) and GetStatus()";
        }

        (_hasUntil, string? untilError) = await HasMethodAsync(session, UntilMethod, cancellationToken);
        if (untilError is not null)
        {
            return untilError;
        }

        if (_plan.Capture is not null)
        {
            (_hasShotUntil, string? shotUntilError) = await HasMethodAsync(session, ShotUntilMethod, cancellationToken);
            if (shotUntilError is not null)
            {
                return shotUntilError;
            }
        }

        (_hasHidden, string? hiddenError) = await HasMethodAsync(session, HiddenMethod, cancellationToken);
        return hiddenError;
    }

    /// <summary>Whether the scene's root has <paramref name="method"/>, or why has_method failed.</summary>
    private async Task<(bool Found, string? Error)> HasMethodAsync(GodotSession session, string method, CancellationToken cancellationToken)
    {
        (JsonNode? has, string? error) = await CallAsync(session, "has_method", [method], cancellationToken);
        return (has is JsonValue value && value.TryGetValue(out bool found) && found, error);
    }

    /// <summary>
    /// Reads the step count and every step's name, then closes the launch window: its lines are the boot's, as the error
    /// feed's entries so far are the boot's.
    /// </summary>
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

        (_launchLines, _, error) = await CloseWindowAsync(session, "launch", printed: false, cancellationToken);
        _bootErrors = TakeErrors(session.Errors, ref _feedMark, _plan.WarningsRed);
        return error;
    }

    /// <summary>
    /// Reads every step's name, then each step's pace and the steps to play, a partial run adding its scene's setup steps to the
    /// ones it selects; null, or why a name, a step pace, a setup step or a step to play could not be had.
    /// </summary>
    private async Task<string?> ReadNamesAsync(GodotSession session, int total, CancellationToken cancellationToken)
    {
        string? nameError = await ReadEachNameAsync(session, total, cancellationToken);
        if (nameError is not null)
        {
            return nameError;
        }

        (ScratchPace[] paces, string? paceError, IReadOnlyList<string> paceWarnings) = StepPaces(_scene, NamedSteps);
        _warnings.AddRange(paceWarnings);
        string? captureError = paceError ?? ResolveCapture();
        if (captureError is not null)
        {
            return captureError;
        }

        IReadOnlyList<ScratchStepToken>? tokens = _scene.StepTokens ?? _plan.StepsFor(_scene.Name);
        if (tokens is null)
        {
            _walk = new ScratchWalk(paces, [.. Enumerable.Range(0, _names.Count)], _scene.TimeScale)
            {
                Given = _scene.Ceiling,
                ExitHold = _scene.ExitHold,
                Floor = _bounds.Floor,
                UntilGuard = _bounds.UntilGuard,
            };
            return null;
        }

        (ScratchSelection selection, string? selectError) = SelectSteps(_scene, tokens, NamedSteps);
        _selected = selection.Selected;
        _setupOnly = selection.Setup;
        _walk = new ScratchWalk(paces, selection.Played, _scene.TimeScale)
        {
            Given = _scene.Ceiling,
            ExitHold = _scene.ExitHold,
            Floor = _bounds.Floor,
            UntilGuard = _bounds.UntilGuard,
        };
        if (selectError is null)
        {
            WarnOfUnplayedShots();
        }

        return selectError;
    }

    /// <summary>Adds the scene warning naming the steps options.capture.steps names that the run does not play, if any.</summary>
    private void WarnOfUnplayedShots()
    {
        if (_plan.Capture?.StepsFor(_scene.Name) is not null && CaptureNotPlayed(_shots, _walk.Played, _names) is { } unplayed)
        {
            _warnings.Add(unplayed);
        }
    }

    /// <summary>The scene's step names and the steps its root marks hidden, as read so far.</summary>
    private ScratchStepNames NamedSteps => new(_names, _hidden);

    /// <summary>Resolves options.capture's steps against the scene's names into the steps to shoot; null, or why a token names no step.</summary>
    private string? ResolveCapture()
    {
        if (_plan.Capture is not { } capture)
        {
            return null;
        }

        (int[]? steps, string? error) = CaptureSteps(capture, _scene.Name, NamedSteps);
        _shots = [.. steps ?? Enumerable.Range(0, _names.Count)];
        return error;
    }

    /// <summary>
    /// Reads every step's name and, for a root that has GetStepHidden, whether it marks the step hidden; null, or why a call
    /// failed or GetStepHidden gave neither true, false nor null.
    /// </summary>
    private async Task<string?> ReadEachNameAsync(GodotSession session, int total, CancellationToken cancellationToken)
    {
        for (int index = 0; index < total; index++)
        {
            (JsonNode? name, string? error) = await CallAsync(session, "GetStepName", [index], cancellationToken);
            error ??= _hasHidden ? await ReadHiddenAsync(session, index, cancellationToken) : null;
            if (error is not null)
            {
                return error;
            }

            _names.Add(StepName(name));
        }

        return null;
    }

    /// <summary>GetStepHidden(index), marking the step hidden when it answers true; null, or why the call or its answer failed.</summary>
    private async Task<string?> ReadHiddenAsync(GodotSession session, int index, CancellationToken cancellationToken)
    {
        (JsonNode? answer, string? error) = await CallAsync(session, HiddenMethod, [index], cancellationToken);
        (bool hidden, string? answerError) = error is null ? HiddenAnswer(answer, index) : (false, error);
        if (hidden)
        {
            _hidden.Add(index);
        }

        return answerError;
    }

    /// <summary>
    /// A scene's steps as a run resolves them, from <paramref name="tokens"/> against the scene's <paramref name="names"/>: every
    /// step of a call that gave no steps, and otherwise the steps the tokens select with the setup steps the profile declares for
    /// the scene added to them; or why a setup key or a token names no single step, the selection then empty and nothing to play.
    /// </summary>
    internal static (ScratchSelection Selection, string? Error) SelectSteps(
        ScratchScenePlan scene,
        IReadOnlyList<ScratchStepToken>? tokens,
        ScratchStepNames names
    ) => tokens is null ? (new ScratchSelection(null, [], [.. Enumerable.Range(0, names.Count)]), null) : SelectedSteps(scene, tokens, names);

    /// <summary>A partial run's selection, its setup steps and their union; or why a setup key or a token names no single step.</summary>
    private static (ScratchSelection Selection, string? Error) SelectedSteps(
        ScratchScenePlan scene,
        IReadOnlyList<ScratchStepToken> tokens,
        ScratchStepNames names
    )
    {
        (int[] played, string? stepsError) = ResolveSteps(tokens, scene.Name, names);
        (int[] setup, string? setupError) = SetupSteps(scene, played, names);
        string? error = setupError ?? stepsError;
        return error is not null
            ? (new ScratchSelection(null, [], []), error)
            : (new ScratchSelection(played, [.. setup.Except(played)], [.. played.Union(setup).Order()]), null);
    }

    /// <summary>
    /// The steps <paramref name="scene"/>'s setup keys name, ascending, each once: its scene-wide list, and the list of each step
    /// of <paramref name="selected"/> that has one, a step of a list bringing no list of its own; or why a key names no single
    /// step, or two steps. Every key is resolved on every partial run, so one naming no step of the scene turns it red then,
    /// selected or not; a full run resolves none of them.
    /// </summary>
    internal static (int[] Steps, string? Error) SetupSteps(ScratchScenePlan scene, int[] selected, ScratchStepNames names)
    {
        SortedSet<int> steps = [];
        string? error = AddSetupKeys(scene.Setup.Always, SetupLabel, scene.Name, steps, names);
        foreach ((string key, IReadOnlyList<string> list) in scene.Setup.Steps)
        {
            (int step, string? keyError) = StepOf(key, SetupStepLabel, scene.Name, names);
            SortedSet<int>? added = selected.Contains(step) ? steps : null;
            error ??= keyError ?? AddSetupKeys(list, $"{SetupStepLabel} \"{key}\"", scene.Name, added, names);
        }

        return error is not null ? ([], error) : ([.. steps], null);
    }

    /// <summary>
    /// The steps <paramref name="keys"/> name, added to <paramref name="steps"/>, or with it null resolved alone so a typo is
    /// refused whether or not the step whose list holds it is selected; null, or why a key names no single step, or two steps.
    /// </summary>
    private static string? AddSetupKeys(IReadOnlyList<string> keys, string label, string scene, SortedSet<int>? steps, ScratchStepNames names)
    {
        foreach (string key in keys)
        {
            (int step, string? error) = StepOf(key, label, scene, names);
            if (error is not null)
            {
                return error;
            }

            steps?.Add(step);
        }

        return null;
    }

    private static string StepName(JsonNode? name) =>
        name is JsonValue value && value.TryGetValue(out string? text) ? text : name?.ToJsonString() ?? string.Empty;

    /// <summary>
    /// Plays each step to play, in ascending order, each waiting its own pace, until one fails; after the last one played,
    /// holds the scene's exit hold, else that step's pace once more, the longer of its game time and its wall time, which the
    /// bridge measures from that step's cut (<see cref="HoldOf"/>).
    /// </summary>
    private async Task PlayStepsAsync(GodotSession session, CancellationToken limit)
    {
        foreach (int index in _walk.Played)
        {
            _inFlight = index;
            _observer?.StepStarted(_index, index, _names[index], _names.Count, _walk.RemainingSecondsAtMost(index));
            ScratchStep step = await PlayStepAsync(session, index, limit);
            if (StopsAfter(step, _plan.Patterns, _plan.KeepGoing))
            {
                _steps.Add(WithErrorsAfterCut(step, session.Errors, ref _feedMark, _plan.WarningsRed));
                return;
            }

            _steps.Add(step);
        }

        _inFlight = -1;
        await PaceAfterAsync(session, _walk.AfterGameMs, limit);
    }

    /// <summary>
    /// Plays one step: paced (PlayStep, then a wait of its pace in game time; for a shot step with a shot condition, inside one
    /// wait_for on it), or for a step whose GetStepUntil gives a condition, inside one wait_for on it; then GetStatus and the
    /// window. The bridge's refusal of either wait is judged once the step's feed entries are in (<see cref="UntilFailure"/>,
    /// <see cref="ShotWaitFailure"/>); a connection or session failure is the step's error at once.
    /// </summary>
    private async Task<ScratchStep> PlayStepAsync(GodotSession session, int index, CancellationToken limit)
    {
        int gameMs = GameMs(_walk.Paces[index].Seconds);
        (JsonObject? wait, string? untilError) = _hasUntil ? await ReadUntilAsync(session, index, gameMs, limit) : (null, null);
        StepPlay played =
            untilError is not null ? new StepPlay(untilError, null, null)
            : wait is null ? await PlayPacedStepAsync(session, index, gameMs, limit)
            : await PlayInsideUntilAsync(session, index, wait, limit);
        StepShot shot = await ShotAsync(session, index, played, limit);
        (JsonNode? status, string? statusError) = await CallAsync(session, "GetStatus", [], limit);
        (List<string> lines, long dropped, string? markerError, IReadOnlyList<ErrorEntry> window) = await EndWindowAsync(
            session,
            $"step {index}",
            played.EndMarked,
            limit
        );
        (string? error, ErrorEntry? raised) =
            played.BridgeError is not { } bridgeError ? UnmetFailure(played.Until, played.Error, window)
            : played.ShotWaited ? ShotWaitFailure(index, _names[index], bridgeError, window)
            : UntilFailure(index, _names[index], bridgeError, window);
        string statusLine = StatusLine(status);
        RecordStatus(session, index, statusLine);
        return new ScratchStep(index, _names[index], gameMs)
        {
            Until = played.Until,
            UntilWaited = wait is not null,
            CallError = error ?? statusError ?? markerError,
            CallErrorEntry = raised,
            Status = statusLine,
            Errors = WithUnreproducedEarly(index, window),
            Lines = lines,
            Dropped = dropped,
            Screenshot = shot.Path,
            Window = shot.Window,
            Viewport = shot.Viewport,
            Warning = shot.Warning,
        };
    }

    /// <summary>
    /// The entries a step's window took from the feed, followed by the entries the step's read before the walk passed over that the
    /// window does not hold again (<see cref="Unreproduced"/>), so an entry another thread or a timer logged during that read still
    /// fails the step; the step's claim on them is dropped, so no window takes them twice. The list is the step's errors only: the
    /// failure functions judge its own window's entries, so an early entry never stands in for why the step failed.
    /// </summary>
    private IReadOnlyList<ErrorEntry> WithUnreproducedEarly(int index, IReadOnlyList<ErrorEntry> errors)
    {
        if (!_earlySkipped.Remove(index, out List<ErrorEntry>? skipped))
        {
            return errors;
        }

        IReadOnlyList<ErrorEntry> notReproduced = Unreproduced(skipped, errors);
        return notReproduced.Count == 0 ? errors : [.. errors, .. notReproduced];
    }

    /// <summary>
    /// Writes a step's status into the scene's whole-output log beside the game's own lines, so an agent reading the log sees what
    /// the game answered at each step; a status that only repeats the step's name says nothing and is left out.
    /// </summary>
    private void RecordStatus(GodotSession session, int index, string statusLine)
    {
        if (statusLine.Length > 0 && !string.Equals(statusLine, _names[index], StringComparison.Ordinal))
        {
            session.WriteToOutputLog($"[godot-mcp] step {index} {_names[index]}: {statusLine}");
        }
    }

    /// <summary>
    /// The shot of a step options.capture names whose play went through: an until's or a shot condition's met frame, which its
    /// wait_for captured, or for a paced step the frame after its pace, taken as take_screenshot takes one; copied to the run's
    /// folder (<see cref="ShotPath"/>). Its path with the window's and the viewport's size and the warning they give, or the
    /// step's warning when it failed, the shot failure its play already gave included; nothing for a step not shot.
    /// </summary>
    private async Task<StepShot> ShotAsync(GodotSession session, int index, StepPlay played, CancellationToken limit)
    {
        if (!_shots.Contains(index) || played.Error is not null || played.BridgeError is not null)
        {
            return new StepShot(null, null, null, null);
        }

        (string? capture, JsonNode? reply, string? reason) = await CaptureAsync(session, played, limit);
        if (capture is null)
        {
            return new StepShot(null, null, null, ScratchVerdict.ShotFailed(index, _names[index], reason!));
        }

        StepShotSizes sizes = ShotSizes(reply, index, _names[index]);
        string destination = ShotPath(ProjectPrep.LogFolder(_plan.ProjectDir), _runId, _shotFolder, index, _names[index]);
        try
        {
            _places ??= OutputPlaces.Find(_plan.ProjectDir, _registry.Logger);
            ScreenshotTarget.SaveToRunFolder(capture, destination, _places);
            return new StepShot(destination, sizes.Window, sizes.Viewport, sizes.Warning);
        }
        catch (McpException e)
        {
            return new StepShot(null, null, null, ScratchVerdict.ShotFailed(index, _names[index], ScreenshotTarget.ReasonOf(e)));
        }
    }

    /// <summary>
    /// A shot step's capture and the reply that holds it, or why there is none: the failure its play already gave its shot (a
    /// shot condition refused or not held within the pace); the met frame its until's or its shot condition's wait_for captured;
    /// else, for a paced step, the frame after its pace (<see cref="PacedShotAsync"/>).
    /// </summary>
    private async Task<(string? Capture, JsonNode? Shot, string? Reason)> CaptureAsync(GodotSession session, StepPlay played, CancellationToken limit)
    {
        if (played.ShotFailure is { } failed)
        {
            return (null, null, failed);
        }

        if (played.Until is null && played.Reply is null)
        {
            return await PacedShotAsync(session, limit);
        }

        (string? capture, string? reason) = UntilShot(played.Reply);
        return (capture, played.Reply?["screenshot"], reason);
    }

    /// <summary>
    /// The bridge's screenshot of the next drawn frame, as take_screenshot asks for it: the capture's path and the reply that holds
    /// it, or why it failed.
    /// </summary>
    private async Task<(string? Capture, JsonNode? Shot, string? Reason)> PacedShotAsync(GodotSession session, CancellationToken limit)
    {
        JsonObject parameters = RuntimeTools.BuildScreenshotParameters(ScreenshotMode.PathOnly, null, 0);
        try
        {
            JsonNode? reply = await session.SendAsync("screenshot", parameters, _bounds.Call + ReplyAllowance, limit, _bounds.Call);
            return reply?["path"] is JsonValue path && path.TryGetValue(out string? capture)
                ? (capture, reply, null)
                : (null, null, $"the screenshot reply names no file: {reply?.ToJsonString() ?? "null"}");
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or SessionException)
        {
            return (null, null, e.Message);
        }
    }

    /// <summary>
    /// A paced step's play: for a step options.capture shoots whose root's GetStepShotUntil gives a condition, inside one wait_for
    /// on it (<see cref="PlayToShotAsync"/>); else PlayStep and its pace (<see cref="PlayPacedAsync"/>), a shot condition the
    /// runner refused, or a failed read of it, then being the shot's failure.
    /// </summary>
    private async Task<StepPlay> PlayPacedStepAsync(GodotSession session, int index, int gameMs, CancellationToken limit)
    {
        (JsonObject? shotWait, string? refusal) =
            _hasShotUntil && _shots.Contains(index) ? await ReadShotUntilAsync(session, index, gameMs, limit) : (null, null);
        if (shotWait is not null)
        {
            return await PlayToShotAsync(session, index, gameMs, shotWait, limit);
        }

        (string? error, bool? endMarked) = await PlayPacedAsync(session, index, gameMs, limit);
        return new StepPlay(error, null, null) { ShotFailure = refusal, EndMarked = endMarked };
    }

    /// <summary>
    /// GetStepShotUntil(index) as the shot wait's parameters (<see cref="ShotUntilWait"/>), with the C# helper a state condition
    /// reads a C# node's state with, as wait_for adds it; neither for a step shot at the end of its pace; or why the read failed
    /// or the answer was refused.
    /// </summary>
    private async Task<(JsonObject? Wait, string? Refusal)> ReadShotUntilAsync(GodotSession session, int index, int gameMs, CancellationToken limit)
    {
        (JsonNode? answer, string? error) = await CallAsync(session, ShotUntilMethod, [index], limit);
        if (error is not null)
        {
            return (null, error);
        }

        (JsonObject? wait, string? refusal) = ShotUntilWait(answer, index, gameMs, _rootPath);
        RuntimeTools.AddConditionStateHelper(wait, _csharp, session.ProjectDir);
        return (wait, refusal);
    }

    /// <summary>
    /// One wait_for on a paced step's shot condition, PlayStep its call, ended when the condition holds, whose met frame is the
    /// shot, or once the step's pace has run in game time, released as the pace's own wait is: when met, the bridge holds the
    /// wait until the pace has run (endMarkerAtGameMs) and ends the step's window there (<see cref="EndMarker"/>); when not, the
    /// shot fails (<see cref="ShotNotHeld"/>) and the wait's end ends the window. Either way a game clock that did not run the
    /// pace fails the step as a paced step's wait does. A wait that failed on the connection or the session is the step's error
    /// (<see cref="ShotWaitFailed"/>); the bridge's refusal of it is judged once the step's feed entries are in
    /// (<see cref="ShotWaitFailure"/>).
    /// </summary>
    private async Task<StepPlay> PlayToShotAsync(GodotSession session, int index, int gameMs, JsonObject wait, CancellationToken limit)
    {
        TimeSpan release = _bounds.Release(gameMs, _scene.TimeScale);
        EndsWindow(wait, EndMarker($"step {index}"), HoldOf(index));
        wait["endMarkerAtGameMs"] = gameMs;
        try
        {
            JsonNode? reply = await session.SendAsync("wait_for", wait, release + ReplyAllowance, limit, release);
            ScratchUntil figures = UntilFigures(reply);
            string? stoodStill = figures.GameMs < gameMs ? PaceNotMet(gameMs, release) : null;
            return new StepPlay(stoodStill, null, null)
            {
                Reply = figures.Met ? reply : null,
                ShotFailure = figures.Met ? null : ShotNotHeld,
                ShotWaited = true,
                EndMarked = EndMarked(reply),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or SessionException)
        {
            (string? error, string? refusal) = ShotCaught(index, _names[index], e);
            return new StepPlay(error, refusal, null) { ShotWaited = true, EndMarked = UnmarkedUnlessClosed(e) };
        }
    }

    /// <summary>
    /// One wait of the step's pace in game time whose call is PlayStep, so the pace starts in PlayStep's frame and no round trip
    /// lies inside the step's window, which the wait's end ends (<see cref="EndMarker"/>): null, or why the call or the wait
    /// failed, and whether the bridge ended the window.
    /// </summary>
    private async Task<(string? Error, bool? EndMarked)> PlayPacedAsync(GodotSession session, int index, int gameMs, CancellationToken limit)
    {
        JsonObject call = new()
        {
            ["node"] = _rootPath,
            ["method"] = "PlayStep",
            ["args"] = new JsonArray(index),
        };
        (string? error, bool closed, bool endMarked) = await WaitAsync(session, gameMs, index, call, limit);
        return (error, closed ? null : endMarked);
    }

    /// <summary>
    /// GetStepUntil(index) as the until's wait parameters (none for a paced step), with the C# helper a state condition reads a
    /// C# node's state with, as wait_for adds it; or why the step fails.
    /// </summary>
    private async Task<(JsonObject? Wait, string? Error)> ReadUntilAsync(GodotSession session, int index, int gameMs, CancellationToken limit)
    {
        (JsonNode? answer, string? error) = _untilAnswers.TryGetValue(index, out JsonNode? cached)
            ? (cached, null)
            : await CallAsync(session, UntilMethod, [index], limit);
        if (error is not null)
        {
            return (null, error);
        }

        UntilPlay play = new(_rootPath, _shots.Contains(index), _walk.GuardOf(index), _scene.TimeScale);
        (JsonObject? wait, string? untilError) = UntilWait(answer, index, _names[index], gameMs, play);
        RuntimeTools.AddConditionStateHelper(wait, _csharp, session.ProjectDir);
        return (wait, untilError);
    }

    /// <summary>
    /// On a root that has GetStepUntil, reads it once for each played step before the ceiling's clock starts and keeps each
    /// answer for that step's play (<see cref="ReadUntilAsync"/>, which judges it there): the walk counts a step whose answer is
    /// neither null nor an empty dictionary at the until's hang guard, and so a step whose read failed, which is read again at
    /// its play. Each read names its step as the one in flight, so a hang there names it.
    /// </summary>
    private async Task ReadUntilsAsync(GodotSession session, CancellationToken cancellationToken)
    {
        if (!_hasUntil)
        {
            return;
        }

        HashSet<int> untils = [];
        bool passedOver = false;
        foreach (int index in _walk.Played)
        {
            _inFlight = index;
            (bool until, bool logged) = await ReadUntilEarlyAsync(session, index, cancellationToken);
            passedOver |= logged;
            if (until)
            {
                untils.Add(index);
            }
        }

        _inFlight = -1;
        _walk = _walk with { UntilSteps = untils };
        if (passedOver)
        {
            await PassOverReadLinesAsync(session, cancellationToken);
        }
    }

    /// <summary>
    /// Closes an output window over the reads before the walk, one of which logged a judged entry (an error, or a warning when
    /// scratch.warnings is "red"), and passes its lines over, each logged to stderr, so the engine's own line for that entry never
    /// lands in the first step's window.
    /// </summary>
    private async Task PassOverReadLinesAsync(GodotSession session, CancellationToken cancellationToken)
    {
        (List<string> lines, _, string? markerError) = await CloseWindowAsync(session, "until reads", printed: false, cancellationToken);
        if (markerError is not null)
        {
            EarlyUntilWindowUnclosed(_registry.Logger, markerError, null);
        }

        foreach (string line in lines)
        {
            EarlyUntilLinePassedOver(_registry.Logger, line, null);
        }
    }

    /// <summary>
    /// Reads step <paramref name="index"/>'s GetStepUntil before the walk and keeps its answer, unless the read failed on the
    /// bridge or the feed logged a judged entry across it (a script error inside the call, or a warning when scratch.warnings is
    /// "red"): such a read keeps nothing, so the step reads it again at its play, where that entry lands in its own window; the
    /// entries it logged are passed over here, so the first step's window never takes them, and each is logged to stderr. They are
    /// kept for the step, which takes at its play every one its own read did not log again (<see cref="Unreproduced"/>). Whether
    /// the ceiling counts the step at the guard (a condition, or a failed read), and whether the read logged a judged entry.
    /// </summary>
    private async Task<(bool Until, bool Logged)> ReadUntilEarlyAsync(GodotSession session, int index, CancellationToken cancellationToken)
    {
        long mark = _feedMark;
        (JsonNode? answer, string? error) = await CallAsync(session, UntilMethod, [index], cancellationToken);
        IReadOnlyList<ErrorEntry> logged = session.Errors.JudgedSince(mark, _plan.WarningsRed);
        if (logged.Count > 0)
        {
            _feedMark = Math.Max(_feedMark, logged.Max(entry => entry.Seq));
            _earlySkipped[index] = [.. logged];
            foreach (ErrorEntry entry in logged)
            {
                EarlyUntilReadLogged(_registry.Logger, index, entry.IsError ? "an error" : "a warning", entry.Message, null);
            }

            return (true, true);
        }

        if (error is null)
        {
            _untilAnswers[index] = answer;
        }

        return (error is not null || answer is not (null or JsonObject { Count: 0 }), false);
    }

    /// <summary>
    /// One wait_for on the step's until, PlayStep its call, ended when its condition holds, however long past the step's pace,
    /// and released only at the step's own hang guard in load-adjusted time (<see cref="ScratchBounds.UntilReleaseOf"/>): its
    /// figures and, when not met, why the step fails (<see cref="UntilMissed"/>); the bridge's refusal, judged by the caller; or
    /// why the wait failed on the connection.
    /// </summary>
    private async Task<StepPlay> PlayInsideUntilAsync(GodotSession session, int index, JsonObject wait, CancellationToken limit)
    {
        TimeSpan guard = _walk.GuardOf(index);
        TimeSpan release = _bounds.UntilReleaseOf(guard);
        EndsWindow(wait, EndMarker($"step {index}"), HoldOf(index));
        try
        {
            JsonNode? reply = await session.SendAsync("wait_for", wait, release + ReplyAllowance, limit, release);
            ScratchUntil until = UntilFigures(reply);
            string? notMet = until.Met ? null : UntilMissed(index, _names[index], until, guard.TotalSeconds, _scene.TimeScale);
            return new StepPlay(notMet, null, until) { Reply = reply, EndMarked = EndMarked(reply) };
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or SessionException)
        {
            (string? error, string? refusal) = UntilCaught(index, _names[index], e);
            return new StepPlay(error, refusal, null) { EndMarked = UnmarkedUnlessClosed(e) };
        }
    }

    /// <summary>
    /// How a step's play went: why it failed, or the bridge's refusal of its until's wait or its shot condition's wait, the
    /// until's figures, its wait_for reply, which holds the met frame's screenshot for a captured step (its until's, or its shot
    /// condition's), why its shot already failed in the play, whether the play waited on its shot condition, which says
    /// whose wait a refusal is, and whether the bridge ended the step's window as the step's last wait ended.
    /// </summary>
    private readonly record struct StepPlay(string? Error, string? BridgeError, ScratchUntil? Until)
    {
        public JsonNode? Reply { get; init; }

        public string? ShotFailure { get; init; }

        public bool ShotWaited { get; init; }

        /// <summary>Whether the bridge ended the step's window as its last wait ended; null when no wait carrying the marker ran.</summary>
        public bool? EndMarked { get; init; }
    }

    /// <summary>
    /// The exit hold after the last step, the scene's exit hold else that step's pace, the longer of its game time and its wall
    /// time, which the bridge runs in game time from that step's cut and ends by printing the exit's "pace" end marker and
    /// marking the feed (<see cref="HoldOf"/>): an error in the feed during it (or a warning when scratch.warnings is "red"), or a
    /// hold whose end never came, fails the scene's exit. The server sends no wait for it; it waits for the bridge's marker
    /// (<see cref="AwaitHoldAsync"/>), and when the marker never comes the window falls back as a failed wait's did.
    /// </summary>
    private async Task PaceAfterAsync(GodotSession session, int gameMs, CancellationToken limit)
    {
        (string? waitError, bool closed, IReadOnlyList<ErrorEntry>? marked) = await AwaitHoldAsync(session, gameMs, limit);
        string? markerError;
        IReadOnlyList<ErrorEntry> errors;
        if (marked is not null)
        {
            (_paceLines, _, markerError) = await CloseWindowAsync(session, "pace", printed: true, limit);
            errors = marked;
        }
        else
        {
            (_paceLines, _, markerError, errors) = await EndWindowAsync(session, "pace", closed ? null : false, limit);
        }

        string? feedError = ScratchVerdict.FeedError(errors);
        _afterClosed = closed && feedError is null;
        // A closed connection's hold error comes before the marker's, which then fails only because the game is gone.
        _afterError = feedError ?? waitError ?? markerError;
    }

    /// <summary>
    /// Waits for the end of the exit hold the bridge runs from the last step's cut: both "pace" end marker lines in stdout and
    /// stderr and the feed's mark, whose entries since the last window it then takes (<see cref="TakeErrors(ErrorFeed, ref long,
    /// bool, string)"/>); null error and closed false. Bounded as a wait_for of the hold was, at its game time at the scene's time
    /// scale plus the reply allowance, on the load clock: past it (a game paused through the hold), no entries and why the marker
    /// never came. Ends at once, no entries, closed, with a failed wait's text, when the session's bridge connection closes first
    /// (<see cref="Wire.BridgeConnection.Closed"/>).
    /// </summary>
    private async Task<(string? Error, bool Closed, IReadOnlyList<ErrorEntry>? Marked)> AwaitHoldAsync(
        GodotSession session,
        int gameMs,
        CancellationToken limit
    )
    {
        string marker = EndMarker("pace");
        Task closed = BridgeClosed(session);
        TimeSpan bound = _bounds.Release(gameMs, _scene.TimeScale) + ReplyAllowance;
        using LoadDeadline deadline = _registry.Clock.Start(bound, limit);
        while (true)
        {
            bool expired = deadline.Expired;
            (OutputPage stdout, OutputPage stderr, _) = Output(session);
            bool printed = Markers(stdout, stderr, _mark, marker) is not null;
            if (printed && TakeErrors(session.Errors, ref _feedMark, _plan.WarningsRed, marker) is { } marked)
            {
                return (null, false, marked);
            }

            if (closed.IsCompleted)
            {
                return (PaceFailed(gameMs, BridgeClosedMessage), true, null);
            }

            if (expired)
            {
                bool stdoutArrived = MarkerLine(stdout, _mark.Stdout, marker) is not null;
                return (MarkerTimeout("pace", bound, stdoutArrived, MarkerLine(stderr, _mark.Stderr, marker) is not null), false, null);
            }

            await Task.Delay(MarkerPoll, limit);
        }
    }

    /// <summary>
    /// The session's bridge connection's end (<see cref="Wire.BridgeConnection.Closed"/>): its run's or its attached game's, or done at
    /// once when it has none, the game being gone.
    /// </summary>
    private static Task BridgeClosed(GodotSession session) =>
        session.CurrentRun?.Connection?.Closed ?? session.AttachedBridgeClosed ?? Task.CompletedTask;

    /// <summary>The exit hold a step's wait carries to the bridge: its length in game milliseconds and the marker that ends it.</summary>
    internal readonly record struct HoldEnd(int GameMs, string EndMarker);

    /// <summary>
    /// The exit hold the wait playing step <paramref name="index"/> carries: for the last step the walk plays, the hold after it
    /// (<see cref="ScratchWalk.AfterGameMs"/>, ended by the exit's "pace" marker); for any other, none.
    /// </summary>
    private HoldEnd? HoldOf(int index) =>
        _walk.Played.Count > 0 && index == _walk.Played[^1] ? new HoldEnd(_walk.AfterGameMs, EndMarker("pace")) : null;

    /// <summary>
    /// Has <paramref name="wait"/> end its step's window at <paramref name="endMarker"/> and, with <paramref name="hold"/>, run
    /// the exit hold on from that end: the bridge's server-only endMarker, holdGameMs and holdEndMarker.
    /// </summary>
    internal static void EndsWindow(JsonObject wait, string endMarker, HoldEnd? hold)
    {
        wait["endMarker"] = endMarker;
        if (hold is { } exit)
        {
            wait["holdGameMs"] = exit.GameMs;
            wait["holdEndMarker"] = exit.EndMarker;
        }
    }

    /// <summary>
    /// Ends the window <paramref name="label"/> of a step or of the exit, whose last wait has ended: when the bridge ended it
    /// (<paramref name="endMarked"/>, the wait's reply said so) and the feed holds its mark, at the marker lines the bridge
    /// printed and the feed's mark, so what the game printed or raised after the wait's end, the runner's own calls since
    /// included, stays for the next window; else as before, at a marker the game is told to print now, taking every feed entry
    /// so far. A window whose last wait carried its marker (<paramref name="endMarked"/> not null) and still falls back is logged
    /// (<see cref="FallbackReason"/>). Returns the window's lines, how many the output buffer no longer held, why its markers
    /// failed, and its entries.
    /// </summary>
    private async Task<(List<string> Lines, long Dropped, string? Error, IReadOnlyList<ErrorEntry> Errors)> EndWindowAsync(
        GodotSession session,
        string label,
        bool? endMarked,
        CancellationToken limit
    )
    {
        string marker = EndMarker(label);
        IReadOnlyList<ErrorEntry>? marked = endMarked == true ? TakeErrors(session.Errors, ref _feedMark, _plan.WarningsRed, marker) : null;
        if (FallbackReason(endMarked, marked is not null, marker) is { } why)
        {
            WindowFellBack(_registry.Logger, label, why, null);
        }

        (List<string> lines, long dropped, string? error) = await CloseWindowAsync(session, label, marked is not null, limit);
        return (lines, dropped, error, marked ?? TakeErrors(session.Errors, ref _feedMark, _plan.WarningsRed));
    }

    /// <summary>
    /// Why a window whose last wait was sent with its marker falls back to the runner's marker: the bridge did not end the wait
    /// (<paramref name="endMarked"/> false: the reply lacked endMarked, or the wait failed), or it did but the feed holds no mark
    /// <paramref name="marker"/> (<paramref name="markTaken"/> false); null when the window ends at the bridge's marker, or when no
    /// wait carried the marker (<paramref name="endMarked"/> null: no wait ran).
    /// </summary>
    internal static string? FallbackReason(bool? endMarked, bool markTaken, string marker) =>
        endMarked switch
        {
            false => "the bridge did not end the wait",
            true when !markTaken => $"the feed has no mark {marker}",
            _ => null,
        };

    /// <summary>
    /// The marker line that ends the window <paramref name="label"/>, which the bridge prints when it ends the window itself
    /// (the server-only endMarker of the window's last wait_for) and the game is told to print otherwise (<see cref="MarkerScript"/>).
    /// </summary>
    private string EndMarker(string label) => $"{MarkerPrefix}{label} end {_nonce}";

    /// <summary>
    /// A marked wait that threw: not ended by the bridge (false), or null when the game closed the connection, whose window then
    /// falls back as expected, unreported.
    /// </summary>
    private static bool? UnmarkedUnlessClosed(Exception e) => e is IOException ? null : false;

    /// <summary>Whether a wait_for reply says the bridge ended the window as the wait ended (its result's endMarked).</summary>
    private static bool EndMarked(JsonNode? reply) => reply?["endMarked"] is JsonValue value && value.TryGetValue(out bool marked) && marked;

    /// <summary>
    /// Has the game print a marker line to stdout and to stderr, unless the bridge has already printed it
    /// (<paramref name="printed"/>), waits until both have arrived, and returns the lines of the window they close, the marker
    /// lines left out, and how many of its lines the output buffer no longer held; the next window starts after the markers.
    /// When the markers fail or do not arrive in time, the window takes every line so far, with why.
    /// </summary>
    private async Task<(List<string> Lines, long Dropped, string? Error)> CloseWindowAsync(
        GodotSession session,
        string label,
        bool printed,
        CancellationToken limit
    )
    {
        string marker = EndMarker(label);
        string? error = printed ? null : (await ScriptAsync(session, MarkerScript(marker), limit)).Error;
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
                MarkerTimeout(
                    label,
                    _bounds.Markers,
                    MarkerLine(stdout, after.Stdout, marker) is not null,
                    MarkerLine(stderr, after.Stderr, marker) is not null
                )
            )
            : (lines, dropped, null);
    }

    /// <summary>
    /// The markers' line numbers once both streams hold them, or null when <see cref="ScratchBounds.Markers"/> of load-adjusted
    /// time passes first, on the registry's clock as every other wait of the walk: the server's own output readers starve with
    /// the server on a loaded machine, so a wall-time wait would end before they have read lines the game already printed.
    /// The output is read once more after the deadline is seen to expire, so lines read meanwhile still count.
    /// </summary>
    private async Task<LineMark?> WaitForMarkersAsync(GodotSession session, string marker, CancellationToken limit)
    {
        using LoadDeadline allowance = _registry.Clock.Start(_bounds.Markers, limit);
        while (true)
        {
            bool expired = allowance.Expired;
            (OutputPage stdout, OutputPage stderr, _) = Output(session);
            if (Markers(stdout, stderr, _mark, marker) is { } found)
            {
                return found;
            }

            if (expired)
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
    private Task<(JsonNode? Value, string? Error)> ScriptAsync(GodotSession session, string source, CancellationToken limit) =>
        SendAsync(session, "run_script", new JsonObject { ["source"] = source }, limit);

    /// <summary>A bridge command's value, or why it failed. A timeout or the ceiling is left to throw.</summary>
    private async Task<(JsonNode? Value, string? Error)> SendAsync(
        GodotSession session,
        string command,
        JsonObject parameters,
        CancellationToken limit
    )
    {
        try
        {
            JsonNode? reply = await session.SendAsync(command, parameters, _bounds.Call + ReplyAllowance, limit, _bounds.Call);
            return (reply?["value"]?.DeepClone(), null);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or SessionException)
        {
            return (null, e.Message);
        }
    }

    /// <summary>
    /// wait_for {gameMs} playing step <paramref name="index"/>: null once the game's clock has run that long; else why it did not,
    /// and whether that was the game closing the bridge connection. Released at that game time at the scene's time scale plus the
    /// step allowance. The wait's end, met or not, ends the step's window (the bridge prints its marker and marks the error feed),
    /// and the reply says whether it did; the last played step's wait also carries the exit hold (<see cref="HoldOf"/>).
    /// </summary>
    private async Task<(string? Error, bool Closed, bool EndMarked)> WaitAsync(
        GodotSession session,
        int gameMs,
        int index,
        JsonObject? call,
        CancellationToken limit
    )
    {
        TimeSpan release = _bounds.Release(gameMs, _scene.TimeScale);
        JsonObject parameters = new()
        {
            ["gameMs"] = gameMs,
            ["kind"] = "gameMs",
            ["timeoutMs"] = (int)release.TotalMilliseconds,
        };
        EndsWindow(parameters, EndMarker($"step {index}"), HoldOf(index));
        if (call is not null)
        {
            parameters["call"] = call;
            parameters["keepRaised"] = true;
        }

        try
        {
            JsonNode? reply = await session.SendAsync("wait_for", parameters, release + ReplyAllowance, limit, release);
            string? notMet = reply?["met"] is JsonValue met && met.TryGetValue(out bool isMet) && !isMet ? PaceNotMet(gameMs, release) : null;
            return (notMet, false, EndMarked(reply));
        }
        catch (IOException e)
        {
            return (PaceFailed(gameMs, e.Message), true, false);
        }
        catch (Exception e) when (e is InvalidOperationException or SessionException)
        {
            return (PacedWaitFailed(gameMs, call is not null, e.Message), false, false);
        }
    }

    /// <summary>
    /// Why a pace wait of <paramref name="gameMs"/> failed with <paramref name="message"/>: when it carried PlayStep as its call
    /// (<paramref name="called"/>), PlayStep's failure in the words a call_method of it failed with before the pace was one wait
    /// with it (the bridge's refusal of the wait read as its refusal of call_method); any wait without a call, the pace's failure.
    /// </summary>
    internal static string PacedWaitFailed(int gameMs, bool called, string message) =>
        called ? $"PlayStep failed: {AsCallMethodRefusal(message)}" : PaceFailed(gameMs, message);

    /// <summary><paramref name="message"/> with the bridge's wait_for refusal prefix read as its call_method one.</summary>
    private static string AsCallMethodRefusal(string message) =>
        message.StartsWith(BridgeRefusalPrefix, StringComparison.Ordinal)
            ? $"The bridge refused 'call_method': {message[BridgeRefusalPrefix.Length..]}"
            : message;

    /// <summary>Why a step failed when the game's clock did not run its pace of <paramref name="gameMs"/> before its release.</summary>
    private static string PaceNotMet(int gameMs, TimeSpan release) =>
        $"wait_for {gameMs} game ms was not met within {release.TotalSeconds:0.#} s: the game's clock stood still (is it paused?)";

    /// <summary>Why a step failed when the wait of its pace of <paramref name="gameMs"/> failed with <paramref name="message"/>.</summary>
    private static string PaceFailed(int gameMs, string message) => $"wait_for {gameMs} game ms failed: {message}";

    private string KillReason(Exception e)
    {
        if (_memoryKill is { } memory)
        {
            return memory;
        }

        if (e is TimeoutException)
        {
            return e.Message;
        }

        return _walk.CeilingReason(_deadline?.Reason == DeadlineReason.Backstop ? _deadline.BackstopClause() : string.Empty);
    }

    private ScratchObservation Seen(string session) =>
        new(_scene.Name, session, _scene.Pace, _names.Count)
        {
            Log = _logPath,
            LaunchLines = _launchLines,
            BootErrors = _bootErrors,
            Steps = _steps,
            StepPaceReasons = [.. _walk.Paces.Select(pace => pace.Reason)],
            AfterError = _afterError,
            AfterClosed = _afterClosed,
            Selected = _selected,
            Setup = _setupOnly.Length == 0 ? null : _setupOnly,
            StepNames = NamedSteps,
            TimeScale = _scene.TimeScale == _plan.TimeScale ? null : _scene.TimeScale,
            ExitHold = _scene.ExitHold,
            PeakPrivateMb = _memoryPeak,
            FreeMemoryMb = _freeMemoryMb,
            Warnings = [.. _warnings],
            Watch = _watch,
            Listed = _plan.ListSteps,
            Batch = _scene.Batch,
            Batches = _scene.Batches,
        };

    private static int WholeNumber(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double number) && number >= 0 && number <= int.MaxValue && number == Math.Floor(number)
            ? (int)number
            : -1;

    /// <summary>The held lines of each stream, as <see cref="Bounded"/> cuts them for the call's options.detailChars.</summary>
    private (OutputPage Stdout, OutputPage Stderr, LineMark Total) Output(GodotSession session)
    {
        (OutputPage stdout, OutputPage stderr, _, _) = session.GetRawOutput();
        return Bounded(stdout, stderr, _plan.DetailChars);
    }

    /// <summary>
    /// Each raw line cut to the output buffer's page cut, or to <paramref name="detailChars"/> when that is longer, so only a
    /// call asking for more sees longer lines; and the number of each stream's last held line, taken from the snapshot itself
    /// so the window and the mark always agree (0 for a stream with no lines).
    /// </summary>
    internal static (OutputPage Stdout, OutputPage Stderr, LineMark Total) Bounded(OutputPage stdout, OutputPage stderr, int detailChars)
    {
        int longest = Math.Max(OutputBuffer.MaxLineLength, detailChars);
        OutputPage cutOut = stdout with { Lines = [.. stdout.Lines.Select(line => OutputBuffer.Cut(line, longest))] };
        OutputPage cutErr = stderr with { Lines = [.. stderr.Lines.Select(line => OutputBuffer.Cut(line, longest))] };
        return (cutOut, cutErr, new LineMark(LastLine(cutOut), LastLine(cutErr)));
    }

    private static long LastLine(OutputPage page) => page.FirstLine is long first ? first + page.Lines.Count - 1 : 0;
}
