using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Server.Session;

/// <summary>
/// A scratch scene to play: its name (the file's, without .tscn), its res:// path, its pace in seconds, the user arguments it
/// starts with, the reason the profile knows it to fail, if any, the profile's reason for its pace, if any, the paces of
/// single steps, keyed by a step's index or its name, and the time scale its game runs at.
/// </summary>
internal sealed record ScratchScenePlan(string Name, string ResPath, double Pace, IReadOnlyList<string> UserArgs)
{
    public string? Known { get; init; }

    public string? PaceReason { get; init; }

    /// <summary>
    /// The paces of single steps and why, keyed by a step's index or its name, resolved once the scene has named its steps.
    /// </summary>
    public IReadOnlyDictionary<string, ScratchPace> StepPaces { get; init; } = ReadOnlyDictionary<string, ScratchPace>.Empty;

    /// <summary>The time scale the scene's game runs at: the call's options.timeScale, or 1 when its profile pace opts out.</summary>
    public required double TimeScale { get; init; }

    /// <summary>The largest pace the scene holds: its own, or one of its steps'.</summary>
    public double LargestPace => StepPaces.Values.Select(step => step.Seconds).Append(Pace).Max();
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
/// many scenes play at once, whether the call listed its scenes rather than leaving them to the folder, the prefix each
/// scene's session is named with, whether a scene plays on past a failed step, the steps options.steps selects, and the
/// call's time scale.
/// </summary>
internal sealed record ScratchPlan(
    string ProjectDir,
    IReadOnlyList<ScratchScenePlan> Scenes,
    IReadOnlyList<ScratchPattern> Patterns,
    bool Prepare,
    bool Details
)
{
    public required int Parallel { get; init; }

    public required bool Listed { get; init; }

    public required string SessionPrefix { get; init; }

    /// <summary>Whether a scene plays on past a step that failed on a pushed error or a matching line.</summary>
    public required bool KeepGoing { get; init; }

    /// <summary>options.steps' tokens, which every scene resolves against its own step names; null plays every step.</summary>
    public required IReadOnlyList<ScratchStepToken>? Steps { get; init; }

    /// <summary>options.timeScale, the time scale the scenes' games run at unless a scene's profile pace opts out; 1 by default.</summary>
    public required double TimeScale { get; init; }
}

/// <summary>
/// A scene's step paces by index, the steps a run plays of them, ascending, and the time scale its game runs at: what its
/// ceiling, its pace after the last step played and its kill reason count. A step's pace is game time, so at time scale N it
/// takes pace / N of wall time; the pace after the last step played counts in wall time above time scale 1 and in game time
/// below it.
/// </summary>
internal sealed record ScratchWalk(IReadOnlyList<ScratchPace> Paces, IReadOnlyList<int> Played, double TimeScale)
{
    public static readonly ScratchWalk None = new([], [], 1);

    /// <summary>The ceiling of the played steps, from each one's pace; see <see cref="ScratchRun.Ceiling"/>.</summary>
    public TimeSpan Ceiling => ScratchRun.Ceiling([.. Played.Select(step => Paces[step].Seconds)], TimeScale);

    /// <summary>
    /// The game milliseconds waited after the last step played: that step's own pace in game time or in wall time, whichever is
    /// longer, so pace × the time scale above 1.
    /// </summary>
    public int AfterGameMs => ScratchRun.GameMs(Paces[Played[^1]].Seconds * Math.Max(1, TimeScale));

    /// <summary>
    /// The most wall seconds the scene's steps can still take once <paramref name="step"/> starts: the pace of each played step
    /// not yet started and the step's own, each divided by the time scale, and the pace after the last step played, in wall time
    /// above time scale 1 and in game time below it; the launch and the prep are left out.
    /// </summary>
    public double RemainingSecondsAtMost(int step) =>
        (Played.Where(played => played >= step).Sum(played => Paces[played].Seconds) / TimeScale)
        + (Paces[Played[^1]].Seconds / Math.Min(1, TimeScale));

