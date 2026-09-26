using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
internal sealed partial class RuntimeTools(SessionRegistry sessions)
{
    internal const int MaxErrorsLimit = ErrorFeed.Capacity;
    internal const int MaxPageSize = 500;
    internal const int MaxValueLength = 20000;
    private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UiElementsTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "take_screenshot")]
    [Description(
        "Captures the running game's next drawn frame and saves it as a PNG under the project's .godot/godot-mcp/screenshots/ "
            + "(which git ignores). Returns the file's absolute path and size, plus an image unless responseMode is path_only: by "
            + "default a preview at most 480 px wide, saved beside the full-size PNG, whose path is always returned."
    )]
    public async Task<IEnumerable<ContentBlock>> TakeScreenshotAsync(
        [Description(
            "path_only: the path and size only; preview: also the image, scaled down to previewMaxWidth when wider "
                + "(the scaled copy is saved beside the full one); full: also the full-resolution image."
        )]
            string responseMode = "preview",
        [Description(
            "A rectangle to keep, in the screenshot's pixels from its top-left corner (the viewport's pixels when the project does not "
                + "stretch); any part outside the screenshot is dropped."
        )]
            ScreenshotCrop? crop = null,
        [Description("The widest the preview image may be, in pixels; 480 by default.")] int previewMaxWidth = 480,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        ScreenshotMode mode = ParseMode(responseMode);
        JsonObject parameters = BuildScreenshotParameters(mode, crop, previewMaxWidth);
        BridgeCall call = new("take_screenshot", "screenshot", parameters, ScreenshotTimeout);
        BridgeResult result = await CallWithErrorsAsync(Find(session), call, cancellationToken);
        ScreenshotFiles files = ReadScreenshotFiles(result.Reply);
        JsonObject text = JsonSerializer.SerializeToNode(files, Json)!.AsObject();
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

    [McpServerTool(Name = "get_ui_elements")]
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
    internal static JsonObject PageElements(JsonNode? reply, int offset, int limit)
    {
        JsonArray all = reply?["elements"] as JsonArray ?? [];
        JsonArray page = [.. all.Skip(offset).Take(limit).Select(element => element?.DeepClone())];
        JsonObject result = new()
        {
            ["elements"] = page,
            ["total"] = all.Count,
            ["offset"] = offset,
        };
        if (offset + page.Count < all.Count)
        {
            result["next"] = offset + page.Count;
        }

        return result;
    }

    [McpServerTool(Name = "run_script")]
    [Description(
        "Runs GDScript inside the running game. The script must `extends RefCounted` and define "
            + "`func execute(scene_tree: SceneTree) -> Variant`, which may await. Returns {value}, execute's value as JSON: "
            + "Vector2/3 as {x, y[, z]}, Color as {r, g, b, a}, Rect2 as {x, y, width, height}, a Node as its path, another "
            + "Object as {class, string}, Dictionaries and Arrays recursively. A value whose JSON is longer than 20000 characters "
            + "comes back as {valuePreview, valueLength}: its first 20000 characters and its length. Errors raised while it ran "
            + "come back in errors, each with its file, line and stack. The call fails, with those errors, on a compile error, "
            + "or when execute returns null and an error is located in the script itself (its file, or its most recent frame, is "
            + "gdscript://…): a runtime error ends execute with null. A null value with errors located elsewhere (a game "
            + "script execute called, another thread) still succeeds."
    )]
    public async Task<string> RunScriptAsync(
        [Description("The GDScript source.")] string script,
        [Description("How long to wait for execute to return, in milliseconds.")] int timeoutMs = 30000,
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
            throw new McpException(
                $"run_script failed: execute returned null and Godot reported errors while it ran:\n{ErrorReport.Summarise(errors)}"
            );
        }

        return ErrorReport.AddTo(ShapeScriptValue(value), errors).ToJsonString();
    }

    /// <summary>
    /// <c>{value}</c>, or <c>{valuePreview, valueLength}</c> when the value's JSON is longer than <see cref="MaxValueLength"/>
    /// characters: its first <see cref="MaxValueLength"/> characters and its whole length.
    /// </summary>
    internal static JsonObject ShapeScriptValue(JsonNode? value)
    {
        string json = value?.ToJsonString() ?? "null";
        return json.Length <= MaxValueLength
            ? new JsonObject { ["value"] = value?.DeepClone() }
            : new JsonObject { ["valuePreview"] = json[..MaxValueLength], ["valueLength"] = json.Length };
    }

    [McpServerTool(Name = "get_errors", ReadOnly = true)]
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
    private static async Task<BridgeResult> CallWithErrorsAsync(GodotSession target, BridgeCall call, CancellationToken cancellationToken)
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
        (string tool, string command, JsonObject parameters, TimeSpan timeout) = call;
        try
        {
            return await target.SendAsync(command, parameters, timeout, cancellationToken);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
        catch (TimeoutException e)
        {
            string hint = tool == "run_script" ? "; a script that needs longer can raise timeoutMs" : string.Empty;
            throw new McpException(
                $"The game of session '{target.Name}' did not answer {tool} within {timeout.TotalSeconds:0.###} s: it may be "
                    + $"paused, busy or hung{hint}. Check get_debug_output, or stop_project session '{target.Name}' and run it again.",
                e
            );
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

    private static ScreenshotMode ParseMode(string responseMode) =>
        responseMode switch
        {
            "path_only" => ScreenshotMode.PathOnly,
            "preview" => ScreenshotMode.Preview,
            "full" => ScreenshotMode.Full,
            _ => throw new McpException($"responseMode '{responseMode}' is not one of path_only, preview, full."),
        };

    private static JsonObject BuildScreenshotParameters(ScreenshotMode mode, ScreenshotCrop? crop, int previewMaxWidth)
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

    /// <summary>One request to the bridge: the tool it serves (for messages), the bridge command, its parameters and its timeout.</summary>
    private sealed record BridgeCall(string Tool, string Command, JsonObject Parameters, TimeSpan Timeout);

    /// <summary>A bridge reply's result and the errors the game raised while it was on its way.</summary>
    private sealed record BridgeResult(JsonNode? Reply, IReadOnlyList<ErrorEntry> Errors);
}
