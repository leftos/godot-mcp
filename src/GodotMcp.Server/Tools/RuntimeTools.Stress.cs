using System.ComponentModel;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// stress_input: a seeded run of random inputs against the running game, looking for the input a game cannot survive. The
/// tool is a type of its own, apart from <see cref="RuntimeTools"/>, because batch_drive plays every RuntimeTools tool on
/// a session and a thousand inputs would outrun the batch's own deadline.
/// </summary>
[McpServerToolType]
internal sealed class StressTools(SessionRegistry sessions)
{
    internal const string ToolName = "stress_input";
    internal const int MaxPoolEntries = 200;
    internal const int MaxCount = 1000;
    internal const int MaxGapMs = 5000;
    internal const string EmptyPoolMessage = "pool is empty: give at least one of actions, keys, elements";

    // A generous allowance per event on top of the input tools' timeout: each takes a frame or two.
    private static readonly TimeSpan PerEventAllowance = TimeSpan.FromMilliseconds(100);

    [McpServerTool(Name = ToolName, ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Fires count random inputs, drawn uniformly and seeded from the pool the caller gives (InputMap actions, key names, "
            + "UI elements), one at a time, and reports whether the game survived and which new errors appeared at which "
            + "iteration. The same seed and pool replay the same sequence."
    )]
    public async Task<string> StressInputAsync(
        [Description("The inputs to draw from: {actions?, keys?, elements?}, at least one entry and at most 200 in all; a blank entry is refused.")]
            StressPool pool,
        [Description("How many inputs to fire, 1 to 1000; 100 by default.")] int count = 100,
        [Description("The draw's seed, 0 or more; one drawn from the random generator and returned when left out.")] int? seed = null,
        [Description(
            "options {gapMs?}: the time waited after each input, 0 to 5000 ms, of game time the time scale does not "
                + "stretch (real time in a plain run, clip time in a recording); 0 by default."
        )]
            StressOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        StressPlan plan = Plan(pool, count, seed, options);
        return (await PlayAsync(Find(session), plan, cancellationToken)).ToJsonString();
    }

    /// <summary>The checked arguments: the flattened pool, the count, the seed and the gap.</summary>
    /// <exception cref="McpException">The pool, the count, the seed or the gap is refused.</exception>
    private static StressPlan Plan(StressPool? pool, int count, int? seed, StressOptions? options) =>
        new(CheckPool(pool), CheckCount(count), CheckSeed(seed), CheckGap(options));

    /// <summary>
    /// The pool flattened in drawing order: actions, then keys, then elements, each in the caller's own order.
    /// </summary>
    /// <exception cref="McpException">The pool is empty, an entry is blank, or it holds more than 200 entries.</exception>
    internal static List<PoolEntry> CheckPool(StressPool? pool)
    {
        List<PoolEntry> entries = [];
        AddEntries(entries, pool?.Actions, "actions", PoolKind.Action);
        AddEntries(entries, pool?.Keys, "keys", PoolKind.Key);
        AddEntries(entries, pool?.Elements, "elements", PoolKind.Element);
        if (entries.Count == 0)
        {
            throw new McpException(EmptyPoolMessage);
        }

        return entries.Count <= MaxPoolEntries ? entries : throw new McpException($"pool has {entries.Count} entries; at most {MaxPoolEntries}");
    }

    /// <exception cref="McpException">The count is outside 1 to 1000.</exception>
    internal static int CheckCount(int count) =>
        count is >= 1 and <= MaxCount ? count : throw new McpException($"count must be 1-{MaxCount}; got {count}");

    /// <exception cref="McpException">The gap is outside 0 to 5000 milliseconds.</exception>
    internal static int CheckGap(StressOptions? options)
    {
        int gapMs = options?.GapMs ?? 0;
        return gapMs is >= 0 and <= MaxGapMs ? gapMs : throw new McpException($"options.gapMs must be 0-{MaxGapMs}; got {gapMs}");
    }

    /// <summary>The seed to draw with: the caller's, or one drawn from <see cref="Random.Shared"/> when left out.</summary>
    /// <exception cref="McpException">The seed is negative.</exception>
    internal static int CheckSeed(int? seed)
    {
        if (seed is not int value)
        {
            return Random.Shared.Next();
        }

        return value >= 0 ? value : throw new McpException($"seed must be 0 or more; got {value}.");
    }

    private static void AddEntries(List<PoolEntry> entries, string[]? names, string list, PoolKind kind)
    {
        for (int index = 0; index < (names?.Length ?? 0); index++)
        {
            string name = names![index];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new McpException($"pool.{list}[{index}] is blank");
            }

            entries.Add(new PoolEntry(name, kind));
        }
    }