    /// <summary>Why a scene was killed at its ceiling, with <paramref name="backstop"/> appended to the budget's terms.</summary>
    public string CeilingReason(string backstop)
    {
        string seconds = Ceiling.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        string each = Played.Count == 1 ? "its 1 step's" : $"each of its {Played.Count} steps'";
        string scaled = TimeScale == 1 ? string.Empty : $" at time scale {TimeScale.ToString(CultureInfo.InvariantCulture)}";
        string after = TimeScale > 1 ? "once more in wall time" : "once more";
        return $"the scene passed its ceiling of {seconds} s of load-adjusted time "
            + $"({each} pace{scaled} + 10 s, the last step's pace {after}, and 10 s for the markers){backstop}, so its game was stopped";
    }
}

/// <summary>A line number in each of a session's stdout and stderr: the edge of a step's window.</summary>
internal readonly record struct LineMark(long Stdout, long Stderr);

/// <summary>
/// Plays one scratch scene in a headless game session of its own, as an agent would with the runtime tools: launches it,
/// asks the game for its current scene's path, checks that root for the scratch protocol, reads its step names and takes the
/// error feed's entries so far, the boot's: an error among them fails the scene before any step plays (the lines printed so far
/// are never judged). A step pace keyed to no single step, or an options.steps token naming none, refuses the scene before any
/// step plays. Then per step it plays (every step, or those options.steps selects, in ascending order) it calls PlayStep, waits
/// the step's pace in game time (for a root that has GetStepUntil and a step it gives a condition, plays the step inside one
/// wait_for on that condition instead, PlayStep the wait's call, until it holds or the pace has passed in game time), reads
/// GetStatus and takes the feed's entries since the last window, stopping at the first failed step, or with the plan's
/// keep-going only at one whose call failed; after the last step played it waits that step's pace once more, the longer of its
/// game time and its wall time, then stops the game gracefully and reads the lines it printed after the steps.
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

    private const string BridgeRefusalPrefix = "The bridge refused 'wait_for': ";

    /// <summary>The until wait's parameter, sent by the runner alone, of the game milliseconds the bridge ends it at, not met.</summary>
    private const string LimitGameMsField = "limitGameMs";

    /// <summary>The start of every marker line the runner has the game print; such lines are never judged or listed.</summary>
    internal const string MarkerPrefix = "[godot-mcp] scratch ";

    private const string PaceKeyLabel = "pace.steps key";
    private const string StepsTokenLabel = "options.steps token";

    private const string CurrentSceneScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\tvar scene: Node = scene_tree.current_scene\n"
        + "\treturn \"\" if scene == null else str(scene.get_path())\n";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReplyAllowance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StepAllowance = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MarkerPoll = TimeSpan.FromMilliseconds(10);
    private static readonly LineMark EveryLine = new(long.MaxValue, long.MaxValue);

    private readonly SessionRegistry _registry;
    private readonly CSharpBridge _csharp;
    private readonly ScratchPlan _plan;
    private readonly ScratchScenePlan _scene;
    private readonly IScratchRunObserver? _observer;
    private readonly int _index;
    private readonly string _nonce = Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _names = [];
    private readonly List<ScratchStep> _steps = [];
    private ScratchWalk _walk = ScratchWalk.None;
    private GodotSession? _session;
    private LoadDeadline? _deadline;
    private LineMark _mark;
    private int _inFlight = -1;
    private string _rootPath = string.Empty;
    private List<string> _launchLines = [];
    private IReadOnlyList<ErrorEntry> _bootErrors = [];
    private List<string> _paceLines = [];
    private string? _afterError;
    private long _feedMark;
    private bool _hasUntil;

    private ScratchRun(RunContext context, int index)
    {
        _registry = context.Registry;
        _csharp = context.CSharp;
        _plan = context.Plan;
        _scene = context.Plan.Scenes[index];
        _observer = context.Observer;
        _index = index;
    }

    /// <summary>What every scene of one run shares: the registry, the C# bridge, the plan, and who is told as the run plays.</summary>
    private sealed record RunContext(SessionRegistry Registry, CSharpBridge CSharp, ScratchPlan Plan, IScratchRunObserver? Observer);

