using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>Reading the running game through its bridge: screenshots, the Control tree, and scripts run inside it.</summary>
[McpServerToolType]
internal sealed partial class RuntimeTools(GodotSession session)
{
    private const int MaxScriptErrorLines = 20;
    private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UiElementsTimeout = TimeSpan.FromSeconds(10);

    // Godot prints a script's errors before the bridge replies, but stderr is read from a pipe on another thread than
    // the socket, so the lines may land a moment after the reply.
    private static readonly TimeSpan StderrSettle = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StderrWait = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "take_screenshot")]
    [Description(
        "Captures the running game's next drawn frame and saves it as a PNG under the project's .godot/godot-mcp/screenshots/ "
            + "(which git ignores). Returns the file's absolute path and size, plus the image itself unless responseMode is path_only."
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
        [Description("The widest the preview image may be, in pixels.")] int previewMaxWidth = 960,
        CancellationToken cancellationToken = default
    )
    {
        ScreenshotMode mode = ParseMode(responseMode);
        JsonObject parameters = BuildScreenshotParameters(mode, crop, previewMaxWidth);
        JsonNode? reply = await CallBridgeAsync("take_screenshot", "screenshot", parameters, ScreenshotTimeout, cancellationToken);
        ScreenshotFiles files = ReadScreenshotFiles(reply);
        List<ContentBlock> blocks = [new TextContentBlock { Text = JsonSerializer.Serialize(files, Json) }];
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
            + "(buttons) and tooltip."
    )]
    public async Task<string> GetUiElementsAsync(
        [Description("Skip every Control that is not visible in the tree, and everything under it.")] bool visibleOnly = true,
        [Description("Only Controls of this engine class or a subclass of it, e.g. BaseButton.")] string? filter = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject parameters = new() { ["visibleOnly"] = visibleOnly, ["classFilter"] = filter ?? string.Empty };
        JsonNode? reply = await CallBridgeAsync("get_ui_elements", "ui_elements", parameters, UiElementsTimeout, cancellationToken);
        return reply?.ToJsonString() ?? "{\"elements\":[]}";
    }

    [McpServerTool(Name = "run_script")]
    [Description(
        "Runs GDScript inside the running game. The script must `extends RefCounted` and define "
            + "`func execute(scene_tree: SceneTree) -> Variant`, which may await. Returns execute's value as JSON: Vector2/3 as "
            + "{x, y[, z]}, Color as {r, g, b, a}, Rect2 as {x, y, width, height}, a Node as its path, another Object as "
            + "{class, string}, Dictionaries and Arrays recursively. A compile error, or a runtime error that ends execute, fails "
            + "the call with Godot's SCRIPT ERROR lines."
    )]
    public async Task<string> RunScriptAsync(
        [Description("The GDScript source.")] string script,
        [Description("How long to wait for execute to return, in milliseconds.")] int timeoutMs = 30000,
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

        long mark = session.MarkStderr();
        JsonNode? reply = await RunScriptOnBridgeAsync(script, TimeSpan.FromMilliseconds(timeoutMs), mark, cancellationToken);
        JsonNode? value = reply?["value"];
        if (value is null)
        {
            // GDScript has no exceptions: a runtime error ends execute with null, and the error itself goes to stderr.
            IReadOnlyList<string> errors = await CollectScriptErrorsAsync(mark, cancellationToken);
            if (errors.Count > 0)
            {
                throw new McpException(
                    $"run_script failed: execute returned null and Godot reported errors while it ran:\n{string.Join('\n', errors)}"
                );
            }
        }

        return value?.ToJsonString() ?? "null";
    }

    /// <summary>The lines of <paramref name="lines"/> that report a script error, each with the <c>at:</c> line under it.</summary>
    internal static List<string> FindScriptErrors(IReadOnlyList<string> lines)
    {
        List<string> errors = [];
        bool previousKept = false;
        foreach (string line in lines)
        {
            bool keep = ScriptErrorLine().IsMatch(line) || (previousKept && LocationLine().IsMatch(line));
            if (keep && errors.Count < MaxScriptErrorLines)
            {
                errors.Add(line);
            }

            previousKept = keep;
        }

        return errors;
    }

    private async Task<JsonNode?> RunScriptOnBridgeAsync(string script, TimeSpan timeout, long mark, CancellationToken cancellationToken)
    {
        try
        {
            return await CallBridgeAsync("run_script", "run_script", new JsonObject { ["source"] = script }, timeout, cancellationToken);
        }
        catch (McpException e) when (e.InnerException is InvalidOperationException refused)
        {
            IReadOnlyList<string> errors = await CollectScriptErrorsAsync(mark, cancellationToken);
            string detail =
                errors.Count == 0
                    ? "\nGodot printed no SCRIPT ERROR line for it; check get_debug_output."
                    : $"\nGodot reported:\n{string.Join('\n', errors)}";
            throw new McpException(e.Message + detail, refused);
        }
    }

    private async Task<IReadOnlyList<string>> CollectScriptErrorsAsync(long mark, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + StderrWait;
        await Task.Delay(StderrSettle, cancellationToken);
        List<string> errors = FindScriptErrors(session.GetStderrSince(mark));
        while (errors.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, cancellationToken);
            errors = FindScriptErrors(session.GetStderrSince(mark));
        }

        return errors;
    }

    private async Task<JsonNode?> CallBridgeAsync(
        string tool,
        string command,
        JsonObject parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await session.SendAsync(command, parameters, timeout, cancellationToken);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
        catch (TimeoutException e)
        {
            string hint = tool == "run_script" ? "; a script that needs longer can raise timeoutMs" : string.Empty;
            throw new McpException(
                $"The game did not answer {tool} within {timeout.TotalSeconds:0.###} s: it may be paused, busy or hung{hint}. "
                    + "Check get_debug_output, or stop_project and run it again.",
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

    [GeneratedRegex("SCRIPT ERROR|Parse Error")]
    private static partial Regex ScriptErrorLine();

    [GeneratedRegex(@"^\s+at: ")]
    private static partial Regex LocationLine();
}
