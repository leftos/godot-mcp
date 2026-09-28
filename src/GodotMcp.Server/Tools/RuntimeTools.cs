using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The running game through its bridge: screenshots, the Control tree, scripts run inside it and the session's errors here,
/// input in RuntimeTools.Input.cs. Every call's result carries the errors the game raised while the call ran: the bridge
/// sends them ahead of its reply, so they are in the session's feed once the reply is.
/// </summary>
[McpServerToolType]
internal sealed partial class RuntimeTools(SessionRegistry sessions, CSharpBridge csharp)
{
    internal const int MaxErrorsLimit = ErrorFeed.Capacity;
    internal const int MaxPageSize = 500;
    internal const int MaxValueLength = 20000;
    internal const string ResponseModeDescription =
        "path_only: the path and size only; preview: also the image, scaled down to previewMaxWidth when wider "
        + "(the scaled copy is saved beside the full one); full: also the full-resolution image.";
    internal const string PreviewMaxWidthDescription = "The widest the preview image may be, in pixels; 480 by default.";
    private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UiElementsTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "take_screenshot", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Captures the running game's next drawn frame and saves it as a PNG under the project's .godot/godot-mcp/screenshots/ "
            + "(which git ignores). Returns the file's absolute path and size, plus an image unless responseMode is path_only: by "
            + "default a preview at most 480 px wide, saved beside the full-size PNG, whose path is always returned."
    )]
    public async Task<IEnumerable<ContentBlock>> TakeScreenshotAsync(
        [Description(ResponseModeDescription)] string responseMode = "preview",
        [Description(
            "A rectangle to keep, in the screenshot's pixels from its top-left corner (the viewport's pixels when the project does not "
                + "stretch); any part outside the screenshot is dropped."
        )]
            ScreenshotCrop? crop = null,
        [Description(PreviewMaxWidthDescription)] int previewMaxWidth = 480,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        ScreenshotMode mode = ParseMode(responseMode);
        JsonObject parameters = BuildScreenshotParameters(mode, crop, previewMaxWidth);
        BridgeCall call = new("take_screenshot", "screenshot", parameters, ScreenshotTimeout);
        return await CaptureAsync(Find(session), call, mode, addFields: null, cancellationToken);
    }

    /// <summary>
    /// Sends a screenshot-shaped bridge call and shapes its reply as take_screenshot does: a text block with the saved files'
    /// paths and sizes, what <paramref name="addFields"/> adds from the reply, and the errors the game raised meanwhile, then
    /// the image <paramref name="mode"/> asks for.
    /// </summary>
    /// <exception cref="McpException">The call failed, or the reply names no file, or the image cannot be read.</exception>
    internal static async Task<IEnumerable<ContentBlock>> CaptureAsync(
        GodotSession target,
        BridgeCall call,
        ScreenshotMode mode,
        Action<JsonObject, JsonNode?>? addFields,
        CancellationToken cancellationToken
    )
    {
        BridgeResult result = await CallWithErrorsAsync(target, call, cancellationToken);
        ScreenshotFiles files = ReadScreenshotFiles(result.Reply);
        JsonObject text = JsonSerializer.SerializeToNode(files, Json)!.AsObject();
        addFields?.Invoke(text, result.Reply);
        List<ContentBlock> blocks = [new TextContentBlock { Text = ErrorReport.AddTo(text, result.Errors).ToJsonString() }];
        string? imagePath = mode switch
        {
            ScreenshotMode.Preview => files.PreviewPath ?? files.Path,
            ScreenshotMode.Full => files.Path,
            _ => null,
        };
        if (imagePath is not null)
        {
            blocks.Add(ImageContentBlock.FromBytes(await ReadImageAsync(imagePath, cancellationToken), "image/png"));
        }

        return blocks;
    }

    [McpServerTool(Name = "get_ui_elements", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Lists the running game's Controls, depth first: path, name, class, rect ({x, y, width, height} in viewport coordinates, "
            + "from get_global_rect), visible, and where they apply text (Label, Button, LineEdit, RichTextLabel), disabled "
            + "(buttons) and tooltip. Returns one page, {elements, total, offset}, plus next, the offset of the following page, "
            + "while more remain."
    )]
    public async Task<string> GetUiElementsAsync(
        [Description("Skip every Control that is not visible in the tree, and everything under it.")] bool visibleOnly = true,
        [Description("Only Controls of this engine class or a subclass of it, e.g. BaseButton.")] string? filter = null,
        [Description("How many Controls of the list to skip: 0, or the next of the previous page.")] int offset = 0,
        [Description("How many Controls to return, 1 to 500.")] int limit = 100,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        CheckPage(offset, limit);
        JsonObject parameters = new() { ["visibleOnly"] = visibleOnly, ["classFilter"] = filter ?? string.Empty };
        BridgeCall call = new("get_ui_elements", "ui_elements", parameters, UiElementsTimeout);
        BridgeResult result = await CallWithErrorsAsync(Find(session), call, cancellationToken);
        return ErrorReport.AddTo(PageElements(result.Reply, offset, limit), result.Errors).ToJsonString();
    }

    /// <exception cref="McpException">The offset is negative, or the limit is outside 1 to <see cref="MaxPageSize"/>.</exception>
    internal static void CheckPage(int offset, int limit)
    {
        if (offset < 0)
        {
            throw new McpException($"offset must be 0 or more; got {offset}.");
        }

        if (limit is < 1 or > MaxPageSize)
        {
            throw new McpException($"limit must be 1 to {MaxPageSize}; got {limit}.");
        }
    }

    /// <summary>
    /// The bridge's element list cut to <paramref name="limit"/> elements from <paramref name="offset"/>:
    /// <c>{elements, total, offset}</c>, plus <c>next</c> while elements remain after the page.
    /// </summary>
    internal static JsonObject PageElements(JsonNode? reply, int offset, int limit) => PageList(reply, "elements", offset, limit);

    /// <summary>
    /// The bridge's list under <paramref name="key"/> cut to <paramref name="limit"/> items from <paramref name="offset"/>:
    /// <c>{key, total, offset}</c>, plus <c>next</c> while items remain after the page.
    /// </summary>
    internal static JsonObject PageList(JsonNode? reply, string key, int offset, int limit)
    {
        JsonArray all = reply?[key] as JsonArray ?? [];
        JsonArray page = [.. all.Skip(offset).Take(limit).Select(element => element?.DeepClone())];
        JsonObject result = new()
        {
            [key] = page,
            ["total"] = all.Count,
            ["offset"] = offset,
        };
        if (offset + page.Count < all.Count)
        {
            result["next"] = offset + page.Count;
        }

        return result;
    }

    [McpServerTool(Name = "run_script", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Runs GDScript inside the running game. The script must `extends RefCounted` and define "
            + "`func execute(scene_tree: SceneTree) -> Variant`, which may await. Returns {value}, execute's value as JSON: "
            + "Vector2/3 as {x, y[, z]}, Color as {r, g, b, a}, Rect2 as {x, y, width, height}, a Node as its path, another "
            + "Object as {class, string}, Dictionaries and Arrays recursively. A value whose JSON is longer than 20000 characters "
            + "comes back as {valuePreview, valueLength}: its first 20000 characters and its length. Errors raised while it ran "
            + "come back in errors, each with its file, line and stack. The call fails, with those errors, on a compile error, "
            + "or when execute returns null and an error is located in the script itself (its file, or its most recent frame, is "
            + "gdscript://…): a runtime error ends execute with null. A null value with errors located elsewhere (a game "
            + "script execute called, another thread) still succeeds. In a C# project, read C# members with cs_get, cs_call or "
            + "run_csharp: GDScript cannot reach a member Godot does not marshal."
    )]
    public async Task<string> RunScriptAsync(
        [Description("The GDScript source.")] string script,
        [Description("How long to wait for execute to return, in milliseconds, load-adjusted.")] int timeoutMs = 30000,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new McpException(
                "script is empty. Pass GDScript that extends RefCounted and defines func execute(scene_tree: SceneTree) -> Variant."
            );
        }

        if (timeoutMs < 1)
        {
            throw new McpException($"timeoutMs must be at least 1; got {timeoutMs}.");
        }

        GodotSession target = Find(session);
        long mark = target.Errors.Mark();
        JsonNode? reply = await RunScriptOnBridgeAsync(target, script, TimeSpan.FromMilliseconds(timeoutMs), mark, cancellationToken);
        IReadOnlyList<ErrorEntry> errors = target.Errors.ErrorsSince(mark);
        JsonNode? value = reply?["value"];
        if (value is null && errors.Any(error => error.IsInSourceScript))
        {
            // GDScript has no exceptions: a runtime error ends execute with null, and the error itself arrives through the feed.
            // An error located elsewhere (a game script execute called, another thread) does not say execute failed.
            bool isCSharpProject = PrepScan.FindCsproj(target.ProjectDir).Kind != CsprojKind.None;
            string? hint = ErrorReport.CSharpHint(errors, isCSharpProject);
            string suffix = hint is null ? string.Empty : $"\n{hint}";
            throw new McpException(
                $"run_script failed: execute returned null and Godot reported errors while it ran:\n{ErrorReport.Summarise(errors)}{suffix}"
            );
        }

        return ErrorReport.AddTo(ShapeScriptValue(value), errors).ToJsonString();
    }

    /// <summary>
    /// <c>{value}</c>, or <c>{valuePreview, valueLength}</c> when the value's JSON is longer than <see cref="MaxValueLength"/>
    /// characters: its first <see cref="MaxValueLength"/> characters and its whole length.
    /// </summary>
    internal static JsonObject ShapeScriptValue(JsonNode? value) =>
        ValuePreview(value, MaxValueLength) ?? new JsonObject { ["value"] = value?.DeepClone() };

    /// <summary>
    /// <c>{valuePreview, valueLength}</c> when the value's JSON is longer than <paramref name="maxLength"/> characters: its first
    /// <paramref name="maxLength"/> characters and its whole length; null when it fits.
    /// </summary>
    internal static JsonObject? ValuePreview(JsonNode? value, int maxLength)
    {
        string json = value?.ToJsonString() ?? "null";
        return json.Length <= maxLength ? null : new JsonObject { ["valuePreview"] = json[..maxLength], ["valueLength"] = json.Length };
    }

    [McpServerTool(Name = "get_errors", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "The errors and warnings a session's game has logged (engine errors, script errors, push_error, push_warning), "
            + "oldest first: {errors: [{seq, type, message, file, line, function, stack}], next, dropped}. seq numbers them "
            + "from 1 for the session. Pass since = the next of the previous call to read only what came after it; 0 reads "
            + "from the oldest kept. The server keeps the last 500; dropped counts those lost. Other tools' results already "
            + "carry the errors (not warnings) raised while they ran."
    )]
    public string GetErrors(
        [Description("Return entries with a seq greater than this: 0, or the next of an earlier call.")] long since = 0,
        [Description("How many entries to return at most, 1 to 500.")] int limit = 50,
        [Description(ProjectTools.SessionDescription)] string? session = null
    )
    {
        if (since < 0)
        {
            throw new McpException($"since must be 0 or more; got {since}.");
        }

        if (limit is < 1 or > MaxErrorsLimit)
        {
            throw new McpException($"limit must be 1 to {MaxErrorsLimit}; got {limit}.");
        }

        ErrorFeed feed = Find(session).Errors;
        IReadOnlyList<ErrorEntry> entries = feed.Since(since, limit);
        JsonObject result = new()
        {
            ["errors"] = new JsonArray([.. entries.Select(entry => ErrorReport.Describe(entry, withType: true))]),
            ["next"] = entries.Count > 0 ? entries[^1].Seq : since,
            ["dropped"] = feed.Dropped,
        };
        return result.ToJsonString();
    }

    private static async Task<JsonNode?> RunScriptOnBridgeAsync(
        GodotSession target,
        string script,
        TimeSpan timeout,
        long mark,
        CancellationToken cancellationToken
    )
    {
        try
        {
            BridgeCall call = new("run_script", "run_script", new JsonObject { ["source"] = script }, timeout);
            return await CallBridgeAsync(target, call, cancellationToken);
        }
        catch (McpException e) when (e.InnerException is InvalidOperationException refused)
        {
            IReadOnlyList<ErrorEntry> errors = target.Errors.ErrorsSince(mark);
            string detail =
                errors.Count == 0
                    ? "\nGodot reported no error for it; check get_errors and get_debug_output."
                    : $"\nGodot reported:\n{ErrorReport.Summarise(errors)}";
            throw new McpException(e.Message + detail, refused);
        }
    }

    /// <summary>Sends one request and collects the errors the game raised from just before it until its reply.</summary>
    internal static async Task<BridgeResult> CallWithErrorsAsync(GodotSession target, BridgeCall call, CancellationToken cancellationToken)
    {
        long mark = target.Errors.Mark();
        JsonNode? reply = await CallBridgeAsync(target, call, cancellationToken);
        return new BridgeResult(reply, target.Errors.ErrorsSince(mark));
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

    private static async Task<JsonNode?> CallBridgeAsync(GodotSession target, BridgeCall call, CancellationToken cancellationToken)
    {
        (string tool, string command, JsonObject parameters, TimeSpan timeout, TimeSpan? release) = call;
        return await SendMappedAsync(
            target,
            tool,
            timeout,
            () => target.SendAsync(command, parameters, timeout, cancellationToken, release),
            cancellationToken
        );
    }

    /// <summary>
    /// Awaits one send and maps what it throws the way every runtime tool reports it: a session error as itself, a timeout as
    /// the hang probe's report with the tool's hint, a refusal or a helper error as <c>&lt;tool&gt; failed: …</c>, and a broken
    /// connection as a crash notice. The helper's own sends go through here too, so a C# tool fails like a bridge tool.
    /// </summary>
    private static async Task<T> SendMappedAsync<T>(
        GodotSession target,
        string tool,
        TimeSpan timeout,
        Func<Task<T>> send,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await send();
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
        catch (TimeoutException e)
        {
            throw await DescribeTimeoutAsync(target, tool, timeout, e, cancellationToken);
        }
        catch (InvalidOperationException e)
        {
            throw new McpException($"{tool} failed: {e.Message}", e);
        }
        catch (IOException e)
        {
            throw new McpException(
                $"The connection to the game ended during {tool}: {e.Message} Check get_debug_output; the game may have crashed.",
                e
            );
        }
    }

    /// <summary>Probes a game whose reply timed out, logs what the probe found, and says whether its main thread is running or stuck.</summary>
    private static async Task<McpException> DescribeTimeoutAsync(
        GodotSession target,
        string tool,
        TimeSpan timeout,
        TimeoutException timedOut,
        CancellationToken cancellationToken
    )
    {
        HangReport report = await HangProbe.RunAsync(target, cancellationToken);
        LoadDeadline? deadline = (timedOut as LoadTimeoutException)?.Deadline;
        string load =
            $"{LoadDeadline.Seconds(deadline?.Wall ?? timeout)} s of wall time, the machine free "
            + $"{LoadDeadline.Percent(deadline?.MeanFree ?? 1)}% on average";
        Log.RequestTimedOut(target.Logger, tool, target.Name, load, report.Outcome, report.ProcessState);
        string hint = tool switch
        {
            "run_script" => "; a script that needs longer can raise timeoutMs",
            "call_method" => "; a method that needs longer can raise options.timeoutMs",
            CsCallToolName or RunCSharpToolName =>
                "; a Task that needs longer can raise options.timeoutMs, the first C# call loads the helper into the game, "
                    + "and restart_project clears a stuck one",
            CsMembersToolName or CsGetToolName or CsSetToolName =>
                "; the first C# call loads the helper into the game, and restart_project clears a stuck one",
            "frame_control" => "; a step waits for drawn frames, so a minimized window stalls it",
            _ => string.Empty,
        };
        return new McpException(report.Describe(tool, timeout, hint, deadline), timedOut);
    }

    /// <exception cref="McpException">The mode is not path_only, preview or full.</exception>
    internal static ScreenshotMode ParseMode(string responseMode) =>
        responseMode switch
        {
            "path_only" => ScreenshotMode.PathOnly,
            "preview" => ScreenshotMode.Preview,
            "full" => ScreenshotMode.Full,
            _ => throw new McpException($"responseMode '{responseMode}' is not one of path_only, preview, full."),
        };

    /// <summary>The bridge's screenshot parameters: the crop when given, and previewMaxWidth for a preview.</summary>
    /// <exception cref="McpException">The crop is empty, or a preview's previewMaxWidth is under 1.</exception>
    internal static JsonObject BuildScreenshotParameters(ScreenshotMode mode, ScreenshotCrop? crop, int previewMaxWidth)
    {
        JsonObject parameters = [];
        if (crop is not null)
        {
            if (crop.Width < 1 || crop.Height < 1)
            {
                throw new McpException($"A crop needs a width and a height of at least 1 pixel; got {crop.Width}x{crop.Height}.");
            }

            parameters["crop"] = new JsonObject
            {
                ["x"] = crop.X,
                ["y"] = crop.Y,
                ["width"] = crop.Width,
                ["height"] = crop.Height,
            };
        }

        if (mode == ScreenshotMode.Preview)
        {
            parameters["previewMaxWidth"] =
                previewMaxWidth >= 1 ? previewMaxWidth : throw new McpException($"previewMaxWidth must be at least 1; got {previewMaxWidth}.");
        }

        return parameters;
    }

    private static ScreenshotFiles ReadScreenshotFiles(JsonNode? reply)
    {
        string? path =
            (reply is JsonObject result ? HandshakeExpectation.ReadString(result, "path") : null)
            ?? throw new McpException($"The bridge's screenshot reply names no file: {reply?.ToJsonString() ?? "null"}. Check get_debug_output.");
        string? previewPath = HandshakeExpectation.ReadString(reply!.AsObject(), "previewPath");
        return new ScreenshotFiles(
            Path.GetFullPath(path),
            ReadInt(reply["width"]) ?? 0,
            ReadInt(reply["height"]) ?? 0,
            previewPath is null ? null : Path.GetFullPath(previewPath),
            ReadInt(reply["previewWidth"]),
            ReadInt(reply["previewHeight"])
        );
    }

    private static async Task<byte[]> ReadImageAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"The screenshot was saved, but reading {path} failed: {e.Message}", e);
        }
    }

    // GDScript's JSON reads and writes every number it parsed as a float, so an integer may arrive as 640.0.
    private static int? ReadInt(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double number) ? (int)number : null;

    /// <summary>
    /// One request to the bridge: the tool it serves (for messages), the bridge command, its parameters, its reply timeout and,
    /// for a request the bridge can end early, its release (both load-adjusted, as <see cref="BridgeConnection.SendAsync"/>
    /// takes them).
    /// </summary>
    internal sealed record BridgeCall(string Tool, string Command, JsonObject Parameters, TimeSpan Timeout, TimeSpan? Release = null);

    /// <summary>A bridge reply's result and the errors the game raised while it was on its way.</summary>
    internal sealed record BridgeResult(JsonNode? Reply, IReadOnlyList<ErrorEntry> Errors);
}