    /// <summary>The session a tool addresses, resolved once its own arguments have been checked.</summary>
    private GodotSession Find(string? session)
    {
        try
        {
            return sessions.Resolve(session);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
    }

    /// <summary>Plays the whole run and shapes its result; a run the game does not survive ends where it stopped.</summary>
    private static async Task<JsonObject> PlayAsync(GodotSession target, StressPlan plan, CancellationToken cancellationToken)
    {
        StressRun run = new(target, plan, cancellationToken);
        for (int iteration = 1; iteration <= plan.Count; iteration++)
        {
            if (!await PlayOneAsync(run, iteration))
            {
                run.Iterations = iteration - 1;
                run.StoppedAt = iteration;
                return BuildResult(run);
            }
        }

        run.Iterations = plan.Count;
        run.Survived = run.Target.HasGame && (await HangProbe.RunAsync(run.Target, run.Cancellation)).Answered;
        return BuildResult(run);
    }

    /// <summary>
    /// Draws one input, fires it and files what it caused: the errors it raised, and a skip when the bridge refused the
    /// gesture. False when the run must stop there: the game is gone, or the call failed for anything but a refusal.
    /// </summary>
    private static async Task<bool> PlayOneAsync(StressRun run, int iteration)
    {
        if (!run.Target.HasGame)
        {
            return false;
        }

        PoolEntry entry = run.Plan.Entries[run.Draw.NextIndex(run.Plan.Entries.Count)];
        run.Drawn[(int)entry.Kind]++;
        long mark = run.Target.Errors.Mark();
        bool continues = true;
        try
        {
            await SendAsync(run, entry);
        }
        catch (McpException refused) when (refused.InnerException is InvalidOperationException)
        {
            run.Refused.Add(new RefusedDraw(entry, RefusalText(refused), iteration));
        }
        catch (McpException)
        {
            continues = false;
        }

        // Filed on every path: what the game raised in the iteration that killed it is what a stress run exists to show.
        run.Errors.AddRange(run.Target.Errors.ErrorsSince(mark).Select(error => new LocatedError(error, iteration)));
        return continues;
    }

    /// <summary>The reply timeout of one stress call: the input tools' allowance for its events and gap, in clip frames in a recording.</summary>
    internal static TimeSpan ReplyTimeout(int eventCount, int gapMs, bool recording) =>
        RuntimeTools.InputAllowance((PerEventAllowance * eventCount) + TimeSpan.FromMilliseconds(gapMs), recording);

    /// <summary>Plays one drawn input through the input bridge command, under the session's input gate, as the input tools do.</summary>
    private static async Task SendAsync(StressRun run, PoolEntry entry)
    {
        JsonArray events = EventsFor(entry, run.Plan.GapMs);
        JsonObject parameters = new() { ["gesture"] = "events", ["events"] = events };
        TimeSpan allowance = ReplyTimeout(events.Count, run.Plan.GapMs, run.Target.ActiveRecording is not null);
        await run.Target.InputGate.WaitAsync(run.Cancellation);
        try
        {
            RuntimeTools.BridgeCall call = new(ToolName, "input", parameters, allowance);
            await RuntimeTools.CallWithErrorsAsync(run.Target, call, run.Cancellation);
        }
        finally
        {
            run.Target.InputGate.Release();
        }
    }

    /// <summary>
    /// The one gesture an entry plays: the tap simulate_action builds for an action, a key press and release, or a click
    /// on the element; then the gap wait the caller asked for, of game time the time scale does not stretch (clip time
    /// in a recording), which the bridge runs on its own clock.
    /// </summary>
    private static JsonArray EventsFor(PoolEntry entry, int gapMs)
    {
        JsonArray events = entry.Kind switch
        {
            PoolKind.Action => RuntimeTools.BuildActionEvents(entry.Name, null),
            PoolKind.Key => [new JsonObject { ["type"] = "key", ["key"] = entry.Name }],
            _ => [new JsonObject { ["type"] = "click_element", ["element"] = entry.Name }],
        };
        if (gapMs > 0)
        {
            events.Add(new JsonObject { ["type"] = "wait", ["ms"] = gapMs });
        }

        return events;
    }

    // The connection wraps a refusal as "The bridge refused '<command>': <what the bridge said>"; the reason a skip
    // reports is what the bridge said (godot_mcp_input.gd's _play_gesture).
    private static string RefusalText(McpException refused)
    {
        string message = refused.InnerException?.Message ?? refused.Message;
        const string Marker = "': ";
        int at = message.IndexOf(Marker, StringComparison.Ordinal);
        return at < 0 ? message : message[(at + Marker.Length)..];
    }

    private static JsonObject BuildResult(StressRun run)
    {
        JsonObject result = new()
        {
            ["survived"] = run.Survived,
            ["seed"] = run.Plan.Seed,
            ["iterations"] = run.Iterations,
        };
        if (run.StoppedAt is int stoppedAt)
        {
            result["stoppedAt"] = stoppedAt;
        }

        result["drawn"] = new JsonObject
        {
            ["actions"] = run.Drawn[(int)PoolKind.Action],
            ["keys"] = run.Drawn[(int)PoolKind.Key],
            ["elements"] = run.Drawn[(int)PoolKind.Element],
        };
        result["errors"] = CollapseErrors(run.Errors);
        result["skipped"] = CollapseSkips(run.Refused);
        return result;
    }

    /// <summary>The errors keyed as ErrorReport keys them (message, file, line) into one entry each, in first-appearance order.</summary>
    private static JsonArray CollapseErrors(IReadOnlyList<LocatedError> errors)
    {
        Dictionary<(string Message, string File, int Line), int> positions = [];
        List<CollapsedError> collapsed = [];
        foreach (LocatedError error in errors)
        {
            (string, string, int) key = (error.Entry.Message, error.Entry.File, error.Entry.Line);
            if (positions.TryGetValue(key, out int position))
            {
                collapsed[position] = collapsed[position] with { Count = collapsed[position].Count + 1 };
                continue;
            }

            positions[key] = collapsed.Count;
            collapsed.Add(new CollapsedError(error.Entry, error.Iteration, 1));
        }

        return [.. collapsed.Select(Describe)];
    }

    private static JsonObject Describe(CollapsedError error) =>
        new()
        {
            ["message"] = error.Entry.Message,
            ["file"] = error.Entry.File,
            ["line"] = error.Entry.Line,
            ["iteration"] = error.FirstIteration,
            ["count"] = error.Count,
        };

    /// <summary>The refusals keyed by the pool entry they belong to, in first-appearance order.</summary>
    private static JsonArray CollapseSkips(IReadOnlyList<RefusedDraw> refused)
    {
        Dictionary<(PoolKind Kind, string Entry), int> positions = [];
        List<CollapsedSkip> collapsed = [];
        foreach (RefusedDraw draw in refused)
        {
            (PoolKind, string) key = (draw.Entry.Kind, draw.Entry.Name);
            if (positions.TryGetValue(key, out int position))
            {
                collapsed[position] = collapsed[position] with { Count = collapsed[position].Count + 1 };
                continue;
            }

            positions[key] = collapsed.Count;
            collapsed.Add(new CollapsedSkip(draw, 1));
        }

        return [.. collapsed.Select(DescribeSkip)];
    }

    private static JsonObject DescribeSkip(CollapsedSkip skip) =>
        new()
        {
            ["entry"] = skip.First.Entry.Name,
            ["kind"] = KindName(skip.First.Entry.Kind),
            ["reason"] = skip.First.Reason,
            ["firstIteration"] = skip.First.Iteration,
            ["count"] = skip.Count,
        };

    private static string KindName(PoolKind kind) =>
        kind switch
        {
            PoolKind.Action => "actions",
            PoolKind.Key => "keys",
            _ => "elements",
        };

    /// <summary>One run's state: what it draws from, what it has seen so far, and how it ended.</summary>
    private sealed class StressRun(GodotSession target, StressPlan plan, CancellationToken cancellation)
    {
        public GodotSession Target { get; } = target;
        public StressPlan Plan { get; } = plan;
        public CancellationToken Cancellation { get; } = cancellation;
        public SeededDraw Draw { get; } = new((ulong)plan.Seed);
        public List<LocatedError> Errors { get; } = [];
        public List<RefusedDraw> Refused { get; } = [];

        // One tally per PoolKind, in its declaration order: actions, keys, elements.
        public int[] Drawn { get; } = new int[3];
        public bool Survived { get; set; }
        public int Iterations { get; set; }
        public int? StoppedAt { get; set; }
    }

    /// <summary>The pool's kinds, in the order the pool flattens.</summary>
    internal enum PoolKind
    {
        Action,
        Key,
        Element,
    }

    /// <summary>One input to draw: its name as the caller gave it, and the list it came from.</summary>
    internal sealed record PoolEntry(string Name, PoolKind Kind);

    /// <summary>A checked stress run: the flattened pool, how many inputs to fire, the seed and the gap.</summary>
    internal sealed record StressPlan(List<PoolEntry> Entries, int Count, int Seed, int GapMs);

    /// <summary>An error the game raised, with the iteration it appeared after.</summary>
    private sealed record LocatedError(ErrorEntry Entry, int Iteration);

    /// <summary>A gesture the bridge refused, with the iteration it happened at and what the bridge said.</summary>
    private sealed record RefusedDraw(PoolEntry Entry, string Reason, int Iteration);

    /// <summary>One collapsed error: the first appearance, and how many times it appeared.</summary>
    private sealed record CollapsedError(ErrorEntry Entry, int FirstIteration, int Count);

    /// <summary>One collapsed skip: the entry's first refusal, and how many times it was refused.</summary>
    private sealed record CollapsedSkip(RefusedDraw First, int Count);
}

/// <summary>What stress_input draws its inputs from: InputMap actions, key names and UI elements, at least one entry in all.</summary>
internal sealed record StressPool(
    [property: Description("InputMap action names, as simulate_action takes them: ui_accept, probe_jump.")] string[]? Actions = null,
    [property: Description("Key names, as key takes them: Enter, Space, A, F1.")] string[]? Keys = null,
    [property: Description("UI elements, as click takes them: a node path, a path under the root, or a node name.")] string[]? Elements = null
);

/// <summary>How stress_input spaces its inputs out, in game time the time scale does not stretch.</summary>
internal sealed record StressOptions(
    [property: Description(
        "The time waited after each input, in milliseconds, 0 to 5000, of game time the time scale does not stretch "
            + "(real time in a plain run, clip time in a recording); 0 by default."
    )]
        int GapMs = 0
);