    /// <summary>
    /// Prepares the project once when the plan says so, then plays the scenes, starting them in <see cref="StartOrder"/> at most
    /// the plan's parallel at once, and judges each; a scene red or killed beside others is then played once more alone, one at a time in
    /// order. The result lists the scenes in the plan's order, whatever finished first, with what the prep did.
    /// <paramref name="csharp"/> readies the C# helper a state until reads a C# node's state with. <paramref name="observer"/>,
    /// a background run's, is told each scene's start, session, steps and verdict, and each replay's start; a synchronous run
    /// passes null.
    /// </summary>
    /// <exception cref="SessionException">The prep failed.</exception>
    public static async Task<ScratchRunResult> RunAsync(
        SessionRegistry registry,
        CSharpBridge csharp,
        ScratchPlan plan,
        IScratchRunObserver? observer,
        CancellationToken cancellationToken
    )
    {
        RunContext context = new(registry, csharp, plan, observer);
        PrepResult prep = plan.Prepare ? await registry.PrepareFolderAsync(plan.ProjectDir, cancellationToken) : PrepResult.Skipped;
        ScratchSceneResult[] scenes = await PlayBesideAsync(context, cancellationToken);
        for (int index = 0; index < scenes.Length; index++)
        {
            if (ScratchVerdict.PlaysAgainAlone(scenes[index], plan.Parallel))
            {
                observer?.ReplayStarted(index);
                ScratchSceneResult replay = await PlayOneAsync(context, index, cancellationToken);
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
                results[index] = await PlayOneAsync(context, index, cancellationToken);
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

    private static async Task<ScratchSceneResult> PlayOneAsync(RunContext context, int index, CancellationToken cancellationToken)
    {
        ScratchPlan plan = context.Plan;
        ScratchScenePlan scene = plan.Scenes[index];
        ScratchObservation seen = await new ScratchRun(context, index).PlayAsync(cancellationToken);
        return ScratchVerdict.Judge(seen, new ScratchRules(plan.Patterns, scene.Known, scene.PaceReason, plan.Details));
    }

    /// <summary>
    /// The ceiling of a scene's steps, load-adjusted, from each step's pace in game time at <paramref name="timeScale"/>: each
    /// step's pace / timeScale and 10 s, the last step's pace once more for the pace after it, in wall time above time scale 1
    /// and in game time below it, and 10 s more for the launch and pace markers.
    /// </summary>
    internal static TimeSpan Ceiling(IReadOnlyList<double> paces, double timeScale) =>
        TimeSpan.FromSeconds(
            paces.Sum(pace => (pace / timeScale) + StepAllowance.TotalSeconds)
                + (paces.Count > 0 ? paces[^1] / Math.Min(1, timeScale) : 0)
                + StepAllowance.TotalSeconds
        );

    /// <summary>
    /// The engine arguments a scene's game launches with: headless, and at a <paramref name="timeScale"/> other than 1 Godot's
    /// --time-scale, which scales every process and physics step.
    /// </summary>
    internal static string[] EngineArgs(double timeScale) =>
        timeScale == 1 ? ["--headless"] : ["--headless", "--time-scale", timeScale.ToString(CultureInfo.InvariantCulture)];

    /// <summary>
    /// The real time a wait of <paramref name="gameMs"/> game milliseconds is released at when the game's clock stands still: that
    /// game time at <paramref name="timeScale"/>, plus the step allowance.
    /// </summary>
    internal static TimeSpan Release(int gameMs, double timeScale) => TimeSpan.FromMilliseconds(gameMs / timeScale) + StepAllowance;

    /// <summary>
    /// Each of the steps <paramref name="names"/> lists' pace and why: the scene's, or a step pace keyed by the step's index, else
    /// by its name; or why a key names no single step, or two keys name one step.
    /// </summary>
    internal static (ScratchPace[] Paces, string? Error) StepPaces(ScratchScenePlan scene, IReadOnlyList<string> names)
    {
        ScratchPace scenePace = new(scene.Pace, scene.PaceReason);
        ScratchPace[] paces = [.. names.Select(_ => scenePace)];
        Dictionary<int, string> keyOf = [];
        foreach ((string key, ScratchPace pace) in scene.StepPaces)
        {
            (int step, string? error) = StepOf(key, PaceKeyLabel, scene.Name, names);
            error ??= keyOf.TryGetValue(step, out string? other)
                ? $"pace.steps keys \"{other}\" and \"{key}\" of {scene.Name} both name step {step}; keep one"
                : null;
            if (error is not null)
            {
                return ([], error);
            }

            keyOf[step] = key;
            paces[step] = pace;
        }

        return (paces, null);
    }

    /// <summary>
    /// The steps <paramref name="tokens"/> select among those <paramref name="names"/> lists, ascending, each once; or why a token
    /// selects no step. A number is an index; a string is an index spelled plainly, else a step's exact name, else a range.
    /// </summary>
    internal static (int[] Indices, string? Error) ResolveSteps(IReadOnlyList<ScratchStepToken> tokens, string scene, IReadOnlyList<string> names)
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

    private static (IEnumerable<int> Steps, string? Error) TokenSteps(ScratchStepToken token, string scene, IReadOnlyList<string> names)
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
        IReadOnlyList<string> names
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
    private static (int Step, string? Error) StepOf(string key, string label, string scene, IReadOnlyList<string> names)
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

    private static string NoStep(string key, string scene, IReadOnlyList<string> names) => $"{key} of {scene} matches no step; {StepList(names)}";

    private static string StepList(IReadOnlyList<string> names) =>
        names.Count == 0 ? "it has no steps" : "its steps are " + string.Join(", ", names.Select((name, index) => $"{index}: {name}"));

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
    /// checks one, <paramref name="paceMs"/> its timeoutMs and its limitGameMs (the game time the bridge ends the wait at, not
    /// met, a parameter only the runner sends), and PlayStep(index) on <paramref name="rootPath"/> its call. Neither parameters
    /// nor an error for null or an empty dictionary, a paced step; an error for any other answer, or a condition wait_for refuses.
    /// </summary>
    internal static (JsonObject? Wait, string? Error) UntilWait(JsonNode? answer, int index, string name, int paceMs, string rootPath)
    {
        if (answer is null || answer is JsonObject { Count: 0 })
        {
            return (null, null);
        }

        if (answer is not JsonObject condition)
        {
            return (null, $"{UntilMethod}({index}) returned {answer.ToJsonString()}, not a wait_for condition");
        }

        try
        {
            WaitCondition until = condition.Deserialize<WaitCondition>(ToolJson.Options)!;
            MethodCall play = new(rootPath, "PlayStep", JsonSerializer.SerializeToElement(new[] { index }));
            JsonObject wait = RuntimeTools.BuildWaitParameters(until, paceMs, new WaitOptions(Call: play));
            wait[LimitGameMsField] = paceMs;
            return (wait, null);
        }
        catch (Exception e) when (e is McpException or JsonException)
        {
            return (null, UntilRefused(index, name, e.Message));
        }
    }

    /// <summary>Why a step failed when its until was not met within its pace.</summary>
    internal static string UntilNotMet(int index, string name, double pace) =>
        $"step {index} '{name}': its until was not met within its pace of {pace.ToString(CultureInfo.InvariantCulture)} s; "
        + $"give it a longer scratch.pace, or check the condition {UntilMethod} gives";

    /// <summary>
    /// Why a step whose until's wait ended unmet failed: not met within its pace when the bridge counted the whole pace of game
    /// time; else the release of <paramref name="releaseSeconds"/> real seconds ended the wait first, the game's clock stood still.
    /// </summary>
    internal static string UntilMissed(int index, string name, ScratchUntil until, double pace, double releaseSeconds) =>
        until.GameMs >= GameMs(pace)
            ? UntilNotMet(index, name, pace)
            : $"step {index} '{name}': its until was not met within "
                + $"{releaseSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s: the game's clock stood still (is it paused?)";

    /// <summary>Why a step failed when its until was refused, by the server's checks or the bridge.</summary>
    internal static string UntilRefused(int index, string name, string refusal) => $"step {index} '{name}': its until was refused: {refusal}";

    /// <summary>Why a step failed when its until's wait failed on the connection or the session rather than a refusal.</summary>
    internal static string UntilWaitFailed(int index, string name, string message) => $"step {index} '{name}': its until's wait failed: {message}";

    /// <summary>
    /// Why a step failed when the bridge refused its until's wait with <paramref name="error"/>: when the step's feed
    /// <paramref name="errors"/> logged that same error (PlayStep, the wait's call, raised it), that feed entry as a paced step's
    /// thrown PlayStep reads (its message, then its file and line when it has them); else the until's refusal, the bridge's
    /// wrapper cut from the text.
    /// </summary>
    /// <returns>The step's error, and the feed entry it was read from, or null when it is the until's refusal.</returns>
    internal static (string Error, ErrorEntry? Raised) UntilFailure(int index, string name, string error, IReadOnlyList<ErrorEntry> errors)
    {
        string said = error.StartsWith(BridgeRefusalPrefix, StringComparison.Ordinal) ? error[BridgeRefusalPrefix.Length..] : error;
        ErrorEntry? raised = errors.FirstOrDefault(entry => entry.IsError && entry.Message == said);
        return raised is null ? (UntilRefused(index, name, said), null) : (ScratchVerdict.FeedError([raised])!, raised);
    }

    /// <summary>
    /// An exception from the until's wait as the step's play takes it: a connection or session failure (a disposed connection
    /// included) as why the step failed (<see cref="UntilWaitFailed"/>); any other refusal as the bridge's text, which the caller
    /// judges once the step's feed entries are in (<see cref="UntilFailure"/>).
    /// </summary>
    internal static (string? Error, string? Refusal) UntilCaught(int index, string name, Exception e) =>
        e is ObjectDisposedException or IOException or SessionException ? (UntilWaitFailed(index, name, e.Message), null) : (null, e.Message);

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
            session = await _registry.ReserveScratchAsync(
                _plan.ProjectDir,
                _scene.Name,
                _scene.ResPath,
                _plan.SessionPrefix,
                held: _observer is not null
            );
            _session = session;
            _observer?.SessionHeld(_index, session);
            await session.LaunchAsync(LaunchRequestFor(session), cancellationToken);
        }
        catch (SessionException e)
        {
            string name = _session?.Name ?? SessionRegistry.ScratchName(_plan.SessionPrefix, _scene.Name);
            return Seen(name) with { Refusal = $"{_scene.ResPath} did not start: {e.Message}" };
        }

        ScratchObservation seen = await WalkAsync(session, cancellationToken);
        var stopped = (StopResult)await session.StopAsync(CancellationToken.None);
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
        new(
            _plan.ProjectDir,
            _scene.ResPath,
            EngineArgs(_scene.TimeScale),
            _scene.UserArgs,
            session.Quiet,
            session.ShutOutRealGamepads,
            Prepare: false
        )
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

        if (_names.Count > 0 && ScratchVerdict.FeedError(_bootErrors) is null)
        {
            _deadline = _registry.Clock.Start(_walk.Ceiling, cancellationToken);
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

    /// <summary>
    /// Null when the scene's root has every protocol method, noting whether it also has GetStepUntil; else why not, naming the
    /// root and what it lacks.
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
        return untilError;
    }

    /// <summary>Whether the scene's root has <paramref name="method"/>, or why has_method failed.</summary>
    private async Task<(bool Found, string? Error)> HasMethodAsync(GodotSession session, string method, CancellationToken cancellationToken)
    {
        (JsonNode? has, string? error) = await CallAsync(session, "has_method", [method], cancellationToken);
        return (has is JsonValue value && value.TryGetValue(out bool found) && found, error);
    }

    /// <summary>
    /// Reads the step count and every step's name, then closes the launch window: its lines are never judged, and the error
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

        (_launchLines, _, error) = await CloseWindowAsync(session, "launch", cancellationToken);
        _bootErrors = TakeErrors(session.Errors, ref _feedMark);
        return error;
    }

    /// <summary>
    /// Reads every step's name, then each step's pace and the steps to play; null, or why a name, a step pace or a step to play
    /// could not be had.
    /// </summary>
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

        (ScratchPace[] paces, string? paceError) = StepPaces(_scene, _names);
        if (paceError is not null)
        {
            return paceError;
        }

        if (_plan.Steps is null)
        {
            _walk = new ScratchWalk(paces, [.. Enumerable.Range(0, _names.Count)], _scene.TimeScale);
            return null;
        }

        (int[] played, string? stepsError) = ResolveSteps(_plan.Steps, _scene.Name, _names);
        _walk = new ScratchWalk(paces, played, _scene.TimeScale);
        return stepsError;
    }

