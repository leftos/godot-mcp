using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>How batch_drive returns its result: whole, or as a per-step summary with the whole result in a file.</summary>
internal sealed record BatchOptions(
    [property: Description(
        "\"full\" returns every step's whole result however long; \"summary\" always returns the per-step summary and writes the whole "
            + "result to the file full names; left out, the summary comes back only past 20000 characters."
    )]
        string? Result = null
);

/// <summary>
/// batch_drive's reply: the whole result at or under <see cref="ScratchView.MaxResultChars"/> characters, or with
/// options.result "full"; past it, or with "summary", a copy whose passed steps' long results are cut down, naming the spill
/// file that holds the whole result.
/// </summary>
internal static class BatchSummary
{
    internal const string FullMode = "full";

    internal const string SummaryMode = "summary";

    internal const string SpillPrefix = "batch-result-";

    /// <summary>The longest a passed step's result is kept in the summary, serialised; a longer one is replaced.</summary>
    internal const int MaxKeptResultChars = 1000;

    /// <summary>The longest string field a digest keeps once the summary is still over the cap.</summary>
    internal const int MaxKeptStringChars = 200;

    internal const string NoSessionReason = "no session resolved";

    internal const string RepeatedReason = "the failed step's error, above";

    private const string CaptureFrames = "capture_frames";

    /// <summary>The capture_frames fields the digest keeps, each when the result has it.</summary>
    private static readonly string[] CaptureFields =
    [
        "folder",
        "files",
        "width",
        "height",
        "shared",
        "warning",
        "stopped",
        "missed",
        "errors",
        "errorsOmitted",
    ];

    /// <summary>
    /// The result fields the summary never drops: the game's errors, kept by every digest and by a left-out result (a call
    /// that raised is kept beside them, see IsRaisedCall).
    /// </summary>
    private static readonly string[] ErrorFields = ["errors", "errorsOmitted"];

    /// <summary>The string fields of a capture digest that the long-string cut keeps.</summary>
    private static readonly string[] CaptureKeptStrings = ["warning", "folder"];

    /// <summary>Refuses an options.result other than "full" or "summary", before any step runs.</summary>
    /// <exception cref="McpException">options.result is another value.</exception>
    public static void Check(BatchOptions? options)
    {
        if (options?.Result is { } mode && mode is not FullMode and not SummaryMode)
        {
            throw new McpException($"options.result must be \"full\" or \"summary\", not \"{mode}\".");
        }
    }

    /// <summary>
    /// The reply text for <paramref name="result"/> under the mode, writing the spill file when it summarises, and cutting the
    /// summary down further while it is still over the cap.
    /// </summary>
    /// <param name="projectDir">The project the spill file goes in; null when no session resolved, and no file is written.</param>
    public static string Reply(JsonObject result, BatchOptions? options, string? projectDir, ILogger logger)
    {
        string text = result.ToJsonString();
        string? mode = options?.Result;
        if (mode == FullMode || (mode is null && text.Length <= ScratchView.MaxResultChars))
        {
            return text;
        }

        (JsonObject summary, _) = Summarise(result);
        (string? path, string? reason) = SpillOrReason(result, projectDir, logger);
        BatchCut cut = new(mode, text.Length, path, reason);
        Mark(summary, cut, stillOver: false);
        using IEnumerator<Action> shrinks = Shrinks(summary).GetEnumerator();
        while (IsOver(summary) && shrinks.MoveNext())
        {
            shrinks.Current();
            Mark(summary, cut, stillOver: false);
        }

        if (IsOver(summary))
        {
            Mark(summary, cut, stillOver: true);
        }

        return summary.ToJsonString();
    }

    private static bool IsOver(JsonObject summary) => summary.ToJsonString().Length > ScratchView.MaxResultChars;

    /// <summary>Sets full to the spill file when it was written, and cut, counting the entries whose result was left out.</summary>
    private static void Mark(JsonObject summary, BatchCut cut, bool stillOver)
    {
        if (cut.Path is not null)
        {
            summary["full"] = cut.Path;
        }

        int replaced = Entries(summary).Count(entry => entry.ContainsKey("resultOmitted"));
        summary["cut"] = cut.Text(replaced, stillOver);
    }

    /// <summary>The spill file written for the whole result, or why none was.</summary>
    private static (string? Path, string? Reason) SpillOrReason(JsonObject result, string? projectDir, ILogger logger)
    {
        if (projectDir is null)
        {
            return (null, NoSessionReason);
        }

        ScratchView.SpillOutcome spill = ScratchView.Spill(result, new ScratchSpillPlace(projectDir, DateTimeOffset.UtcNow, logger), SpillPrefix);
        return spill.Error is null ? (spill.Path, null) : (null, spill.Error);
    }

