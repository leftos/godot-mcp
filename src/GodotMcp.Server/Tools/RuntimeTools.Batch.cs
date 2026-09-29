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
    private const string AssertionKindList = "property, expression, wait, no_errors, screenshot";

    /// <summary>The tools a batch step may name: every tool declared on this class but batch_drive itself.</summary>
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
    }.ToFrozenDictionary();

    [McpServerTool(Name = BatchToolName, ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Plays a list of steps against a running game in one call, in order, and stops at the first failed assertion or "
            + "failed tool step. A step is either {tool, args}: a runtime tool by its name with the arguments it takes on its "
            + "own (take_screenshot and compare_screenshot are forced to path_only; image blocks are dropped), or an assertion "
            + "{assert, ...}: property {node, property, equals, timeoutMs?}, expression {expression, timeoutMs?} (timeoutMs "
            + "defaults to 0: checked once, now, even while paused), wait {any wait_for condition, gameMs and frames included, "
            + "timeoutMs?} (default 10000; gameMs + 10000 for gameMs, 10 s + 100 ms a frame for frames), no_errors {} (no "
            + "errors since the previous no_errors, or since the batch started), screenshot {name, "
            + "tolerance?, maxChangedRatio?} (compare_screenshot must match). Any step may carry session. At most 100 steps; "
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

        for (int index = 0; index < steps.Count; index++)
        {
            CheckStep(index, steps[index]);
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
            .Where(name => name != BatchToolName)
            .ToFrozenSet();

    private static void CheckStep(int index, BatchStep? step)
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

        CheckAssertion(index, step);
    }

    private static void CheckAssertion(int index, BatchStep step)
    {
        if (!AssertionKinds.TryGetValue(step.Assert!, out AssertionKind? kind))
        {
            throw new McpException($"step {index}: assert '{step.Assert}' is not one of {AssertionKindList}.");
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

    private static void CheckWaitAssertion(BatchStep step) => _ = BuildWaitParameters(ConditionOf(step), TimeoutOf(step));

    private static void CheckScreenshotAssertion(BatchStep step)
    {
        ScreenshotBaseline.CheckName(step.Name!);
        _ = CheckCompareArguments(null, step.Tolerance ?? DefaultTolerance, CompareOptionsOf(step));
    }

    /// <summary>The wait_for condition an assertion checks: only its own kind's fields for property and expression.</summary>
    private static WaitCondition ConditionOf(BatchStep step) =>
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
                step.Frames
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
            return await RunStepAsync(run, step);
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

    private Task<StepOutcome> RunStepAsync(BatchRun run, BatchStep step)
    {
        string? session = step.Session ?? run.Session;
        return step.Tool is not null
            ? RunToolStepAsync(run, step.Tool, ToolArguments(step.Tool, step.Args, session))
            : AssertionKinds[step.Assert!].RunAsync(this, new AssertionCall(step, session, run));
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

        string when = timeoutMs == 0 ? "when checked" : $"within {timeoutMs} ms";
        string shown = JsonSerializer.Serialize(condition, Json);
        return StepOutcome.Failed($"the {call.Step.Assert} assertion {shown} was not met {when}; wait_for returned {reply.ToJsonString()}");
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
    }

    /// <summary>One assertion step as it runs: the step, the session it addresses, and its batch.</summary>
    private sealed record AssertionCall(BatchStep Step, string? Session, BatchRun Run);

    /// <summary>
    /// An assertion kind: the fields it needs (for the refusal), whether a step has them, its further checks before the batch
    /// runs, and how it runs.
    /// </summary>
    private sealed record AssertionKind(
        string Needs,
        Func<BatchStep, bool> IsComplete,
        Action<BatchStep> Check,
        Func<RuntimeTools, AssertionCall, Task<StepOutcome>> RunAsync
    );

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
