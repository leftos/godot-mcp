using System.Text.Json.Nodes;

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

    private static bool IsGreen(JsonNode? step) =>
        step is JsonObject entry && entry["ok"] is JsonValue ok && ok.TryGetValue(out bool passed) && passed;
}