    private static string StepName(JsonNode? name) =>
        name is JsonValue value && value.TryGetValue(out string? text) ? text : name?.ToJsonString() ?? string.Empty;

    /// <summary>
    /// Plays each step to play, in ascending order, each waiting its own pace, until one fails; after the last one played,
    /// waits its pace once more, the longer of its game time and its wall time.
    /// </summary>
    private async Task PlayStepsAsync(GodotSession session, CancellationToken limit)
    {
        foreach (int index in _walk.Played)
        {
            _inFlight = index;
            _observer?.StepStarted(_index, index, _names[index], _names.Count, _walk.RemainingSecondsAtMost(index));
            ScratchStep step = await PlayStepAsync(session, index, limit);
            _steps.Add(step);
            if (StopsAfter(step, _plan.Patterns, _plan.KeepGoing))
            {
                return;
            }
        }

        _inFlight = -1;
        await PaceAfterAsync(session, _walk.AfterGameMs, limit);
    }

    /// <summary>
    /// Plays one step: paced (PlayStep, then a wait of its pace in game time), or for a step whose GetStepUntil gives a condition,
    /// inside one wait_for on it; then GetStatus and the window. The bridge's refusal of the until's wait is judged once the
    /// step's feed entries are in (<see cref="UntilFailure"/>); a connection or session failure is the step's error at once.
    /// </summary>
    private async Task<ScratchStep> PlayStepAsync(GodotSession session, int index, CancellationToken limit)
    {
        int gameMs = GameMs(_walk.Paces[index].Seconds);
        (JsonObject? wait, string? untilError) = _hasUntil ? await ReadUntilAsync(session, index, gameMs, limit) : (null, null);
        StepPlay played =
            untilError is not null ? new StepPlay(untilError, null, null)
            : wait is null ? new StepPlay(await PlayPacedAsync(session, index, gameMs, limit), null, null)
            : await PlayInsideUntilAsync(session, index, wait, gameMs, limit);
        (JsonNode? status, string? statusError) = await CallAsync(session, "GetStatus", [], limit);
        (List<string> lines, long dropped, string? markerError) = await CloseWindowAsync(session, $"step {index}", limit);
        IReadOnlyList<ErrorEntry> errors = TakeErrors(session.Errors, ref _feedMark);
        (string? error, ErrorEntry? raised) = played.BridgeError is { } bridgeError
            ? UntilFailure(index, _names[index], bridgeError, errors)
            : (played.Error, null);
        return new ScratchStep(index, _names[index], gameMs)
        {
            Until = played.Until,
            CallError = error ?? statusError ?? markerError,
            CallErrorEntry = raised,
            Status = StatusLine(status),
            Errors = errors,
            Lines = lines,
            Dropped = dropped,
        };
    }

