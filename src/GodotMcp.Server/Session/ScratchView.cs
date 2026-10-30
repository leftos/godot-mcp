using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// What a built scratch result shows when read with only: "red": every scene keeps its line, its details keep only its red steps
/// (ok false) with their readback, and a scene with no red step lists none. Applied as the result is read, never to the run's own
/// result or its status file, so a later read with only: "all" still sees every step; a field the result lacks, as in an older
/// or partial status file, stays missing.
/// </summary>
internal static class ScratchView
{
    internal const string All = "all";

    internal const string Red = "red";

    /// <summary>The most characters a returned result or status takes, serialised; past it, scenes' steps are left out.</summary>
    internal const int MaxResultChars = 20000;

    /// <summary>How many spill files of foreground results a project's log folder keeps, the newest by name.</summary>
    internal const int SpillFilesKept = 20;

    /// <summary>How many more names a spill file tries when the one it drew is taken.</summary>
    internal const int SpillNameRetries = 3;

    private const string SpillPrefix = "scratch-result-";

    /// <summary>run_scratches' result as JSON, pruned to its red steps when redOnly; otherwise the same object.</summary>
    public static JsonObject Result(JsonObject result, bool redOnly)
    {
        if (!redOnly)
        {
            return result;
        }

        JsonObject pruned = result.DeepClone().AsObject();
        PruneScenes(pruned);
        return pruned;
    }

    /// <summary>
    /// A background run's status, its result pruned to the red steps when redOnly; otherwise, or when it has no result yet (a
    /// running run, whose scenes are progress entries), the same object.
    /// </summary>
    public static JsonObject Status(JsonObject status, bool redOnly)
    {
        if (!redOnly || status["result"] is not JsonObject)
        {
            return status;
        }

        JsonObject pruned = status.DeepClone().AsObject();
        PruneScenes(pruned["result"]!.AsObject());
        return pruned;
    }

    private static void PruneScenes(JsonObject result)
    {
        if (result["scenes"] is not JsonArray scenes)
        {
            return;
        }

        foreach (JsonObject scene in scenes.OfType<JsonObject>())
        {
            PruneSteps(scene);
        }
    }

    /// <summary>Drops the scene's green steps (ok true) from its details, and details itself when no step is left.</summary>
    private static void PruneSteps(JsonObject scene)
    {
        if (scene["details"] is not JsonArray steps)
        {
            return;
        }

        for (int index = steps.Count - 1; index >= 0; index--)
        {
            if (IsGreen(steps[index]))
            {
                steps.RemoveAt(index);
            }
        }

        if (steps.Count == 0)
        {
            scene.Remove("details");
        }
    }

    /// <summary>Whether a listed step is green: ok true, or a compact step's line, which only a green step is listed as.</summary>
    private static bool IsGreen(JsonNode? step) =>
        step?.GetValueKind() == JsonValueKind.String
        || (step is JsonObject entry && entry["ok"] is JsonValue ok && ok.TryGetValue(out bool passed) && passed);

    /// <summary>
    /// run_scratches' result as returned: <paramref name="shown"/> unchanged at or under <see cref="MaxResultChars"/>; past it,
    /// <paramref name="whole"/> (the result before only pruned it) written to a new spill file in the project's log folder (the
    /// newest <see cref="SpillFilesKept"/> kept) and a copy of <paramref name="shown"/> returned with scenes' steps left out
    /// until it fits, naming the file, or saying why it could not be written.
    /// </summary>
    public static JsonObject CappedResult(JsonObject shown, JsonObject whole, ScratchSpillPlace place)
    {
        int length = Length(shown);
        if (length <= MaxResultChars)
        {
            return shown;
        }

        SpillOutcome spill = Spill(whole, place);
        JsonObject capped = shown.DeepClone().AsObject();
        Shrink(capped, capped, length, spill);
        return capped;
    }

    /// <summary>
    /// A background run's status as returned: <paramref name="shown"/> unchanged at or under <see cref="MaxResultChars"/> or
    /// with no result yet; past it, a copy whose result leaves scenes' steps out until it fits, naming the run's status file
    /// when that holds the result, else a spill file of <paramref name="whole"/>'s result written as run_scratches writes one.
    /// </summary>
    public static JsonObject CappedStatus(JsonObject shown, JsonObject whole, string runId, ScratchSpillPlace place)
    {
        int length = Length(shown);
        if (length <= MaxResultChars || shown["result"] is not JsonObject)
        {
            return shown;
        }

        string statusFile = ScratchBackgroundRun.StatusPath(place.ProjectDir, runId);
        SpillOutcome spill = HoldsResult(statusFile)
            ? new SpillOutcome(statusFile, Error: null)
            : Spill((whole["result"] ?? shown["result"])!.AsObject(), place);
        JsonObject capped = shown.DeepClone().AsObject();
        Shrink(capped, capped["result"]!.AsObject(), length, spill);
        return capped;
    }

