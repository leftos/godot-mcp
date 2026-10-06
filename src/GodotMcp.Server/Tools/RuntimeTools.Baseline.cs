using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// Screenshot baselines: a stored PNG of the running game (ScreenshotBaseline.cs owns the folder and its files) and a
/// comparison of the game's next drawn frame with it, counted in the bridge (bridge/godot_mcp_baseline.gd).
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const int MaxTolerance = 255;
    private static readonly TimeSpan CompareTimeout = TimeSpan.FromSeconds(20);

    [McpServerTool(Name = "save_screenshot_baseline", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Captures the running game's next drawn frame and stores it as the baseline named name, under the project's "
            + ".godot/godot-mcp/baselines/ (which git ignores) as <name>.png beside <name>.json, which records the crop, the "
            + "size and when it was saved. compare_screenshot checks the game against it later, in this run or another. An "
            + "existing name is refused unless options.overwrite is true. Returns {name, baselinePath, width, height, crop}. "
            + "Pause an animating game with frame_control before saving and before comparing, or compare_screenshot counts "
            + "the animation as changed pixels."
    )]
    public async Task<string> SaveScreenshotBaselineAsync(
        [Description("The baseline's name: 1-64 letters, digits, '.', '_' or '-', starting with a letter or digit.")] string name,
        [Description(
            "A rectangle to keep, in viewport coordinates, as take_screenshot takes it; " + "compare_screenshot then compares the same rectangle."
        )]
            ScreenshotCrop? crop = null,
        [Description("{overwrite}: replace a baseline of the same name.")] BaselineOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        ScreenshotBaseline.CheckName(name);
        JsonObject parameters = BuildScreenshotParameters(ScreenshotMode.PathOnly, crop, 0);
        bool overwrite = options?.Overwrite ?? false;
        GodotSession target = Find(session);
        string folder = ScreenshotBaseline.Folder(target.ProjectDir);
        string baselinePath = ScreenshotBaseline.CheckCanSave(folder, name, overwrite);
        BridgeCall call = new("save_screenshot_baseline", "screenshot", parameters, ScreenshotTimeout);
        BridgeResult result = await CallWithErrorsAsync(target, call, cancellationToken);
        ScreenshotFiles files = ReadScreenshotFiles(result.Reply);
        ScreenshotBaseline.Write(folder, name, new BaselineCapture(files.Path, files.Width, files.Height, crop), overwrite);
        JsonObject text = new()
        {
            ["name"] = name,
            ["baselinePath"] = baselinePath,
            ["width"] = files.Width,
            ["height"] = files.Height,
            ["crop"] = ScreenshotBaseline.ToJson(crop),
        };
        return ErrorReport.AddTo(text, result.Errors).ToJsonString();
    }

    [McpServerTool(Name = "compare_screenshot", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Captures the running game's next drawn frame, cropped as the baseline was, saves it as take_screenshot does, and "
            + "counts the pixels that differ from the baseline saved by save_screenshot_baseline: a pixel differs when one of "
            + "its channels, alpha included, differs by more than tolerance levels (0-255). Returns {name, baselinePath, path, "
            + "width, height, tolerance, changedPixels, totalPixels, changedRatio, bbox, match}, bbox {x, y, width, height} "
            + "around the changed pixels (null when none) and match changedRatio <= options.maxChangedRatio; when pixels "
            + "changed, also diffPath (a dimmed grey copy with the changed pixels red), diffPreviewPath when scaled down, and "
            + "the diff image unless responseMode is path_only. A mismatch is a result, not an error; a missing baseline or "
            + "a screenshot of another size fails. Pause an animating game with frame_control before saving and before "
            + "comparing, or the animation counts as changed pixels."
    )]
    public async Task<IEnumerable<ContentBlock>> CompareScreenshotAsync(
        [Description("The baseline's name, as save_screenshot_baseline saved it.")] string name,
        [Description("The crop the baseline was saved with; may be left out, and any other crop is refused.")] ScreenshotCrop? crop = null,
        [Description("How many levels (0-255) a channel may differ by and still count as unchanged; 2 by default.")] int tolerance = 2,
        [Description("{maxChangedRatio, responseMode, previewMaxWidth}.")] CompareOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        ScreenshotBaseline.CheckName(name);
        CompareOptions settings = options ?? new CompareOptions();
        ScreenshotMode mode = CheckCompareArguments(crop, tolerance, settings);
        GodotSession target = Find(session);
        string folder = ScreenshotBaseline.Folder(target.ProjectDir);
        string baselinePath = ScreenshotBaseline.RequireBaseline(folder, name);
        ScreenshotCrop? stored = ScreenshotBaseline.ResolveCrop(name, ScreenshotBaseline.ReadCrop(folder, name), crop);
        JsonObject parameters = new()
        {
            ["baselinePath"] = baselinePath,
            ["crop"] = ScreenshotBaseline.ToJson(stored),
            ["tolerance"] = tolerance,
            ["previewMaxWidth"] = mode == ScreenshotMode.Preview ? settings.PreviewMaxWidth : 0,
        };
        BridgeCall call = new("compare_screenshot", "compare_screenshot", parameters, CompareTimeout);
        BridgeResult result = await CallWithErrorsAsync(target, call, cancellationToken);
        JsonObject text = ShapeComparison(name, baselinePath, tolerance, settings.MaxChangedRatio, result.Reply);
        List<ContentBlock> blocks = [new TextContentBlock { Text = ErrorReport.AddTo(text, result.Errors).ToJsonString() }];
        string? imagePath = DiffImagePath(text, mode);
        if (imagePath is not null)
        {
            blocks.Add(ImageContentBlock.FromBytes(await ReadImageAsync(imagePath, cancellationToken), "image/png"));
        }

        return blocks;
    }

    /// <summary>
    /// The bridge's comparison reply as compare_screenshot's result: {name, baselinePath, path, width, height, tolerance,
    /// changedPixels, totalPixels, changedRatio, bbox, match[, diffPath, diffPreviewPath]}, with native paths.
    /// </summary>
    internal static JsonObject ShapeComparison(string name, string baselinePath, int tolerance, double maxChangedRatio, JsonNode? reply)
    {
        JsonObject found = CheckComparisonReply(reply);
        ScreenshotFiles capture = ReadScreenshotFiles(found);
        int changed = ReadInt(found["changedPixels"]) ?? 0;
        int total = ReadInt(found["totalPixels"]) ?? 0;
        double ratio = (double)changed / total;
        JsonObject result = new()
        {
            ["name"] = name,
            ["baselinePath"] = baselinePath,
            ["path"] = capture.Path,
            ["width"] = capture.Width,
            ["height"] = capture.Height,
            ["tolerance"] = tolerance,
            ["changedPixels"] = changed,
            ["totalPixels"] = total,
            ["changedRatio"] = ratio,
            ["bbox"] = ReadBox(found["bbox"]),
            ["match"] = ratio <= maxChangedRatio,
        };
        AddNativePath(result, found, "diffPath");
        AddNativePath(result, found, "diffPreviewPath");
        return result;
    }

    /// <summary>The bridge's comparison reply, once it names its capture and counts its pixels.</summary>
    /// <exception cref="McpException">The reply names no file, or lacks changedPixels or a totalPixels above 0.</exception>
    private static JsonObject CheckComparisonReply(JsonNode? reply)
    {
        string json = reply?.ToJsonString() ?? "null";
        if (reply is not JsonObject found || found["path"] is not JsonValue path || !path.TryGetValue(out string? _))
        {
            throw new McpException($"The bridge's compare_screenshot reply names no file: {json}. Check get_debug_output.");
        }

        // Without both counts the ratio would read as 0 and the call as a match.
        if (ReadInt(found["changedPixels"]) is null || ReadInt(found["totalPixels"]) is not > 0)
        {
            throw new McpException(
                $"The bridge's compare_screenshot reply has no pixel counts (changedPixels, and totalPixels above 0): {json}. "
                    + "Check get_debug_output."
            );
        }

        return found;
    }

    /// <summary>Checks compare_screenshot's arguments other than its name and returns its response mode.</summary>
    private static ScreenshotMode CheckCompareArguments(ScreenshotCrop? crop, int tolerance, CompareOptions options)
    {
        if (tolerance is < 0 or > MaxTolerance)
        {
            throw new McpException($"tolerance takes values from 0 to {MaxTolerance}; got {tolerance}.");
        }

        if (options.MaxChangedRatio is not (>= 0 and <= 1))
        {
            throw new McpException(
                $"maxChangedRatio takes values from 0 to 1; got {options.MaxChangedRatio.ToString(CultureInfo.InvariantCulture)}."
            );
        }

        ScreenshotMode mode = ParseMode(options.ResponseMode);
        _ = BuildScreenshotParameters(mode, crop, options.PreviewMaxWidth);
        return mode;
    }

    /// <summary>The image compare_screenshot returns: none when nothing changed or for path_only, else the diff or its preview.</summary>
    internal static string? DiffImagePath(JsonObject result, ScreenshotMode mode)
    {
        string? diffPath = result["diffPath"]?.GetValue<string>();
        bool changed = result["changedPixels"]?.GetValue<int>() > 0;
        return mode switch
        {
            _ when diffPath is null || !changed => null,
            ScreenshotMode.Preview => result["diffPreviewPath"]?.GetValue<string>() ?? diffPath,
            ScreenshotMode.Full => diffPath,
            _ => null,
        };
    }

    private static JsonObject? ReadBox(JsonNode? box) =>
        box is JsonObject found
            ? new JsonObject
            {
                ["x"] = ReadInt(found["x"]) ?? 0,
                ["y"] = ReadInt(found["y"]) ?? 0,
                ["width"] = ReadInt(found["width"]) ?? 0,
                ["height"] = ReadInt(found["height"]) ?? 0,
            }
            : null;

    private static void AddNativePath(JsonObject result, JsonNode reply, string key)
    {
        if (reply[key] is JsonValue value && value.TryGetValue(out string? path))
        {
            result[key] = Path.GetFullPath(path);
        }
    }
}