    /// <summary>
    /// A copy of <paramref name="result"/> with each step entry and group member shaped, and how many of their results were
    /// replaced: a failed entry is kept whole, a passed one keeps its result when that is short and gets a digest of it otherwise.
    /// </summary>
    public static (JsonObject Summary, int Replaced) Summarise(JsonObject result)
    {
        JsonObject summary = result.DeepClone().AsObject();
        return (summary, Entries(summary).Count(Shape));
    }

    /// <summary>The summary's step entries and, in place of a group's entry, its members, in order.</summary>
    private static List<JsonObject> Entries(JsonObject summary) =>
        [
            .. (summary["steps"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .SelectMany(entry => entry["members"] is JsonArray members ? members.OfType<JsonObject>() : [entry]),
        ];

    /// <summary>
    /// The second pass's cuts, in order, each applied only while the summary is still over the cap: a failedAt.reason that
    /// repeats the failed entry's error is shortened, capture digests' files become filesOmitted, digests' long strings go
    /// (but a capture digest's warning and folder), the short results kept whole go from the last entry back, keeping
    /// their errors and a call that raised, and finally those kept calls are trimmed from the last entry back to their
    /// raised text and their value's length.
    /// </summary>
    private static IEnumerable<Action> Shrinks(JsonObject summary)
    {
        if (RepeatsError(summary))
        {
            yield return () => summary["failedAt"]!["reason"] = RepeatedReason;
        }

        List<JsonObject> entries = Entries(summary);
        List<JsonObject> digests = [.. entries.Where(entry => entry.ContainsKey("resultOmitted") && entry["result"] is JsonObject)];
        foreach (JsonObject digest in digests.Where(entry => IsCapture(entry["tool"]) && entry["result"]!["files"] is JsonArray))
        {
            yield return () => OmitFiles(digest["result"]!.AsObject());
        }

        foreach (JsonObject digest in digests)
        {
            yield return () => DropLongStrings(digest["result"]!.AsObject(), KeptStrings(digest));
        }

        foreach (JsonObject entry in OmittedResults(entries))
        {
            yield return () => OmitResult(entry);
        }

        foreach (JsonObject entry in TrimmedCalls(entries))
        {
            yield return () => TrimCall(entry["result"]!.AsObject());
        }
    }

    /// <summary>The passed entries whose whole result is left out, last first: the order the second pass omits them in.</summary>
    private static IEnumerable<JsonObject> OmittedResults(List<JsonObject> entries) =>
        Enumerable.Reverse(entries).Where(entry => IsPassed(entry) && !entry.ContainsKey("resultOmitted"));

    /// <summary>The passed entries holding a call that raised, last first: the order the second pass trims them in.</summary>
    private static IEnumerable<JsonObject> TrimmedCalls(List<JsonObject> entries) =>
        Enumerable.Reverse(entries).Where(entry => IsPassed(entry) && HasRaisedCall(entry));

    /// <summary>Whether the entry's result holds a call that raised, which a digest or a left-out result keeps.</summary>
    private static bool HasRaisedCall(JsonObject entry) => entry["result"] is JsonObject result && IsRaisedCall("call", result["call"]);

    /// <summary>Replaces the result's raised call with its raised text and, when it has one, its value's length.</summary>
    private static void TrimCall(JsonObject result)
    {
        if (result["call"] is not JsonObject call)
        {
            return;
        }

        JsonObject trimmed = [];
        if (call["raised"] is { } raised)
        {
            trimmed["raised"] = raised.DeepClone();
        }

        if (call["valueLength"] is { } length)
        {
            trimmed["valueLength"] = length.DeepClone();
        }

        result["call"] = trimmed;
    }

    /// <summary>The string fields the long-string cut keeps in the entry's digest: a capture's warning and folder, else none.</summary>
    private static string[] KeptStrings(JsonObject entry) => IsCapture(entry["tool"]) ? CaptureKeptStrings : [];

    private static void OmitFiles(JsonObject digest)
    {
        digest["filesOmitted"] = digest["files"]!.AsArray().Count;
        digest.Remove("files");
    }

    /// <summary>Drops the digest's string fields past <see cref="MaxKeptStringChars"/>, but its errors and the fields named kept.</summary>
    private static void DropLongStrings(JsonObject digest, string[] kept)
    {
        foreach (
            string key in digest.Where(field => !kept.Contains(field.Key) && IsLongString(field.Key, field.Value)).Select(field => field.Key).ToList()
        )
        {
            digest.Remove(key);
        }
    }

    private static bool IsLongString(string key, JsonNode? value) =>
        !ErrorFields.Contains(key) && value is JsonValue text && text.TryGetValue(out string? shown) && shown.Length > MaxKeptStringChars;

    /// <summary>
    /// Replaces a passed entry's result with resultOmitted and its length, and keeps the result's errors, errorsOmitted and
    /// its call when that raised; an entry without a result is left as it is.
    /// </summary>
    private static void OmitResult(JsonObject entry)
    {
        if (entry["result"] is not { } result)
        {
            return;
        }

        entry["resultOmitted"] = result.ToJsonString().Length;
        JsonObject? kept = result is JsonObject fields ? Fields(fields, (key, value) => ErrorFields.Contains(key) || IsRaisedCall(key, value)) : null;
        entry.Remove("result");
        if (kept is { Count: > 0 })
        {
            entry["result"] = kept;
        }
    }

    /// <summary>
    /// Whether failedAt.reason is the failed entry's, or failed member's, own error, and longer than the text that would
    /// replace it.
    /// </summary>
    private static bool RepeatsError(JsonObject summary) =>
        summary["failedAt"] is JsonObject failedAt
        && Text(failedAt["reason"]) is { } reason
        && reason.Length > RepeatedReason.Length
        && reason == Text(FailedEntry(summary, failedAt)?["error"]);

    /// <summary>The entry failedAt names, or its member when it names one; null when there is no such entry.</summary>
    private static JsonNode? FailedEntry(JsonObject summary, JsonObject failedAt)
    {
        JsonNode? failed = Item(summary["steps"], failedAt["index"]);
        return failedAt["member"] is null ? failed : Item(failed is JsonObject group ? group["members"] : null, failedAt["member"]);
    }

    /// <summary>The list's item at the index; null when either is missing or the index is out of range.</summary>
    private static JsonNode? Item(JsonNode? list, JsonNode? index) =>
        list is JsonArray items && index is JsonValue value && value.TryGetValue(out int at) && at >= 0 && at < items.Count ? items[at] : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>Replaces a passed entry's result past <see cref="MaxKeptResultChars"/> with its digest; true when it did.</summary>
    private static bool Shape(JsonObject entry)
    {
        if (!IsPassed(entry) || entry["result"] is not { } result)
        {
            return false;
        }

        int length = result.ToJsonString().Length;
        if (length <= MaxKeptResultChars)
        {
            return false;
        }

        JsonObject? digest = result is JsonObject fields ? Digest(entry["tool"], fields) : null;
        entry.Remove("result");
        if (digest is not null)
        {
            entry["result"] = digest;
        }

        entry["resultOmitted"] = length;
        return true;
    }

    private static bool IsPassed(JsonObject entry) => entry["ok"] is JsonValue ok && ok.TryGetValue(out bool passed) && passed;

    private static bool IsCapture(JsonNode? tool) => Text(tool) == CaptureFrames;

    /// <summary>Whether the field is a result's call and it raised: an object holding the raised error.</summary>
    private static bool IsRaisedCall(string key, JsonNode? value) => key == "call" && value is JsonObject call && call.ContainsKey("raised");

    /// <summary>
    /// capture_frames' capture fields, its call when that raised, and its points' count; for any other step, its scalar
    /// fields, errors and its call when that raised.
    /// </summary>
    private static JsonObject Digest(JsonNode? tool, JsonObject result)
    {
        if (!IsCapture(tool))
        {
            return Fields(result, (key, value) => ErrorFields.Contains(key) || IsScalar(value) || IsRaisedCall(key, value));
        }

        JsonObject digest = Fields(result, (key, value) => CaptureFields.Contains(key) || IsRaisedCall(key, value));
        digest["pointsOmitted"] = result["points"] is JsonArray points ? points.Count : 0;
        return digest;
    }

    /// <summary>A copy of the result's fields that <paramref name="kept"/> picks, in their order.</summary>
    private static JsonObject Fields(JsonObject result, Func<string, JsonNode?, bool> kept)
    {
        JsonObject fields = [];
        foreach ((string key, JsonNode? value) in result.Where(field => kept(field.Key, field.Value)))
        {
            fields[key] = value?.DeepClone();
        }

        return fields;
    }

    /// <summary>Whether a field's value is a string, number, boolean or null: anything but an object or an array.</summary>
    private static bool IsScalar(JsonNode? value) => value is null or JsonValue;
}

/// <summary>
/// What a summary's cut says: the mode, the whole result's length, and the spill file when it was written, else why not.
/// </summary>
internal sealed record BatchCut(string? Mode, int Length, string? Path, string? Reason)
{
    /// <summary>The cut text for <paramref name="replaced"/> entries left out, saying so when the reply still does not fit.</summary>
    public string Text(int replaced, bool stillOver)
    {
        string head =
            Mode == BatchSummary.SummaryMode
                ? string.Create(CultureInfo.InvariantCulture, $"options.result is summary: {replaced} steps' results are left out here")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"the result ran to {Length} characters, past {ScratchView.MaxResultChars}; {replaced} steps' results are left out here"
                );
        string over = stillOver
            ? string.Create(CultureInfo.InvariantCulture, $"; the reply is still over {ScratchView.MaxResultChars} characters")
            : "";
        return Path is not null ? $"{head}{over}; the whole result is in {Path}" : $"{head}{over}; the whole result could not be written: {Reason}";
    }
}