    /// <summary>Whether the status file can be read and holds a result object; false when it is missing, unreadable or has none.</summary>
    private static bool HoldsResult(string statusFile)
    {
        try
        {
            if (!File.Exists(statusFile))
            {
                return false;
            }

            using FileStream stream = new(statusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream) is JsonObject status && status["result"] is JsonObject;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces scenes' details with detailsOmitted and their watch with watchOmitted, green scenes first and then the rest,
    /// each pass from the last scene to the first, until <paramref name="shown"/> fits with its full and cut fields; every
    /// scene's when it never fits.
    /// </summary>
    private static void Shrink(JsonObject shown, JsonObject result, int length, SpillOutcome spill)
    {
        List<JsonObject> scenes = result["scenes"] is JsonArray array ? [.. array.OfType<JsonObject>().Reverse()] : [];
        IEnumerable<JsonObject> order = scenes.Where(IsGreenScene).Concat(scenes.Where(scene => !IsGreenScene(scene)));
        int shrunk = 0;
        Mark(shown, length, shrunk, spill);
        foreach (JsonObject scene in order.Where(scene => scene["details"] is JsonArray || scene["watch"] is JsonObject))
        {
            if (Length(shown) <= MaxResultChars)
            {
                return;
            }

            LeaveOut(scene);
            Mark(shown, length, ++shrunk, spill);
        }
    }

    /// <summary>Replaces the scene's details with detailsOmitted, their count, and its watch timeline with watchOmitted: true.</summary>
    private static void LeaveOut(JsonObject scene)
    {
        if (scene["details"] is JsonArray details)
        {
            scene["detailsOmitted"] = details.Count;
            scene.Remove("details");
        }

        if (scene.Remove("watch"))
        {
            scene["watchOmitted"] = true;
        }
    }

    /// <summary>Sets full to the file holding the whole result and cut to why; with no full when that file could not be written.</summary>
    private static void Mark(JsonObject shown, int length, int shrunk, SpillOutcome spill)
    {
        string head = string.Create(
            CultureInfo.InvariantCulture,
            $"the result ran to {length} characters, past {MaxResultChars}; {shrunk} scenes' steps are left out here; "
        );
        if (spill.Error is null)
        {
            shown["full"] = spill.Path;
            shown["cut"] = $"{head}the whole result is in {spill.Path}";
        }
        else
        {
            shown["cut"] = $"{head}the whole result could not be written to {spill.Path}: {spill.Error}";
        }
    }

    private static bool IsGreenScene(JsonObject scene) =>
        scene["verdict"] is JsonValue verdict && verdict.TryGetValue(out string? shown) && shown == ScratchVerdict.Green;

    /// <summary>
    /// Writes the whole result to a new scratch-result-&lt;UTC yyyyMMdd-HHmmss&gt;-&lt;4 hex&gt;.json in the log folder, another
    /// suffix tried up to <see cref="SpillNameRetries"/> times when the name is taken, then prunes the older ones; a write that
    /// fails is logged and its error returned, never thrown.
    /// </summary>
    private static SpillOutcome Spill(JsonObject whole, ScratchSpillPlace place)
    {
        string folder = ProjectPrep.LogFolder(place.ProjectDir);
        string stamp = place.Now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string text = whole.ToJsonString(ToolJson.Options);
        string path = SpillPath(folder, stamp);
        try
        {
            Directory.CreateDirectory(folder);
            for (int retry = 0; !TryCreate(path, text); retry++)
            {
                if (retry == SpillNameRetries)
                {
                    throw new IOException($"the file already exists, as did the {SpillNameRetries} names tried before it");
                }

                path = SpillPath(folder, stamp);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.ScratchSpillFailed(place.Logger, e, path);
            return new SpillOutcome(path, e.Message);
        }

        Prune(folder, path, place.Logger);
        return new SpillOutcome(path, Error: null);
    }

    private static string SpillPath(string folder, string stamp) =>
        Path.Combine(folder, $"{SpillPrefix}{stamp}-{RandomNumberGenerator.GetHexString(4, lowercase: true)}.json");

    /// <summary>Writes a new file at the path; false, writing nothing, when a file of that name already exists.</summary>
    /// <exception cref="IOException">The file could not be created or written for another reason.</exception>
    private static bool TryCreate(string path, string text)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }

        using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
        return true;
    }

    /// <summary>
    /// Deletes all but the newest <see cref="SpillFilesKept"/> spill files, by name; one that cannot be deleted is logged and left.
    /// </summary>
    private static void Prune(string folder, string written, ILogger logger)
    {
        List<string> older;
        try
        {
            older =
            [
                .. Directory
                    .GetFiles(folder, SpillPrefix + "*.json")
                    .Where(file => file != written)
                    .OrderDescending(StringComparer.Ordinal)
                    .Skip(SpillFilesKept - 1),
            ];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.ScratchSpillPruneFailed(logger, e, folder);
            return;
        }

        foreach (string file in older)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.ScratchSpillPruneFailed(logger, e, file);
            }
        }
    }

    private static int Length(JsonObject shown) => shown.ToJsonString(ToolJson.Options).Length;

    /// <summary>The file that holds the whole result, or the one that could not be written and why.</summary>
    private sealed record SpillOutcome(string Path, string? Error);
}

/// <summary>Where and when a capped scratch result's spill file is written, and the logger its failures go to.</summary>
internal sealed record ScratchSpillPlace(string ProjectDir, DateTimeOffset Now, ILogger Logger);