    /// <summary>PlayStep, then a wait of the step's pace in game time: null, or why the call or the wait failed.</summary>
    private async Task<string?> PlayPacedAsync(GodotSession session, int index, int gameMs, CancellationToken limit)
    {
        (_, string? error) = await CallAsync(session, "PlayStep", [index], limit);
        return error ?? await WaitAsync(session, gameMs, limit);
    }

    /// <summary>
    /// GetStepUntil(index) as the until's wait parameters (none for a paced step), with the C# helper a state condition reads a
    /// C# node's state with, as wait_for adds it; or why the step fails.
    /// </summary>
    private async Task<(JsonObject? Wait, string? Error)> ReadUntilAsync(GodotSession session, int index, int gameMs, CancellationToken limit)
    {
        (JsonNode? answer, string? error) = await CallAsync(session, UntilMethod, [index], limit);
        if (error is not null)
        {
            return (null, error);
        }

        (JsonObject? wait, string? untilError) = UntilWait(answer, index, _names[index], gameMs, _rootPath);
        RuntimeTools.AddConditionStateHelper(wait, _csharp, session.ProjectDir);
        return (wait, untilError);
    }

    /// <summary>
    /// One wait_for on the step's until, PlayStep its call, which the bridge ends at the step's pace in game time, released at
    /// that pace at the scene's time scale plus the step allowance as a pace wait is: its figures and, when not met, why the
    /// step fails (<see cref="UntilMissed"/>); the bridge's refusal, judged by the caller; or why the wait failed on the connection.
    /// </summary>
    private async Task<StepPlay> PlayInsideUntilAsync(GodotSession session, int index, JsonObject wait, int gameMs, CancellationToken limit)
    {
        TimeSpan release = Release(gameMs, _scene.TimeScale);
        try
        {
            ScratchUntil until = UntilFigures(await session.SendAsync("wait_for", wait, release + ReplyAllowance, limit, release));
            string? notMet = until.Met ? null : UntilMissed(index, _names[index], until, _walk.Paces[index].Seconds, release.TotalSeconds);
            return new StepPlay(notMet, null, until);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or SessionException)
        {
            (string? error, string? refusal) = UntilCaught(index, _names[index], e);
            return new StepPlay(error, refusal, null);
        }
    }

