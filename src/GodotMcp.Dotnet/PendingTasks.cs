using System.Globalization;
using System.Text.Json.Nodes;
using Godot;

namespace GodotMcp.Dotnet;

/// <summary>
/// The tasks a <c>call</c> or a <c>run</c> returned unfinished, by id <c>c&lt;n&gt;</c>, and the helper's <c>poll</c> and
/// <c>forget</c> ops that settle or drop them. Each entry carries the reply its finished task gives and a release hook, run
/// exactly once: when a poll settles the entry, or when a forget drops it. A snippet's entry also carries the source of the
/// token the snippet was given, cancelled when a forget drops the entry.
/// </summary>
internal static class PendingTasks
{
    /// <summary>The unfinished tasks, by id.</summary>
    private static readonly Dictionary<string, Entry> Pending = new(StringComparer.Ordinal);

    private static long _issued;

    /// <summary>
    /// Stores <paramref name="task"/> under a fresh id and answers <c>{"ok":true,"pending":"c&lt;n&gt;"}</c>;
    /// <paramref name="settle"/> writes the reply once the task has finished, <paramref name="release"/>, when given, runs
    /// once the entry is settled or forgotten, and <paramref name="cancellation"/>, when given, is the source of a snippet's
    /// own token, cancelled when a forget drops the entry.
    /// </summary>
    public static JsonObject Add(Task task, Func<JsonObject> settle, Action? release, CancellationTokenSource? cancellation)
    {
        string id = "c" + (++_issued).ToString(CultureInfo.InvariantCulture);
        Pending[id] = new Entry(task, settle, release, cancellation);
        return PendingReply(id);
    }

    /// <summary><c>{"op":"poll","id":"c&lt;n&gt;"}</c> → the final reply once the task has finished, else the pending reply again.</summary>
    public static JsonObject Poll(JsonObject request)
    {
        string id = request["id"]!.GetValue<string>();
        if (!Pending.TryGetValue(id, out Entry? entry))
        {
            return Unknown(id);
        }
        if (!entry.Task.IsCompleted)
        {
            return PendingReply(id);
        }
        Pending.Remove(id);
        try
        {
            return entry.Settle();
        }
        finally
        {
            entry.Release?.Invoke();
        }
    }

    /// <summary>
    /// <c>{"op":"forget","id":"c&lt;n&gt;"}</c> → <c>{"forgotten":"c&lt;n&gt;"}</c>: the entry is dropped and its task left
    /// running, a later fault observed so it is never reported as unobserved, and a snippet's token cancelled so what it does
    /// on the token stops.
    /// </summary>
    public static JsonObject Forget(JsonObject request)
    {
        string id = request["id"]!.GetValue<string>();
        if (!Pending.Remove(id, out Entry? entry))
        {
            return Unknown(id);
        }
        _ = entry.Task.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default
        );
        Cancel(entry);
        entry.Release?.Invoke();
        return new JsonObject
        {
            ["ok"] = true,
            ["result"] = new JsonObject { ["forgotten"] = id },
        };
    }

    /// <summary>Cancels the entry's token; a callback registered on it that threw is logged, and the entry released anyway.</summary>
    private static void Cancel(Entry entry)
    {
        try
        {
            entry.Cancellation?.Cancel();
        }
        catch (AggregateException e)
        {
            GD.PushError($"godot-mcp: a snippet's cancellation callback threw: {e.GetBaseException().Message}");
        }
    }

    private static JsonObject Unknown(string id) => Helper.Failure($"No pending call '{id}': its reply was given, or it was forgotten.");

    private static JsonObject PendingReply(string id) => new() { ["ok"] = true, ["pending"] = id };

    /// <summary>
    /// An unfinished task, the reply it gives once finished, what to release when it is settled or forgotten, and the source
    /// of a snippet's own cancellation token, when it has one.
    /// </summary>
    private sealed record Entry(Task Task, Func<JsonObject> Settle, Action? Release, CancellationTokenSource? Cancellation);
}
