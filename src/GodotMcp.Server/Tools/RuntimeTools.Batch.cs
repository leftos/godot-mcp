using System.Collections.Frozen;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// batch_drive: a list of runtime tool calls and assertions played against a running game in one call. Tool steps run
/// through the server's own tool collection, so each takes its real arguments; assertions run wait_for, the session's
/// error feed or compare_screenshot; a parallel group runs several such steps on their own sessions at once. The batch stops
/// at the first step that fails.
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const int MaxBatchSteps = 100;
    internal const string BatchToolName = "batch_drive";
    internal static readonly TimeSpan DefaultBatchDeadline = TimeSpan.FromSeconds(300);
    private const int DefaultTolerance = 2;
    private const int MaxFiredFailureEntries = 10;
    private const string AssertionKindList = "property, expression, state, wait, no_errors, screenshot, fired";

    /// <summary>
    /// The tools a batch step may name: every tool declared on this class but batch_drive itself and save_screenshot, which
    /// writes a file into the project.
    /// </summary>
    internal static readonly IReadOnlySet<string> BatchableTools = FindBatchableTools();

    private static readonly FrozenDictionary<string, AssertionKind> AssertionKinds = new Dictionary<string, AssertionKind>
    {
        ["property"] = new("node, property and equals", HasPropertyFields, CheckWaitAssertion, (tools, call) => tools.AssertWaitAsync(call)),
        ["expression"] = new(
            "expression",
            step => !string.IsNullOrEmpty(step.Expression),
            CheckWaitAssertion,
            (tools, call) => tools.AssertWaitAsync(call)
        ),
        ["state"] = new("node, state and equals", HasStateFields, CheckWaitAssertion, (tools, call) => tools.AssertWaitAsync(call)),
        ["wait"] = new("a wait_for condition", _ => true, CheckWaitAssertion, (tools, call) => tools.AssertWaitAsync(call)),
        ["no_errors"] = new("nothing", _ => true, CheckNoErrorsSessions, (tools, call) => Task.FromResult(tools.AssertNoErrors(call))),
        ["screenshot"] = new(
            "name",
            step => !string.IsNullOrEmpty(step.Name),
            CheckScreenshotAssertion,
            (tools, call) => tools.AssertScreenshotAsync(call)
        ),
        ["fired"] = new(
            "node and signal",
            HasFiredFields,
            CheckFiredAssertion,
            (_, call) => Task.FromResult(AssertFired(call)),
            NeedsToolBefore: true
        ),
    }.ToFrozenDictionary();

    private const int MinGroupSteps = 2;
    private const int MaxGroupSteps = 8;

    /// <summary>How a step shows that each of its fields but parallel was given, named as in the JSON, in the record's order.</summary>
    private static readonly (string Name, Func<BatchStep, bool> Given)[] StepFields =
    [
        ("tool", step => step.Tool is not null),
        ("args", step => step.Args is not null),
        ("assert", step => step.Assert is not null),
        ("node", step => step.Node is not null),
        ("exists", step => step.Exists is not null),
        ("property", step => !string.IsNullOrEmpty(step.Property)),
        ("equals", step => step.EqualsValue is not null),
        ("signal", step => step.Signal is not null),
        ("expression", step => !string.IsNullOrEmpty(step.Expression)),
        ("uiChanged", step => step.UiChanged is not null),
        ("gameMs", step => step.GameMs is not null),
        ("frames", step => step.Frames is not null),
        ("timeoutMs", step => step.TimeoutMs is not null),
        ("name", step => !string.IsNullOrEmpty(step.Name)),
        ("tolerance", step => step.Tolerance is not null),
        ("maxChangedRatio", step => step.MaxChangedRatio is not null),
        ("session", step => step.Session is not null),
        ("text", step => step.Text is not null),
        ("under", step => step.Under is not null),
        ("stable", step => step.Stable is not null),
        ("state", step => step.State is not null),
        ("output", step => step.Output is not null),
        ("stream", step => step.Stream is not null),
        ("after", step => step.After is not null),
        ("sessions", step => step.Sessions is not null),
    ];

    /// <summary>The fields a fired assertion takes, or that a step of any kind may carry.</summary>
    private static readonly string[] FiredStepFields = ["tool", "assert", "node", "equals", "signal", "session"];

    /// <summary>How a step shows that each field a fired assertion does not take was given.</summary>
    private static readonly (string Name, Func<BatchStep, bool> Given)[] FiredForeignStepFields =
    [
        .. StepFields.Where(field => !FiredStepFields.Contains(field.Name)),
    ];

    [McpServerTool(Name = BatchToolName, ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Plays a list of steps against a running game in one call, in order, and stops at the first failed assertion or "
            + "failed tool step. A step is either {tool, args}: a runtime tool by its name with the arguments it takes on its "
            + "own (take_screenshot and compare_screenshot are forced to path_only; image blocks are dropped), or an assertion "
            + "{assert, ...}: property {node, property, equals, timeoutMs?}, expression {expression, timeoutMs?} and state {node, "
            + "state, equals, timeoutMs?}, the value at a key path of the node's state as wait_for's state condition reads it (for "
            + "all three, timeoutMs defaults to 0: checked once, now, even while paused), wait {any wait_for condition, gameMs "
            + "and frames included, "
            + "timeoutMs?} (default 10000; gameMs + 10000 for gameMs, 10 s + 100 ms a frame for frames, 10 s + 100 ms for each "
            + "of the stable + 1 frames a settled wait reads), no_errors {sessions?} (no "
            + "errors since the previous no_errors, or since the batch started, in its session, or in each of sessions: \"all\" "
            + "for every session another step of the batch addresses, except process sessions, or a list of names, in place of "
            + "session; with sessions, a pass is {errors: 0, sessions: [names checked]}), screenshot {name, "
            + "tolerance?, maxChangedRatio?} (compare_screenshot must match), fired {node, signal, equals?} (the nearest "
            + "earlier tool step's fired list has that node's signal, with those arguments when equals is given). Any tool or "
            + "assertion step may carry session. At most 100 steps; "
            + "the whole batch stops after 300 s of load-adjusted time. Returns {passed, steps: [{index, tool or assert, ok, result or error}], "
            + "failedAt?: {index, reason}}. A step may be {parallel: [steps]} (see the step's parallel field); a group's entry is "
            + "{index, parallel: true, ok, startMs, endMs, members: [entries]} and failedAt.member names the member that failed. The "
            + "result's startedAt and every entry's startMs and endMs are drive-clock milliseconds since the server started; a "
            + "wait step's result also carries metAt and clockErrorMs, the game's stamp converted onto that clock as wait_for "
            + "reports them. A "
            + "top-level entry also carries errorsElsewhere: {session: [errors]}, the errors the batch's other sessions raised "
            + "since the entry before it ended (for a group, every session's but those raised during the call of the member that "
            + "addresses it, a cancelled member's session counting as not addressed; members carry none), at most 20 "
            + "a session, then errorsElsewhereOmitted: {session: n}; both are left out when there are none."
    )]
    public async Task<string> BatchDriveAsync(
        [Description("The steps, in order: {tool, args, session?} or {assert, …, session?} or {parallel: [steps]}; 1 to 100 of them.")]
            IReadOnlyList<BatchStep> steps,
        McpServer server,
        [Description(ProjectTools.SessionDescription + " Every step addresses it unless the step names its own.")] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        CheckBatch(steps);
        CheckGroupSessions(steps, session);
        double startedAt = DriveClock.NowMs;
        if ((FindProcessToolStep(steps, session) ?? FindSharedSession(steps, session)) is { } refused)
        {
            // Refused before any step runs, so a game step ahead of it in the batch never acts.
            return new JsonObject
            {
                ["passed"] = false,
                ["startedAt"] = startedAt,
                ["steps"] = new JsonArray(),
                ["failedAt"] = refused.ToFailedAt(),
            }.ToJsonString();
        }

        using LoadDeadline deadline = sessions.Clock.Start(BatchDeadline, cancellationToken);
        Dictionary<GodotSession, long> marks = MarkErrorFeeds(steps, session);
        ErrorCursors elsewhere = new([.. marks.Select(mark => (mark.Key.Name, Feed: mark.Key.Errors, Mark: mark.Value))]);
        BatchRun run = new(server, session, marks, elsewhere, deadline);
        JsonArray entries = [];
        JsonObject? failedAt = null;
        for (int index = 0; index < steps.Count && failedAt is null; index++)
        {
            (JsonObject entry, failedAt) = steps[index].Parallel is { } members
                ? await RunGroupAsync(run, index, members, cancellationToken)
                : await RunTimedStepAsync(run, index, steps[index], cancellationToken);
            entries.Add(entry);
        }

        JsonObject result = new()
        {
            ["passed"] = failedAt is null,
            ["startedAt"] = startedAt,
            ["steps"] = entries,
        };
        if (failedAt is not null)
        {
            result["failedAt"] = failedAt;
        }

        return result.ToJsonString();
    }

    /// <summary>Runs one step that is not a group, and gives its entry, stamped on the drive clock, and its failedAt when it failed.</summary>
    private async Task<(JsonObject Entry, JsonObject? FailedAt)> RunTimedStepAsync(
        BatchRun run,
        int index,
        BatchStep step,
        CancellationToken clientCancellation
    )
    {
        double startMs = DriveClock.NowMs;
        StepOutcome outcome = await RunStepOrDeadlineAsync(run, index, step, clientCancellation);
        double endMs = DriveClock.NowMs;
        RecordToolStep(run, index, step, outcome);
        JsonObject? failedAt = outcome.Ok ? null : new JsonObject { ["index"] = index, ["reason"] = outcome.Error };
        JsonObject entry = outcome.ToEntry(index, step, startMs, endMs);
        run.Elsewhere.AddTo(entry, AddressedSessions(step, run));
        return (entry, failedAt);
    }

    /// <summary>
    /// Runs a parallel group: each member starts once the one before has sent its request, has read its output baseline or
    /// has ended, and the group ends when every member has. The first member to fail cancels the others still running.
    /// </summary>
    private async Task<(JsonObject Entry, JsonObject? FailedAt)> RunGroupAsync(
        BatchRun run,
        int index,
        IReadOnlyList<BatchStep> members,
        CancellationToken clientCancellation
    )
    {
        double startMs = DriveClock.NowMs;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(run.Cancellation);
        GroupRun group = new(run with { Cancellation = cancel.Token }, index, cancel, clientCancellation);
        var running = new Task<JsonObject>[members.Count];
        for (int member = 0; member < members.Count; member++)
        {
            WriteWatch watch = new();
            running[member] = RunMemberAsync(group, member, members[member], watch);
            await watch.Released;
        }

        JsonObject[] entries = await Task.WhenAll(running);
        JsonObject entry = new()
        {
            ["index"] = index,
            ["parallel"] = true,
            ["ok"] = group.Failure is null,
            ["startMs"] = startMs,
            ["endMs"] = DriveClock.NowMs,
            ["members"] = new JsonArray(entries),
        };
        run.Elsewhere.AddTo(entry, group.Calls());
        JsonObject? failedAt = group.Failure is { } failure
            ? new JsonObject
            {
                ["index"] = index,
                ["member"] = failure.Member,
                ["reason"] = failure.Reason,
            }
            : null;
        return (entry, failedAt);
    }

    /// <summary>
    /// Runs one member of a group under <paramref name="watch"/>, which it releases when it ends at the latest, and gives its
    /// entry: its own outcome, or the cancelled entry when the group was cancelled before it finished.
    /// </summary>
    private async Task<JsonObject> RunMemberAsync(GroupRun group, int member, BatchStep step, WriteWatch watch)
    {
        watch.Watch();
        double startMs = DriveClock.NowMs;
        try
        {
            MemberCall call = new(ResolvedSession(MemberSession(step, group.Run.Session)));
            StepOutcome? outcome = await RunMemberStepAsync(group, step);
            call.End();
            bool cancelled = group.Judge(member, outcome, group.Cancel.IsCancellationRequested);
            double endMs = DriveClock.NowMs;
            if (cancelled || outcome is null)
            {
                return CancelledEntry(member, step, MemberSession(step, group.Run.Session), watch.Sent, startMs, endMs);
            }

            group.Called(call);
            return outcome.ToEntry(member, step, startMs, endMs);
        }
        catch (Exception)
        {
            // An exception no outcome caught (the client's cancel, or one of no token's) ends the group now, not at its slowest member.
            await group.Cancel.CancelAsync();
            throw;
        }
        finally
        {
            watch.Release();
        }
    }

    /// <summary>A member's outcome; null when the group's cancel, not the client or the deadline, ended it with an exception.</summary>
    private async Task<StepOutcome?> RunMemberStepAsync(GroupRun group, BatchStep step)
    {
        try
        {
            return await RunStepOrDeadlineAsync(group.Run, group.Index, step, group.ClientCancellation);
        }
        catch (OperationCanceledException) when (group.Cancel.IsCancellationRequested && !group.ClientCancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The entry of a member the group cancelled: whether its request had reached the game, and no result or error.</summary>
    private static JsonObject CancelledEntry(int member, BatchStep step, string? session, bool sent, double startMs, double endMs)
    {
        JsonObject entry = new() { ["index"] = member };
        entry[EntryKind(step)] = step.Tool ?? step.Assert;
        entry["session"] = session;
        entry["ok"] = false;
        entry["cancelled"] = true;
        entry["sent"] = sent;
        entry["startMs"] = startMs;
        entry["endMs"] = endMs;
        return entry;
    }

    /// <summary>The key a step's entry names it under: tool for a tool step, assert for an assertion.</summary>
    private static string EntryKind(BatchStep step) => step.Tool is not null ? "tool" : "assert";

    /// <summary>Keeps the step's result for a later fired assertion to check it, when the step runs a tool.</summary>
    private static void RecordToolStep(BatchRun run, int index, BatchStep step, StepOutcome outcome)
    {
        if (step.Tool is { } tool)
        {
            run.LastTool = (index, tool, outcome.Result as JsonObject);
        }
    }

    /// <summary>How long a whole batch may run; <see cref="DefaultBatchDeadline"/> but in tests.</summary>
    internal TimeSpan BatchDeadline { get; init; } = DefaultBatchDeadline;

    /// <summary>The reason a batch whose deadline passed gives, e.g. "the batch's 300 s deadline passed".</summary>
    internal string DeadlineReason => $"the batch's {BatchDeadline.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s deadline passed";

    /// <summary>Refuses a batch that cannot run as given, naming the step at fault, before any step runs.</summary>
    /// <exception cref="McpException">No steps, too many, or a step that is not a batchable tool or a complete assertion.</exception>
    internal static void CheckBatch(IReadOnlyList<BatchStep>? steps)
    {
        if (steps is null || steps.Count == 0)
        {
            throw new McpException("steps is empty; a batch needs at least one step.");
        }

        if (StepCount(steps) > MaxBatchSteps)
        {
            throw new McpException($"a batch takes at most {MaxBatchSteps} steps; split it.");
        }

        bool toolStepBefore = false;
        for (int index = 0; index < steps.Count; index++)
        {
            CheckStep($"step {index}", steps[index], toolStepBefore);
            toolStepBefore |= steps[index]?.Tool is not null;
        }
    }

    /// <summary>The steps a batch counts toward its limit: a group counts as its members.</summary>
    private static int StepCount(IReadOnlyList<BatchStep> steps) => steps.Sum(step => step?.Parallel?.Count ?? 1);

    /// <summary>
    /// Refuses a group two of whose members address one session by the names given: a tool member's args.session, else its
    /// step's session, else the batch's, as <see cref="MemberSession"/> reads them. A member that names none while the batch
    /// names none is left to the check on the resolved sessions as the batch starts.
    /// </summary>
    /// <exception cref="McpException">A group's members address one session twice.</exception>
    internal static void CheckGroupSessions(IReadOnlyList<BatchStep> steps, string? session)
    {
        for (int index = 0; index < steps.Count; index++)
        {
            if (steps[index].Parallel is { } members && SharedName(members, member => MemberSession(member, session)) is { } shared)
            {
                throw new McpException(SharedSessionRefusal(index, shared));
            }
        }
    }

    /// <summary>
    /// The first group two of whose members resolve to one live session, with the refusal; null when none does. A name that
    /// resolves to no session is passed over here: the member's own call refuses it as it runs.
    /// </summary>
    private StepRefusal? FindSharedSession(IReadOnlyList<BatchStep> steps, string? session)
    {
        for (int index = 0; index < steps.Count; index++)
        {
            if (steps[index].Parallel is { } members && SharedName(members, member => ResolvedName(MemberSession(member, session))) is { } shared)
            {
                return new StepRefusal(index, null, SharedSessionRefusal(index, shared));
            }
        }

        return null;
    }

    /// <summary>The name of the session <paramref name="session"/> resolves to; null when it resolves to none.</summary>
    private string? ResolvedName(string? session) => ResolvedSession(session)?.Name;

    /// <summary>The session <paramref name="session"/> resolves to, a process session included; null when it resolves to none.</summary>
    private GodotSession? ResolvedSession(string? session)
    {
        try
        {
            return sessions.Resolve(session, allowProcess: true);
        }
        catch (SessionException)
        {
            // The member's own call raises the same refusal when it runs, and its entry carries it.
            return null;
        }
    }

    /// <summary>The first two members, in listed order, whose names are one, with the name; null when every name differs or is null.</summary>
    private static (int First, int Second, string Name)? SharedName(IReadOnlyList<BatchStep> members, Func<BatchStep, string?> nameOf)
    {
        string?[] names = [.. members.Select(nameOf)];
        for (int second = 1; second < names.Length; second++)
        {
            for (int first = 0; first < second; first++)
            {
                if (names[first] is { } name && string.Equals(name, names[second], StringComparison.Ordinal))
                {
                    return (first, second, name);
                }
            }
        }

        return null;
    }

    private static string SharedSessionRefusal(int index, (int First, int Second, string Name) shared) =>
        $"step {index}: parallel steps {shared.First} and {shared.Second} both address session '{shared.Name}'; "
        + "each step of a group addresses its own session.";

    /// <summary>The session a step addresses: a tool step's <see cref="EffectiveSession"/>, an assertion's own, else the batch's.</summary>
    private static string? MemberSession(BatchStep step, string? session) =>
        step.Tool is not null ? EffectiveSession(step, session) : step.Session ?? session;

    /// <summary>
    /// A tool step's arguments as the tool receives them: its args, the session when they name none, and path_only forced
    /// for take_screenshot and compare_screenshot.
    /// </summary>
    internal static Dictionary<string, JsonElement> ToolArguments(string tool, JsonObject? args, string? session)
    {
        JsonObject arguments = args?.DeepClone().AsObject() ?? [];
        if (session is not null && arguments["session"] is null)
        {
            arguments["session"] = session;
        }

        ForcePathOnly(tool, arguments);
        return arguments.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value, Json));
    }

    /// <summary>take_screenshot's responseMode and compare_screenshot's options.responseMode set to path_only.</summary>
    private static void ForcePathOnly(string tool, JsonObject arguments)
    {
        if (tool == "take_screenshot")
        {
            arguments["responseMode"] = "path_only";
        }
        else if (tool == "compare_screenshot" && arguments["options"] is JsonObject options)
        {
            options["responseMode"] = "path_only";
        }
        else if (tool == "compare_screenshot" && arguments["options"] is null)
        {
            arguments["options"] = new JsonObject { ["responseMode"] = "path_only" };
        }
    }

    private static FrozenSet<string> FindBatchableTools() =>
        typeof(RuntimeTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .Where(name => name is not (BatchToolName or SaveScreenshotToolName))
            .ToFrozenSet();

    /// <summary>Checks one step; <paramref name="at"/> is how its refusals name it, "step 3" or "step 3: parallel step 1".</summary>
    private static void CheckStep(string at, BatchStep? step, bool toolStepBefore)
    {
        if (step is null || KindsGiven(step) != 1)
        {
            throw new McpException($"{at}: give exactly one of tool, assert and parallel.");
        }

        if (step.Parallel is { } members)
        {
            CheckGroup(at, step, members);
        }
        else if (step.Tool is not null)
        {
            CheckToolStep(at, step);
        }
        else
        {
            CheckAssertion(at, step, toolStepBefore);
        }
    }

    private static int KindsGiven(BatchStep step) =>
        (step.Tool is not null ? 1 : 0) + (step.Assert is not null ? 1 : 0) + (step.Parallel is not null ? 1 : 0);

    private static void CheckToolStep(string at, BatchStep step)
    {
        if (!BatchableTools.Contains(step.Tool!))
        {
            throw new McpException(
                $"{at}: '{step.Tool}' cannot run in a batch; a tool step names a runtime tool, one of: "
                    + $"{string.Join(", ", BatchableTools.Order(StringComparer.Ordinal))}."
            );
        }

        if (step.Sessions is not null)
        {
            throw new McpException($"{at}: a tool step does not take sessions; only no_errors does.");
        }
    }

    /// <summary>Checks a group: parallel alone, 2 to 8 members, and each a tool step or an assertion a group may hold.</summary>
    private static void CheckGroup(string at, BatchStep step, IReadOnlyList<BatchStep> members)
    {
        string[] beside = [.. StepFields.Where(field => field.Given(step)).Select(field => field.Name)];
        if (beside.Length > 0)
        {
            throw new McpException($"{at}: a parallel group takes parallel alone; got {string.Join(", ", beside)}.");
        }

        if (members.Count is < MinGroupSteps or > MaxGroupSteps)
        {
            throw new McpException($"{at}: a parallel group takes {MinGroupSteps} to {MaxGroupSteps} steps; got {members.Count}.");
        }

        for (int member = 0; member < members.Count; member++)
        {
            CheckMember(at, member, members[member]);
        }
    }

    private static void CheckMember(string at, int member, BatchStep? step)
    {
        if (step?.Parallel is not null)
        {
            throw new McpException($"{at}: parallel steps cannot be nested; a group holds tool and assertion steps only.");
        }

        if (step is { Tool: null, Assert: "fired" or "no_errors" })
        {
            throw new McpException($"{at}: parallel step {member}: {step.Assert} cannot run in a parallel group; put it after the group.");
        }

        CheckStep($"{at}: parallel step {member}", step, toolStepBefore: false);
    }

    private static void CheckAssertion(string at, BatchStep step, bool toolStepBefore)
    {
        if (!AssertionKinds.TryGetValue(step.Assert!, out AssertionKind? kind))
        {
            throw new McpException($"{at}: assert '{step.Assert}' is not one of {AssertionKindList}.");
        }

        if (kind.NeedsToolBefore && !toolStepBefore)
        {
            throw new McpException(NeedsToolStepRefusal(at, step.Assert!));
        }

        if (!kind.IsComplete(step))
        {
            throw new McpException($"{at}: the {step.Assert} assertion needs {kind.Needs}.");
        }

        CheckSessionsField(at, step);
        try
        {
            kind.Check(step);
        }
        catch (McpException e)
        {
            throw new McpException($"{at}: {e.Message}", e);
        }
    }

    /// <summary>Refuses sessions on any assertion but no_errors, the one kind that checks several sessions.</summary>
    private static void CheckSessionsField(string at, BatchStep step)
    {
        if (step.Sessions is not null && step.Assert != "no_errors")
        {
            throw new McpException($"{at}: the {step.Assert} assertion does not take sessions; only no_errors does.");
        }
    }

    /// <summary>Refuses a no_errors assertion's sessions that is neither "all" nor a non-empty list of distinct session names.</summary>
    private static void CheckNoErrorsSessions(BatchStep step)
    {
        if (step.Sessions is not { } given)
        {
            return;
        }

        if (step.Session is not null)
        {
            throw new McpException("no_errors takes session or sessions, not both; sessions replaces its own session.");
        }

        if (!IsAllSessions(step))
        {
            CheckSessionList(step, given);
        }
    }

    /// <summary>Refuses a sessions list that is not a non-empty list of distinct session names.</summary>
    private static void CheckSessionList(BatchStep step, JsonElement given)
    {
        if (given.ValueKind != JsonValueKind.Array || !given.EnumerateArray().All(IsSessionName))
        {
            throw new McpException($"sessions takes \"all\" or a list of session names; got {given.GetRawText()}.");
        }

        if (given.GetArrayLength() == 0)
        {
            throw new McpException("sessions is empty; name at least one session, or give \"all\".");
        }

        string? twice = ListedSessions(step)!.GroupBy(name => name, StringComparer.Ordinal).FirstOrDefault(names => names.Count() > 1)?.Key;
        if (twice is not null)
        {
            throw new McpException($"sessions names '{twice}' twice.");
        }
    }

    private static bool IsSessionName(JsonElement element) => element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 };

    /// <summary>Whether the step's sessions is "all".</summary>
    private static bool IsAllSessions(BatchStep step) => step.Sessions is { ValueKind: JsonValueKind.String } given && given.GetString() == "all";

    /// <summary>The session names the step's sessions lists; null when it gives none or "all".</summary>
    private static string?[]? ListedSessions(BatchStep step) =>
        step.Sessions is { ValueKind: JsonValueKind.Array } given ? [.. given.EnumerateArray().Select(name => name.GetString()!)] : null;

    private static bool HasPropertyFields(BatchStep step) =>
        !string.IsNullOrEmpty(step.Node) && !string.IsNullOrEmpty(step.Property) && step.EqualsValue is { ValueKind: not JsonValueKind.Null };

    private static bool HasStateFields(BatchStep step) =>
        !string.IsNullOrEmpty(step.Node) && step.State is not null && step.EqualsValue is { ValueKind: not JsonValueKind.Null };

    private static bool HasFiredFields(BatchStep step) => !string.IsNullOrEmpty(step.Node) && !string.IsNullOrEmpty(step.Signal);

    /// <summary>Refuses a fired assertion's equals that is not an array, and any field the kind does not take.</summary>
    private static void CheckFiredAssertion(BatchStep step)
    {
        if (step.EqualsValue is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Array) } equals)
        {
            throw new McpException($"equals must be an array of the emission's arguments; got {equals.GetRawText()}.");
        }

        string[] foreign = ForeignFieldsOf(step);
        if (foreign.Length > 0)
        {
            throw new McpException($"the fired assertion takes node, signal and equals; got {string.Join(", ", foreign)}.");
        }
    }

    /// <summary>The step's fields a fired assertion was given but does not take, in the step record's order.</summary>
    private static string[] ForeignFieldsOf(BatchStep step) =>
        [.. FiredForeignStepFields.Where(field => field.Given(step)).Select(field => field.Name)];

    /// <summary>The refusal of a fired assertion no tool step comes before; validation raises it before the batch runs.</summary>
    private static string NeedsToolStepRefusal(string at, string assert) =>
        $"{at}: assert '{assert}' needs a tool step before it, whose fired list it checks.";

    private static void CheckWaitAssertion(BatchStep step) => _ = BuildWaitParameters(ConditionOf(step), TimeoutOf(step));

    private static void CheckScreenshotAssertion(BatchStep step)
    {
        ScreenshotBaseline.CheckName(step.Name!);
        _ = CheckCompareArguments(null, step.Tolerance ?? DefaultTolerance, CompareOptionsOf(step));
    }

    /// <summary>The wait_for condition an assertion checks: only its own kind's fields for property, expression and state.</summary>
    internal static WaitCondition ConditionOf(BatchStep step) =>
        step.Assert switch
        {
            "property" => new WaitCondition(Node: step.Node, Property: step.Property, EqualsValue: step.EqualsValue),
            "expression" => new WaitCondition(Node: step.Node, Expression: step.Expression),
            "state" => new WaitCondition(Node: step.Node, EqualsValue: step.EqualsValue, State: step.State),
            _ => new WaitCondition(
                step.Node,
                step.Exists,
                step.Property,
                step.EqualsValue,
                step.Signal,
                step.Expression,
                step.UiChanged,
                step.GameMs,
                step.Frames,
                step.Text,
                step.Under,
                step.Stable,
                step.State,
                step.Output,
                step.Stream,
                step.After
            ),
        };

    /// <summary>The step's timeoutMs; left out, 0 for property, expression and state, and wait_for's own default for wait.</summary>
    internal static int? TimeoutOf(BatchStep step) => step.Assert == "wait" ? step.TimeoutMs : step.TimeoutMs ?? 0;

    private static CompareOptions CompareOptionsOf(BatchStep step) => new(step.MaxChangedRatio ?? 0, "path_only");

    /// <summary>
    /// The first tool step, or tool member of a group, whose effective session is a process, with its place and the refusal;
    /// null when every tool step addresses another kind, an unknown name or none. It runs before the first step, so no step
    /// of the batch acts first.
    /// </summary>
    private StepRefusal? FindProcessToolStep(IReadOnlyList<BatchStep> steps, string? session)
    {
        for (int index = 0; index < steps.Count; index++)
        {
            IReadOnlyList<BatchStep> members = steps[index].Parallel ?? [steps[index]];
            for (int member = 0; member < members.Count; member++)
            {
                if (ProcessRefusalOf(members[member], session) is { } refusal)
                {
                    return new StepRefusal(index, steps[index].Parallel is null ? null : member, refusal);
                }
            }
        }

        return null;
    }

    /// <summary>The refusal of a tool step whose effective session is a process; null for any other step.</summary>
    private string? ProcessRefusalOf(BatchStep step, string? session) =>
        step.Tool is not null && sessions.ProcessSession(EffectiveSession(step, session)) is { } process
            ? GodotSession.ProcessRefusal(process.Name, process.ProcessProgram ?? "a process")
            : null;

    /// <summary>
    /// The session a tool step's own arguments name: args.session when it carries one, else the step's, else the batch's,
    /// which is what <see cref="ToolArguments"/> hands the tool.
    /// </summary>
    internal static string? EffectiveSession(BatchStep step, string? session) =>
        step.Args?["session"] is { } named && named.GetValueKind() == JsonValueKind.String ? named.GetValue<string>() : step.Session ?? session;

    /// <summary>
    /// Marks the error feed of every session the batch names but process sessions, which have none: each step's and each group
    /// member's, as <see cref="MemberSession"/> reads it, and each a no_errors step checks, so the first check and the first
    /// entry count from here. A no_errors step's sessions must resolve; any other step's that resolves to none is passed over,
    /// as its own call refuses it when it runs.
    /// </summary>
    /// <exception cref="McpException">A session a no_errors step checks resolves to none, or to a process; the refusal names the step.</exception>
    private Dictionary<GodotSession, long> MarkErrorFeeds(IReadOnlyList<BatchStep> steps, string? session)
    {
        Dictionary<GodotSession, long> marks = [];
        for (int index = 0; index < steps.Count; index++)
        {
            GodotSession[] named =
                steps[index].Assert == "no_errors"
                    ? NoErrorsSessionsAtStart(steps[index], index, session)
                    :
                    [
                        .. (steps[index].Parallel ?? [steps[index]])
                            .Select(member => ResolvedSession(MemberSession(member, session)))
                            .OfType<GodotSession>(),
                    ];
            foreach (GodotSession target in named.Where(target => target.Kind != SessionKind.Process))
            {
                marks.TryAdd(target, target.Errors.Mark());
            }
        }

        return marks;
    }

    /// <summary>The sessions a no_errors step lists, else its own; none for "all", which reads the sessions the other steps name.</summary>
    /// <exception cref="McpException">One resolves to none, or to a process; the refusal names the step.</exception>
    private GodotSession[] NoErrorsSessionsAtStart(BatchStep step, int index, string? session)
    {
        if (IsAllSessions(step))
        {
            return [];
        }

        try
        {
            return [.. (ListedSessions(step) ?? [step.Session ?? session]).Select(name => Find(name))];
        }
        catch (McpException e)
        {
            throw new McpException($"step {index}: {e.Message}", e);
        }
    }

    /// <summary>
    /// The sessions a step that is not a group addresses, whose errors its entry's errorsElsewhere leaves out whole: a
    /// no_errors step's sessions, else the step's own; those that resolve to none are left out.
    /// </summary>
    private Dictionary<string, SeqRange> AddressedSessions(BatchStep step, BatchRun run)
    {
        IEnumerable<string?> named =
            step.Assert != "no_errors" ? [MemberSession(step, run.Session)]
            : IsAllSessions(step) ? run.Elsewhere.Names
            : ListedSessions(step) ?? [MemberSession(step, run.Session)];
        return named
            .Select(ResolvedName)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, _ => SeqRange.All, StringComparer.Ordinal);
    }

    /// <summary>
    /// Runs one step. A tool's or an assertion's error, any other exception it raises (logged), and the batch's deadline
    /// passing each fail it; the client cancelling the call does not.
    /// </summary>
    private async Task<StepOutcome> RunStepOrDeadlineAsync(BatchRun run, int index, BatchStep step, CancellationToken clientCancellation)
    {
        try
        {
            run.Cancellation.ThrowIfCancellationRequested();
            return await RunStepAsync(run, index, step);
        }
        catch (OperationCanceledException) when (DeadlinePassed(run, clientCancellation))
        {
            return StepOutcome.Failed(DeadlineFailure(run));
        }
        catch (McpException e)
        {
            return StepOutcome.Failed(DeadlinePassed(run, clientCancellation) ? DeadlineFailure(run) : e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Unexpected(run, index, step, e);
        }
    }

    /// <summary>Whether the batch's own deadline, not the client, cancelled it.</summary>
    private static bool DeadlinePassed(BatchRun run, CancellationToken clientCancellation) =>
        run.Deadline.Token.IsCancellationRequested && !clientCancellation.IsCancellationRequested;

    /// <summary><see cref="DeadlineReason"/>, with the backstop clause when the batch's backstop, not its ceiling, ended it.</summary>
    private string DeadlineFailure(BatchRun run) =>
        run.Deadline.Reason == Session.DeadlineReason.Backstop ? DeadlineReason + run.Deadline.BackstopClause() : DeadlineReason;

    /// <summary>Logs an exception a step raised that no tool turned into an error, and fails the step with its type and message.</summary>
    private static StepOutcome Unexpected(BatchRun run, int index, BatchStep step, Exception exception)
    {
        ILogger logger = run.Server?.Services?.GetService<ILogger<RuntimeTools>>() ?? NullLogger<RuntimeTools>.Instance;
        Log.BatchStepFailed(logger, exception, index, step.Tool ?? step.Assert ?? "?");
        return StepOutcome.Failed($"{exception.GetType().Name}: {exception.Message}");
    }

    private Task<StepOutcome> RunStepAsync(BatchRun run, int index, BatchStep step)
    {
        string? session = step.Session ?? run.Session;
        return step.Tool is not null
            ? RunToolStepAsync(run, step.Tool, ToolArguments(step.Tool, step.Args, session))
            : AssertionKinds[step.Assert!].RunAsync(this, new AssertionCall(step, session, run, index));
    }

    /// <summary>Invokes a tool from the server's own collection, as a client's call would, and keeps its text as JSON.</summary>
    private static async Task<StepOutcome> RunToolStepAsync(BatchRun run, string name, Dictionary<string, JsonElement> arguments)
    {
        if (run.Server.ServerOptions.ToolCollection?.TryGetPrimitive(name, out McpServerTool? tool) is not true || tool is null)
        {
            throw new McpException($"The server does not serve {name}, so the batch cannot run it.");
        }

        // The step calls the tool directly, past the server's filters, so it checks its arguments as ArgumentErrors.Filter does.
        JsonElement schema = tool.ProtocolTool.InputSchema;
        string? unknown = ArgumentErrors.UnknownArgument(name, schema, arguments.Keys);
        if (unknown is not null)
        {
            throw new McpException(unknown);
        }

        CallToolRequestParams request = new() { Name = name, Arguments = arguments };
        JsonRpcRequest rpc = new() { Id = new RequestId($"{BatchToolName}/{name}"), Method = RequestMethods.ToolsCall };
        RequestContext<CallToolRequestParams> context = new(run.Server, rpc, request) { Services = run.Server.Services };
        try
        {
            CallToolResult result = await tool.InvokeAsync(context, run.Cancellation);
            string text = TextOf(result.Content);
            return result.IsError is true ? StepOutcome.Failed(text) : StepOutcome.Passed(ParseText(text));
        }
        catch (Exception e) when (ArgumentErrors.Describe(name, schema, arguments, e) is { } message)
        {
            throw new McpException(StepToolMessage(name, message), e);
        }
    }

    /// <summary>An argument error named for the step's tool: one that does not already start with the tool's name gets it as a prefix.</summary>
    private static string StepToolMessage(string name, string message) =>
        message.StartsWith(name, StringComparison.Ordinal) ? message : $"{name}: {message}";

    private async Task<StepOutcome> AssertWaitAsync(AssertionCall call)
    {
        WaitCondition condition = ConditionOf(call.Step);
        int? given = TimeoutOf(call.Step);
        int timeoutMs = WaitTimeoutMs(condition, given);
        JsonObject reply = JsonNode.Parse(await WaitForAsync(condition, given, call.Session, call.Run.Cancellation))!.AsObject();
        if (reply["met"]?.GetValue<bool>() is true)
        {
            return StepOutcome.Passed(reply);
        }

        string when = WaitWhenText(timeoutMs, reply);
        string shown = JsonSerializer.Serialize(condition, Json);
        return StepOutcome.Failed($"the {call.Step.Assert} assertion {shown} was not met {when}; wait_for returned {reply.ToJsonString()}");
    }

    /// <summary>
    /// The "when" a wait assertion that was not met names: "when checked" with no budget, else the budget, which the server
    /// counts on the load-adjusted clock, and, when the bridge's reply carries the wall time the wait took (<c>elapsedMs</c>),
    /// that too, so a refusal that took far longer than its budget on a busy machine is not read as a bug.
    /// </summary>
    internal static string WaitWhenText(int timeoutMs, JsonObject reply)
    {
        if (timeoutMs == 0)
        {
            return "when checked";
        }

        if (reply["elapsedMs"] is not { } elapsed)
        {
            return $"within {timeoutMs} ms";
        }

        string wall = (elapsed.GetValue<double>() / 1000).ToString("0.#", CultureInfo.InvariantCulture);
        return $"within {timeoutMs} ms (load-adjusted; {wall} s of wall time)";
    }

    /// <summary>
    /// The no_errors assertion: no errors in each session it checks since that session's mark, the batch's start or the
    /// previous no_errors that checked it; each mark checked moves to now.
    /// </summary>
    private StepOutcome AssertNoErrors(AssertionCall call)
    {
        GodotSession[] targets = NoErrorsTargets(call);
        List<(string Session, ErrorEntry[] Errors)> raised = [];
        foreach (GodotSession target in targets)
        {
            long mark = call.Run.Marks[target];
            long now = target.Errors.Mark();
            ErrorEntry[] errors = [.. target.Errors.ErrorsSince(mark).Where(entry => entry.Seq <= now)];
            call.Run.Marks[target] = now;
            if (errors.Length > 0)
            {
                raised.Add((target.Name, errors));
            }
        }

        if (raised.Count == 0)
        {
            JsonObject passed = new() { ["errors"] = 0 };
            if (call.Step.Sessions is not null)
            {
                passed["sessions"] = new JsonArray([.. targets.Select(target => JsonValue.Create(target.Name))]);
            }

            return StepOutcome.Passed(passed);
        }

        return StepOutcome.Failed(
            targets.Length == 1
                ? $"the game raised {raised[0].Errors.Length} error(s) since the batch started or its previous no_errors:\n"
                    + ErrorReport.Summarise(raised[0].Errors)
                : string.Join('\n', raised.Select(session => SessionErrorsText(session.Session, session.Errors)))
        );
    }

    /// <summary>One session's part of a failed no_errors that checks several.</summary>
    private static string SessionErrorsText(string session, ErrorEntry[] errors) =>
        $"session '{session}' raised {errors.Length} error(s) since the batch started or its previous no_errors:\n{ErrorReport.Summarise(errors)}";

    /// <summary>The sessions a no_errors step checks, by name: every one the batch marked for "all", else those it lists, else its own.</summary>
    private GodotSession[] NoErrorsTargets(AssertionCall call)
    {
        IEnumerable<GodotSession> targets = IsAllSessions(call.Step)
            ? call.Run.Marks.Keys
            : (ListedSessions(call.Step) ?? [call.Session]).Select(name => Find(name));
        return [.. targets.OrderBy(target => target.Name, StringComparer.Ordinal)];
    }

    private async Task<StepOutcome> AssertScreenshotAsync(AssertionCall call)
    {
        BatchStep step = call.Step;
        IEnumerable<ContentBlock> blocks = await CompareScreenshotAsync(
            step.Name!,
            null,
            step.Tolerance ?? DefaultTolerance,
            CompareOptionsOf(step),
            call.Session,
            call.Run.Cancellation
        );
        JsonObject result = JsonNode.Parse(TextOf(blocks))!.AsObject();
        return result["match"]?.GetValue<bool>() is true
            ? StepOutcome.Passed(result)
            : StepOutcome.Failed($"the screenshot does not match baseline '{step.Name}': {result.ToJsonString()}");
    }

    private static string TextOf(IEnumerable<ContentBlock> blocks) => string.Concat(blocks.OfType<TextContentBlock>().Select(block => block.Text));

    /// <summary>
    /// The fired assertion: the nearest earlier tool step's result lists an entry for the step's node and signal, with the
    /// step's arguments when it gives equals.
    /// </summary>
    private static StepOutcome AssertFired(AssertionCall call)
    {
        (int Index, string Tool, JsonObject? Result) toolStep =
            call.Run.LastTool ?? throw new McpException(NeedsToolStepRefusal($"step {call.Index}", call.Step.Assert!));
        FiredCheck check = CheckFired(call.Step, call.Index, toolStep.Index, toolStep.Tool, toolStep.Result);
        return check.Reason is { } reason ? StepOutcome.Failed(reason) : StepOutcome.Passed(check.Result);
    }

    /// <summary>
    /// Checks a fired assertion against a tool step's result: the entry whose node is the step's or ends with it on a path
    /// segment boundary, whose signal is the step's, and whose arguments equal the step's equals when it gives one. A
    /// passing check answers {fired: entry}, a failing one the reason.
    /// </summary>
    internal static FiredCheck CheckFired(BatchStep step, int index, int toolIndex, string tool, JsonObject? result)
    {
        if (result?["fired"] is not JsonArray fired)
        {
            return FiredCheck.Failed(
                $"step {index}: the fired assertion checks step {toolIndex} ({tool}), whose result has no fired list, so "
                    + "there is nothing to check."
            );
        }

        JsonObject? matched = fired.OfType<JsonObject>().FirstOrDefault(entry => MatchesFiredEntry(step, entry));
        return matched is not null
            ? FiredCheck.Passed(new JsonObject { ["fired"] = matched.DeepClone() })
            : FiredCheck.Failed(FiredFailure(step, toolIndex, result, fired));
    }

    /// <summary>Whether an entry is the step's node and signal, its arguments equal to equals when the step gives one.</summary>
    private static bool MatchesFiredEntry(BatchStep step, JsonObject entry) =>
        NodeMatches(EntryText(entry["node"]), step.Node!) && EntryText(entry["signal"]) == step.Signal && ArgsMatch(step.EqualsValue, entry["args"]);

    /// <summary>Whether an entry's node is the wanted path, or ends with it on a path segment boundary.</summary>
    private static bool NodeMatches(string? entryNode, string wanted) =>
        entryNode == wanted || (entryNode?.EndsWith("/" + wanted, StringComparison.Ordinal) ?? false);

    /// <summary>
    /// Whether an entry's arguments equal the step's equals, the whole array. An entry with none reads as an empty array,
    /// and a step that gives no equals matches any arguments.
    /// </summary>
    private static bool ArgsMatch(JsonElement? equals, JsonNode? args)
    {
        if (equals is not { } expected || expected.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), args ?? new JsonArray());
    }

    /// <summary>The text of a fired assertion that matched no entry.</summary>
    private static string FiredFailure(BatchStep step, int toolIndex, JsonObject result, JsonArray fired)
    {
        string wanted = $"{step.Node}.{step.Signal}";
        if (step.EqualsValue is { ValueKind: not JsonValueKind.Null } equals)
        {
            wanted += $" with args {equals.GetRawText()}";
        }

        string summary = FiredSummary(step, result, fired);
        return $"the fired assertion wanted {wanted} but step {toolIndex} fired: {summary}{DroppedClause(result)}";
    }

    /// <summary>What a failed fired assertion says the step before fired: ten entries as node.signal, else what was listened on.</summary>
    private static string FiredSummary(BatchStep step, JsonObject result, JsonArray fired)
    {
        if (fired.Count == 0)
        {
            string[] listenedOn = [.. (result["listenedOn"] as JsonArray ?? []).Select(EntryText).OfType<string>()];
            return listenedOn.Length == 0 ? "nothing fired and nothing was listened on" : $"nothing fired on {string.Join(", ", listenedOn)}";
        }

        string[] shown = [.. fired.Take(MaxFiredFailureEntries).Select(entry => EntryName(entry, step))];
        return fired.Count <= MaxFiredFailureEntries
            ? string.Join(", ", shown)
            : $"{string.Join(", ", shown)}, … ({fired.Count - MaxFiredFailureEntries} more)";
    }

    /// <summary>The clause a failure adds when the step's fired list was cut, empty when nothing was dropped.</summary>
    private static string DroppedClause(JsonObject result)
    {
        int dropped = DroppedCount(result);
        return dropped > 0 ? $"; {dropped} more were dropped from the fired list" : "";
    }

    /// <summary>The entries the step's result dropped past its fired list, 0 when it carries none or not a number.</summary>
    private static int DroppedCount(JsonObject result)
    {
        if (result["firedDropped"] is not JsonValue value)
        {
            return 0;
        }

        return value.TryGetValue(out int dropped) ? dropped
            : value.TryGetValue(out double number) ? (int)number
            : 0;
    }

    /// <summary>
    /// An entry as node.signal for a failure's list, with its arguments when it is the node and signal the assertion wanted
    /// (so equals is what failed); "?" for an entry that is not an object.
    /// </summary>
    private static string EntryName(JsonNode? entry, BatchStep step)
    {
        if (entry is not JsonObject item)
        {
            return "?";
        }

        string name = $"{EntryText(item["node"])}.{EntryText(item["signal"])}";
        bool wanted = NodeMatches(EntryText(item["node"]), step.Node!) && EntryText(item["signal"]) == step.Signal;
        return wanted ? $"{name} with args {CompactArgs(item["args"])}" : name;
    }

    /// <summary>An entry's arguments as compact JSON, an empty array when it carries none.</summary>
    private static string CompactArgs(JsonNode? args) => args?.ToJsonString() ?? "[]";

    /// <summary>The string a node holds, or null when it is not a string.</summary>
    private static string? EntryText(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>A tool's text as JSON when it parses, else as a JSON string.</summary>
    private static JsonNode? ParseText(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    /// <summary>
    /// What a batch run carries from step to step: the server, the default session, the no_errors marks, the errorsElsewhere
    /// cursors and the deadline.
    /// </summary>
    private sealed record BatchRun(
        McpServer Server,
        string? Session,
        Dictionary<GodotSession, long> Marks,
        ErrorCursors Elsewhere,
        LoadDeadline Deadline
    )
    {
        /// <summary>
        /// Cancelled when the batch's deadline passes or the client cancels the call; in a group's members, also when the
        /// group's first member fails.
        /// </summary>
        public CancellationToken Cancellation { get; init; } = Deadline.Token;

        /// <summary>The nearest earlier tool step's index, tool and result, whose fired list a fired assertion checks.</summary>
        public (int Index, string Tool, JsonObject? Result)? LastTool { get; set; }
    }

    /// <summary>
    /// A batch's errorsElsewhere cursors: per session, by name, the last error sequence number an entry has read, kept apart
    /// from the no_errors marks so that neither takes errors from the other. Each error shows on one entry at most, the first
    /// to end after the server received it, unless that entry's step addressed its session when it was raised.
    /// </summary>
    private sealed class ErrorCursors
    {
        private readonly Cursor[] _cursors;

        /// <param name="marks">Each session's name, error feed and the mark its cursor starts from.</param>
        public ErrorCursors(IReadOnlyList<(string Name, ErrorFeed Feed, long Mark)> marks)
        {
            _cursors = [.. marks.OrderBy(mark => mark.Name, StringComparer.Ordinal).Select(mark => new Cursor(mark.Name, mark.Feed, mark.Mark))];
            Names = _cursors.Select(cursor => cursor.Name).ToFrozenSet(StringComparer.Ordinal);
        }

        /// <summary>The sessions' names.</summary>
        public IReadOnlySet<string> Names { get; }

        /// <summary>
        /// Adds errorsElsewhere, and errorsElsewhereOmitted when a session's errors did not all fit, to <paramref name="entry"/>
        /// for the errors each session raised since its cursor but those in its range in <paramref name="addressed"/>, and moves
        /// every cursor to its feed's current mark. With none, the entry is left as it is.
        /// </summary>
        public void AddTo(JsonObject entry, IReadOnlyDictionary<string, SeqRange> addressed)
        {
            JsonObject errors = [];
            JsonObject omitted = [];
            foreach (Cursor cursor in _cursors)
            {
                SeqRange skipped = addressed.GetValueOrDefault(cursor.Name, SeqRange.None);
                ErrorEntry[] raised = [.. cursor.Advance().Where(error => !skipped.Holds(error.Seq))];
                if (raised.Length == 0)
                {
                    continue;
                }

                (JsonArray shown, int left) = ErrorReport.Shape(raised);
                errors[cursor.Name] = shown;
                if (left > 0)
                {
                    omitted[cursor.Name] = left;
                }
            }

            if (errors.Count > 0)
            {
                entry["errorsElsewhere"] = errors;
            }

            if (omitted.Count > 0)
            {
                entry["errorsElsewhereOmitted"] = omitted;
            }
        }

        /// <summary>One session's cursor over its error feed.</summary>
        private sealed class Cursor(string name, ErrorFeed feed, long seq)
        {
            public string Name => name;

            /// <summary>The errors raised after the cursor, up to the feed's current mark, to which the cursor then moves.</summary>
            public ErrorEntry[] Advance()
            {
                long now = feed.Mark();
                long from = seq;
                seq = now;
                return [.. feed.ErrorsSince(from).Where(entry => entry.Seq <= now)];
            }
        }
    }

    /// <summary>The error sequence numbers after <paramref name="After"/> up to <paramref name="Upto"/>.</summary>
    private readonly record struct SeqRange(long After, long Upto)
    {
        /// <summary>Every sequence number: a session a step addressed throughout.</summary>
        public static SeqRange All => new(long.MinValue, long.MaxValue);

        /// <summary>No sequence number: a session no step addressed.</summary>
        public static SeqRange None => new(0, 0);

        public bool Holds(long seq) => seq > After && seq <= Upto;
    }

    /// <summary>
    /// A group member's call on its session's error feed: the marks just before and just after it, so the group's entry leaves
    /// out only the errors the member's own result covers.
    /// </summary>
    private sealed class MemberCall(GodotSession? session)
    {
        private readonly long _before = session?.Errors.Mark() ?? 0;
        private long _after;

        /// <summary>The session's name; null when the member's session resolved to none.</summary>
        public string? Session => session?.Name;

        public SeqRange Range => new(_before, _after);

        /// <summary>Takes the mark just after the call.</summary>
        public void End() => _after = session?.Errors.Mark() ?? 0;
    }

    /// <summary>One assertion step as it runs: the step, its index, the session it addresses, and its batch.</summary>
    private sealed record AssertionCall(BatchStep Step, string? Session, BatchRun Run, int Index);

    /// <summary>
    /// An assertion kind: the fields it needs (for the refusal), whether a step has them, its further checks before the batch
    /// runs, how it runs, and whether it checks an earlier tool step.
    /// </summary>
    private sealed record AssertionKind(
        string Needs,
        Func<BatchStep, bool> IsComplete,
        Action<BatchStep> Check,
        Func<RuntimeTools, AssertionCall, Task<StepOutcome>> RunAsync,
        bool NeedsToolBefore = false
    );

    /// <summary>A fired assertion's check: what it answers when it matches ({fired: entry}), or why it did not.</summary>
    internal sealed record FiredCheck(JsonNode? Result, string? Reason)
    {
        public static FiredCheck Passed(JsonNode result) => new(result, null);

        public static FiredCheck Failed(string reason) => new(null, reason);
    }

    /// <summary>A step's outcome: its result when it passed, the reason when it failed.</summary>
    private sealed record StepOutcome(bool Ok, JsonNode? Result, string? Error)
    {
        public static StepOutcome Passed(JsonNode? result) => new(true, result, null);

        public static StepOutcome Failed(string reason) => new(false, null, reason);

        public JsonObject ToEntry(int index, BatchStep step, double startMs, double endMs)
        {
            JsonObject entry = new() { ["index"] = index };
            entry[EntryKind(step)] = step.Tool ?? step.Assert;
            entry["ok"] = Ok;
            if (Ok)
            {
                entry["result"] = Result;
            }
            else
            {
                entry["error"] = Error;
            }

            entry["startMs"] = startMs;
            entry["endMs"] = endMs;
            return entry;
        }
    }

    /// <summary>A step refused as the batch starts, before any step runs: its place, its member's in a group, and why.</summary>
    private sealed record StepRefusal(int Index, int? Member, string Reason)
    {
        public JsonObject ToFailedAt()
        {
            JsonObject failedAt = new() { ["index"] = Index };
            if (Member is { } member)
            {
                failedAt["member"] = member;
            }

            failedAt["reason"] = Reason;
            return failedAt;
        }
    }

    /// <summary>
    /// One parallel group as it runs: the batch run its members share, whose cancellation is the group's; the group's
    /// place; its cancel; the client's cancellation; and the first member to fail, by completion order.
    /// </summary>
    private sealed class GroupRun(BatchRun run, int index, CancellationTokenSource cancel, CancellationToken clientCancellation)
    {
        private readonly Lock _lock = new();

        public BatchRun Run => run;

        public int Index => index;

        public CancellationTokenSource Cancel => cancel;

        public CancellationToken ClientCancellation => clientCancellation;

        private readonly Dictionary<string, SeqRange> _calls = new(StringComparer.Ordinal);

        /// <summary>The first member to fail and its reason; null while none has.</summary>
        public (int Member, string Reason)? Failure { get; private set; }

        /// <summary>Keeps the call of a member that ended with its own outcome; a cancelled member's session counts as unaddressed.</summary>
        public void Called(MemberCall call)
        {
            if (call.Session is not { } name)
            {
                return;
            }

            lock (_lock)
            {
                _calls[name] = call.Range;
            }
        }

        /// <summary>Each member session's call range, by session name, for the members that ended with their own outcome.</summary>
        public Dictionary<string, SeqRange> Calls()
        {
            lock (_lock)
            {
                return new Dictionary<string, SeqRange>(_calls, StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// Takes a member's outcome as it ends: the first failure becomes the group's and cancels the members still running.
        /// </summary>
        /// <param name="member">The member's place in the group.</param>
        /// <param name="outcome">Its outcome; null when the group's cancel ended it.</param>
        /// <param name="cancelledBefore">Whether the group's token had fired when the outcome came in.</param>
        /// <returns>Whether the member counts as cancelled: a later failure, or none, after the group's token fired.</returns>
        public bool Judge(int member, StepOutcome? outcome, bool cancelledBefore)
        {
            if (outcome is { Ok: true })
            {
                return false;
            }

            bool first = false;
            lock (_lock)
            {
                if (Failure is null && outcome is not null)
                {
                    Failure = (member, outcome.Error ?? "");
                    first = true;
                }
            }

            if (first)
            {
                // Outside the lock: the cancel runs the members' callbacks and continuations inline.
                cancel.Cancel();
                return false;
            }

            return cancelledBefore || outcome is null;
        }
    }
}