    /// <summary>How a step's play went: why it failed, or the bridge's refusal of its until's wait, and the until's figures.</summary>
    private readonly record struct StepPlay(string? Error, string? BridgeError, ScratchUntil? Until);

    /// <summary>
    /// The pace after the last step, the longer of its game time and its wall time: an error in the feed during it, or a wait
    /// not met, fails the scene's exit.
    /// </summary>
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

    /// <summary>
    /// The markers' line numbers once both streams hold them, or null when <see cref="StepAllowance"/> of load-adjusted time
    /// passes first, on the registry's clock as every other wait of the walk: the server's own output readers starve with
    /// the server on a loaded machine, so a wall-time wait would end before they have read lines the game already printed.
    /// The output is read once more after the deadline is seen to expire, so lines read meanwhile still count.
    /// </summary>
    private async Task<LineMark?> WaitForMarkersAsync(GodotSession session, string marker, CancellationToken limit)
    {
        using LoadDeadline allowance = _registry.Clock.Start(StepAllowance, limit);
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

    /// <summary>
    /// wait_for {gameMs}: null once the game's clock has run that long; else why it did not. Released at that game time at the
    /// scene's time scale plus the step allowance.
    /// </summary>
    private async Task<string?> WaitAsync(GodotSession session, int gameMs, CancellationToken limit)
    {
        TimeSpan release = Release(gameMs, _scene.TimeScale);
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

        return _walk.CeilingReason(_deadline?.Reason == DeadlineReason.Backstop ? _deadline.BackstopClause() : string.Empty);
    }

    private ScratchObservation Seen(string session) =>
        new(_scene.Name, session, _scene.Pace, _names.Count)
        {
            LaunchLines = _launchLines,
            BootErrors = _bootErrors,
            Steps = _steps,
            StepPaceReasons = [.. _walk.Paces.Select(pace => pace.Reason)],
            AfterError = _afterError,
            Selected = _plan.Steps is null || _walk.Played.Count == 0 ? null : _walk.Played,
            TimeScale = _scene.TimeScale == _plan.TimeScale ? null : _scene.TimeScale,
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
