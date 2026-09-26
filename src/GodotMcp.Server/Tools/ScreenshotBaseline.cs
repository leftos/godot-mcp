using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>save_screenshot_baseline's options.</summary>
internal sealed record BaselineOptions(
    [property: Description("Replace a baseline of the same name; without it, saving over an existing name is refused.")] bool Overwrite = false
);

/// <summary>compare_screenshot's options.</summary>
internal sealed record CompareOptions(
    [property: Description("The largest share of changed pixels, 0 to 1, that still counts as a match; 0 by default.")] double MaxChangedRatio = 0,
    [property: Description(
        "path_only: no image; preview (the default): the diff image, scaled down to previewMaxWidth when wider; full: the "
            + "full-size diff image. No image is returned when no pixel changed."
    )]
        string ResponseMode = "preview",
    [property: Description("The widest the diff preview may be, in pixels; 480 by default.")] int PreviewMaxWidth = 480
);

/// <summary>
/// The stored screenshots compare_screenshot checks the game against: <c>&lt;name&gt;.png</c> and its sidecar
/// <c>&lt;name&gt;.json</c> (<c>{crop, width, height, savedAt}</c>) in the project's <c>.godot/godot-mcp/baselines/</c>.
/// </summary>
internal static partial class ScreenshotBaseline
{
    private static readonly string[] DeviceNames =
    [
        "CON",
        "PRN",
        "AUX",
        "NUL",
        .. Enumerable.Range(1, 9).Select(number => $"COM{number}"),
        .. Enumerable.Range(1, 9).Select(number => $"LPT{number}"),
    ];

    /// <summary>The baselines folder of a project.</summary>
    internal static string Folder(string projectDir) => Path.Combine(projectDir, ".godot", "godot-mcp", "baselines");

    /// <exception cref="McpException">The name is not 1-64 of letters, digits, '.', '_', '-' starting with a letter or digit, or is a Windows device name.</exception>
    internal static void CheckName(string? name)
    {
        if (name is null || !NamePattern().IsMatch(name))
        {
            throw new McpException(
                $"baseline name '{name}' is not valid: use 1-64 letters, digits, '.', '_' or '-', starting with a letter or digit."
            );
        }

        // Windows reserves a device name whatever extension follows it: nul.png opens the null device.
        string stem = name.Split('.')[0];
        if (DeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            throw new McpException($"baseline name '{name}' is a reserved Windows device name.");
        }
    }

    /// <summary>The path a new baseline named <paramref name="name"/> is saved to.</summary>
    /// <exception cref="McpException">A baseline of that name exists and <paramref name="overwrite"/> is false.</exception>
    internal static string CheckCanSave(string folder, string name, bool overwrite)
    {
        string path = ImagePath(folder, name);
        if (!overwrite && File.Exists(path))
        {
            throw new McpException($"a baseline named '{name}' already exists at {path}; pass options.overwrite: true to replace it.");
        }

        return path;
    }

    /// <summary>The path of the baseline named <paramref name="name"/>.</summary>
    /// <exception cref="McpException">There is none; the message lists the baselines the folder has.</exception>
    internal static string RequireBaseline(string folder, string name)
    {
        string path = ImagePath(folder, name);
        if (File.Exists(path))
        {
            return path;
        }

        string[] names = Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*.png").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal)]
            : [];
        string listed = names.Length == 0 ? "none" : string.Join(", ", names);
        throw new McpException($"no baseline named '{name}' in {folder}; save one with save_screenshot_baseline. Baselines here: {listed}.");
    }

    /// <summary>The crop a comparison uses: the stored one, which an explicit crop must equal.</summary>
    /// <exception cref="McpException">An explicit crop differs from the stored one.</exception>
    internal static ScreenshotCrop? ResolveCrop(string name, ScreenshotCrop? stored, ScreenshotCrop? given)
    {
        if (given is not null && given != stored)
        {
            throw new McpException($"baseline '{name}' was saved with crop {Describe(stored)}; compare with the same crop or omit it.");
        }

        return stored;
    }

    /// <summary>The crop the baseline's sidecar records; null when it records none or there is no sidecar.</summary>
    /// <exception cref="McpException">The sidecar cannot be read or is not JSON.</exception>
    internal static ScreenshotCrop? ReadCrop(string folder, string name)
    {
        string path = SidecarPath(folder, name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?["crop"] is not JsonObject crop
                ? null
                : new ScreenshotCrop(ReadInt(crop, "x"), ReadInt(crop, "y"), ReadInt(crop, "width"), ReadInt(crop, "height"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            throw new McpException($"reading baseline '{name}''s sidecar {path} failed: {e.Message} Save the baseline again.", e);
        }
    }

    /// <summary>Copies the saved capture to the baseline's PNG and writes its sidecar, creating the folder when missing.</summary>
    /// <exception cref="McpException">The copy or the write failed.</exception>
    internal static void Write(string folder, string name, BaselineCapture capture, bool overwrite)
    {
        string path = ImagePath(folder, name);
        JsonObject sidecar = new()
        {
            ["crop"] = ToJson(capture.Crop),
            ["width"] = capture.Width,
            ["height"] = capture.Height,
            ["savedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        try
        {
            Directory.CreateDirectory(folder);
            // A save that fails after the copy then leaves no sidecar, which reads as no crop, rather than the old one's crop.
            File.Delete(SidecarPath(folder, name));
            File.Copy(capture.Path, path, overwrite);
            File.WriteAllText(SidecarPath(folder, name), sidecar.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"saving baseline '{name}' to {path} failed: {e.Message}", e);
        }
    }

    /// <summary>A crop as <c>{x, y, width, height}</c>, or null.</summary>
    internal static JsonObject? ToJson(ScreenshotCrop? crop) =>
        crop is null
            ? null
            : new JsonObject
            {
                ["x"] = crop.X,
                ["y"] = crop.Y,
                ["width"] = crop.Width,
                ["height"] = crop.Height,
            };

    private static string ImagePath(string folder, string name) => Path.Combine(folder, name + ".png");

    private static string SidecarPath(string folder, string name) => Path.Combine(folder, name + ".json");

    private static string Describe(ScreenshotCrop? crop) =>
        crop is null ? "none" : $"{{x: {crop.X}, y: {crop.Y}, width: {crop.Width}, height: {crop.Height}}}";

    private static int ReadInt(JsonObject crop, string key) =>
        (int)(crop[key]?.GetValue<double>() ?? throw new FormatException($"its crop has no {key}."));

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex NamePattern();
}

/// <summary>A capture the bridge saved for a new baseline: its file, its size and the crop it was taken with.</summary>
internal sealed record BaselineCapture(string Path, int Width, int Height, ScreenshotCrop? Crop);
