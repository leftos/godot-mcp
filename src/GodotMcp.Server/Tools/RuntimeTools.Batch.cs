using System.Collections.Frozen;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
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
/// error feed or compare_screenshot. The batch stops at the first step that fails.
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const int MaxBatchSteps = 100;
    internal const string BatchToolName = "batch_drive";
    internal static readonly TimeSpan DefaultBatchDeadline = TimeSpan.FromSeconds(300);
    private const int DefaultTolerance = 2;
    private const int MaxFiredFailureEntries = 10;
    private const string AssertionKindList = "property, expression, wait, no_errors, screenshot, fired";

    /// <summary>
    /// The tools a batch step may name: every tool declared on this class but batch_drive itself and save_screenshot, which
    /// writes a project file, a power batch_drive's edit-live class does not grant.
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
        ["wait"] = new("a wait_for condition", _ => true, CheckWaitAssertion, (tools, call) => tools.AssertWaitAsync(call)),
        ["no_errors"] = new("nothing", _ => true, _ => { }, (tools, call) => Task.FromResult(tools.AssertNoErrors(call))),
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

    /// <summary>How a step shows that each field a fired assertion does not take was given.</summary>
    private static readonly (string Name, Func<BatchStep, bool> Given)[] FiredForeignStepFields =
    [
        ("args", step => step.Args is not null),
        ("exists", step => step.Exists is not null),
        ("property", step => !string.IsNullOrEmpty(step.Property)),
        ("expression", step => !string.IsNullOrEmpty(step.Expression)),
        ("uiChanged", step => step.UiChanged is not null),
        ("gameMs", step => step.GameMs is not null),
        ("frames", step => step.Frames is not null),
        ("timeoutMs", step => step.TimeoutMs is not null),
        ("name", step => !string.IsNullOrEmpty(step.Name)),
        ("tolerance", step => step.Tolerance is not null),
        ("maxChangedRatio", step => step.MaxChangedRatio is not null),
        ("text", step => step.Text is not null),
        ("under", step => step.Under is not null),
        ("stable", step => step.Stable is not null),
    ];

    [McpServerTool(Name = BatchToolName, ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Plays a list of steps against a running game in one call, in order, and stops at the first failed assertion or "
            + "failed tool step. A step is either {tool, args}: a runtime tool by its name with the arguments it takes on its "
            + "own (take_screenshot and compare_screenshot are forced to path_only; image blocks are dropped), or an assertion "
            + "{assert, ...}: property {node, property, equals, timeoutMs?}, expression {expression, timeoutMs?} (timeoutMs "
            + "defaults to 0: checked once, now, even while paused), wait {any wait_for condition, gameMs and frames included, "
            + "timeoutMs?} (default 10000; gameMs + 10000 for gameMs, 10 s + 100 ms a frame for frames, 10 s + 100 ms for each "
            + "of the stable + 1 frames a settled wait reads), no_errors {} (no "
            + "errors since the previous no_errors, or since the batch started), screenshot {name, "
            + "tolerance?, maxChangedRatio?} (compare_screenshot must match), fired {node, signal, equals?} (the nearest "
            + "earlier tool step's fired list has that node's signal, with those arguments when equals is given). Any step may "
            + "carry session. At most 100 steps; "
            + "the whole batch stops after 300 s of load-adjusted time. Returns {passed, steps: [{index, tool or assert, ok, result or error}], "
            + "failedAt?: {index, reason}}."
    )]
    public async Task<string> BatchDriveAsync(
        [Description("The steps, in order: {tool, args, session?} or {assert, …, session?}; 1 to 100 of them.")] IReadOnlyList<BatchStep> steps,
        McpServer server,
        [Description(ProjectTools.SessionDescription + " Every step addresses it unless the step names its own.")] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        CheckBatch(steps);
        using LoadDeadline deadline = sessions.Clock.Start(BatchDeadline, cancellationToken);
        BatchRun run = new(server, session, MarkErrorFeeds(steps, session), deadline);
        JsonArray entries = [];
        JsonObject? failedAt = null;
        for (int index = 0; index < steps.Count && failedAt is null; index++)
        {
            StepOutcome outcome = await RunStepOrDeadlineAsync(run, index, steps[index], cancellationToken);
            RecordToolStep(run, index, steps[index], outcome);
            entries.Add(outcome.ToEntry(index, steps[index]));
            failedAt = outcome.Ok ? null : new JsonObject { ["index"] = index, ["reason"] = outcome.Error };
        }

        JsonObject result = new() { ["passed"] = failedAt is null, ["steps"] = entries };
        if (failedAt is not null)
        {
            result["failedAt"] = failedAt;
        }

        return result.ToJsonString();
    }

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

        if (steps.Count > MaxBatchSteps)
        {
            throw new McpException($"a batch takes at most {MaxBatchSteps} steps; split it.");
        }

        bool toolStepBefore = false;
        for (int index = 0; index < steps.Count; index++)
        {
            CheckStep(index, steps[index], toolStepBefore);
            toolStepBefore |= steps[index]?.Tool is not null;
        }
    }

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

    private static void CheckStep(int index, BatchStep? step, bool toolStepBefore)
    {
        if (step is null || (step.Tool is null) == (step.Assert is null))
        {
            throw new McpException($"step {index}: give exactly one of tool and assert.");
        }

        if (step.Tool is not null)
        {
            if (!BatchableTools.Contains(step.Tool))
            {
                throw new McpException(
                    $"step {index}: '{step.Tool}' cannot run in a batch; a tool step names a runtime tool, one of: "
                        + $"{string.Join(", ", BatchableTools.Order(StringComparer.Ordinal))}."
                );
            }

            return;
        }

        CheckAssertion(index, step, toolStepBefore);
    }

    private static void CheckAssertion(int index, BatchStep step, bool toolStepBefore)
    {
        if (!AssertionKinds.TryGetValue(step.Assert!, out AssertionKind? kind))
        {
            throw new McpException($"step {index}: assert '{step.Assert}' is not one of {AssertionKindList}.");
        }

        if (kind.NeedsToolBefore && !toolStepBefore)
        {
            throw new McpException(NeedsToolStepRefusal(index, step.Assert!));
        }

        if (!kind.IsComplete(step))
        {
            throw new McpException($"step {index}: the {step.Assert} assertion needs {kind.Needs}.");
        }

        try
        {
            kind.Check(step);
        }
        catch (McpException e)
        {
            throw new McpException($"step {index}: {e.Message}", e);
        }
    }

    private static bool HasPropertyFields(BatchStep step) =>
        !string.IsNullOrEmpty(step.Node) && !string.IsNullOrEmpty(step.Property) && step.EqualsValue is { ValueKind: not JsonValueKind.Null };

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
    private static string NeedsToolStepRefusal(int index, string assert) =>
        $"step {index}: assert '{assert}' needs a tool step before it, whose fired list it checks.";

    private static void CheckWaitAssertion(BatchStep step) => _ = BuildWaitParameters(ConditionOf(step), TimeoutOf(step));

    private static void CheckScreenshotAssertion(BatchStep step)
    {
        ScreenshotBaseline.CheckName(step.Name!);
        _ = CheckCompareArguments(null, step.Tolerance ?? DefaultTolerance, CompareOptionsOf(step));
    }

    /// <summary>The wait_for condition an assertion checks: only its own kind's fields for property and expression.</summary>
    internal static WaitCondition ConditionOf(BatchStep step) =>
        step.Assert switch
        {
            "property" => new WaitCondition(Node: step.Node, Property: step.Property, EqualsValue: step.EqualsValue),
            "expression" => new WaitCondition(Node: step.Node, Expression: step.Expression),
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
                step.Stable
            ),
        };

    /// <summary>The step's timeoutMs; left out, 0 for property and expression, and wait_for's own default for wait.</summary>
    private static int? TimeoutOf(BatchStep step) => step.Assert == "wait" ? step.TimeoutMs : step.TimeoutMs ?? 0;

    private static CompareOptions CompareOptionsOf(BatchStep step) => new(step.MaxChangedRatio ?? 0, "path_only");

    /// <summary>Marks the error feed of every session a no_errors step addresses, so its first check counts from here.</summary>
    private Dictionary<GodotSession, long> MarkErrorFeeds(IReadOnlyList<BatchStep> steps, string? session)
    {
        Dictionary<GodotSession, long> marks = [];
        for (int index = 0; index < steps.Count; index++)
        {
            if (steps[index].Assert != "no_errors")
            {
                continue;
            }

            try
            {
                GodotSession target = Find(steps[index].Session ?? session);
                marks.TryAdd(target, target.Errors.Mark());
            }
            catch (McpException e)
            {
                throw new McpException($"step {index}: {e.Message}", e);
            }
        }

        return marks;
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
        run.Cancellation.IsCancellationRequested && !clientCancellation.IsCancellationRequested;

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

    private StepOutcome AssertNoErrors(AssertionCall call)
    {
        GodotSession target = Find(call.Session);
        long mark = call.Run.Marks[target];
        long now = target.Errors.Mark();
        ErrorEntry[] errors = [.. target.Errors.ErrorsSince(mark).Where(entry => entry.Seq <= now)];
        call.Run.Marks[target] = now;
        return errors.Length == 0
            ? StepOutcome.Passed(new JsonObject { ["errors"] = 0 })
            : StepOutcome.Failed(
                $"the game raised {errors.Length} error(s) since the batch started or its previous no_errors:\n{ErrorReport.Summarise(errors)}"
            );
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
            call.Run.LastTool ?? throw new McpException(NeedsToolStepRefusal(call.Index, call.Step.Assert!));
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

    /// <summary>What a batch run carries from step to step: the server, the default session, the error marks and the deadline.</summary>
    private sealed record BatchRun(McpServer Server, string? Session, Dictionary<GodotSession, long> Marks, LoadDeadline Deadline)
    {
        /// <summary>Cancelled when the batch's deadline passes or the client cancels the call.</summary>
        public CancellationToken Cancellation => Deadline.Token;

        /// <summary>The nearest earlier tool step's index, tool and result, whose fired list a fired assertion checks.</summary>
        public (int Index, string Tool, JsonObject? Result)? LastTool { get; set; }
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

        public JsonObject ToEntry(int index, BatchStep step)
        {
            JsonObject entry = new() { ["index"] = index };
            entry[step.Tool is not null ? "tool" : "assert"] = step.Tool ?? step.Assert;
            entry["ok"] = Ok;
            if (Ok)
            {
                entry["result"] = Result;
            }
            else
            {
                entry["error"] = Error;
            }

            return entry;
        }
    }
}
