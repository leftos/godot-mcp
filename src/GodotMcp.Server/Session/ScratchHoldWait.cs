using System.Globalization;
using System.Text.Json.Nodes;

namespace GodotMcp.Server.Session;

/// <summary>
/// One read of a scene's output for the exit hold's end: the feed entries taken at its "pace" end marker once both marker lines
/// and the feed's mark are in, else null; and whether stdout's and stderr's marker lines each arrived, for a timeout's text.
/// </summary>
internal readonly record struct HoldRead(IReadOnlyList<ErrorEntry>? Marked, bool StdoutArrived, bool StderrArrived);

/// <summary>
/// The terms of an exit hold's wait: <paramref name="Window"/>, the load-adjusted time the hold's game clock may stand still
/// before it fails; <paramref name="Readers"/>, how long the markers are waited for once the bridge says the hold ended;
/// the hold's length in game milliseconds and its end marker; and the error a closed connection ends the wait with.
/// </summary>
internal sealed record HoldWaitTerms(TimeSpan Window, TimeSpan Readers, int GameMs, string Marker, string ClosedError);

/// <summary>
/// The wait for the exit hold's end markers, a stall guard on the hold's own game clock rather than a fixed allowance a slowed
/// game must beat: it reads the output every <see cref="ScratchRun.MarkerPoll"/>, and each time a window of load-adjusted time passes
/// with no markers, it asks the bridge for the hold's progress (its hold command). A clock that advanced since the last ask
/// starts a new window; one that stood still a whole window fails, as does no hold running or a refused ask; a hold the bridge
/// says ended leaves the readers' allowance for its markers. Every verdict is followed by one more read, so a marker that
/// arrived meanwhile wins, and a closed connection wins over any verdict. Single use: one per hold.
/// </summary>
internal sealed class ScratchHoldWait(LoadClock clock, HoldWaitTerms terms, Func<HoldRead> read, Func<CancellationToken, Task<JsonNode?>> probe)
{
    private LoadDeadline? _deadline;
    private double _seen;
    private bool _ended;
    private string? _verdict;
    private TimeSpan _waited;

    /// <summary>Why a hold whose clock stopped advancing failed: the game seconds it ran of its length, and the window it stood still.</summary>
    internal static string HoldStoodStill(double seenSeconds, int gameMs, TimeSpan window)
    {
        string ran = seenSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        string length = (gameMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        string stood = window.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        return $"the exit hold ran {ran} of {length} game s, then no more for {stood} s of load-adjusted time: "
            + "the game's clock stood still (is it paused?)";
    }

    /// <summary>Why the exit failed when the bridge reported no hold of this marker running.</summary>
    internal static string HoldNotRunning => "the bridge ran no exit hold, so its pace end marker never came";

    /// <summary>Why the exit failed when the bridge refused the hold command with <paramref name="message"/>.</summary>
    internal static string HoldUnread(string message) => $"the exit hold's progress could not be read: {message}";

    /// <summary>
    /// Waits for the hold's end: the entries its read took, null error, not closed; the closed error, closed, once
    /// <paramref name="closed"/> completes or the ask fails on the connection; else the verdict, not closed.
    /// </summary>
    /// <exception cref="TimeoutException">The bridge left an ask of the hold's progress unanswered within its timeout.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="limit"/> was cancelled.</exception>
    public async Task<(string? Error, bool Closed, IReadOnlyList<ErrorEntry>? Marked)> RunAsync(Task closed, CancellationToken limit)
    {
        _deadline = clock.Start(terms.Window, limit);
        try
        {
            while (true)
            {
                bool expired = _deadline?.Expired == true;
                HoldRead seen = read();
                if (Settled(seen, closed) is { } settled)
                {
                    return settled;
                }

                if (!expired)
                {
                    await Task.Delay(ScratchRun.MarkerPoll, clock.Time, limit);
                }
                else if (await ExpireAsync(seen, closed, limit))
                {
                    return (terms.ClosedError, true, null);
                }
            }
        }
        finally
        {
            _deadline?.Dispose();
        }
    }

    /// <summary>
    /// How the wait ends after <paramref name="seen"/>, in this order: its markers and mark in; the connection closed; a verdict
    /// already reached. Null while none holds.
    /// </summary>
    private (string? Error, bool Closed, IReadOnlyList<ErrorEntry>? Marked)? Settled(HoldRead seen, Task closed)
    {
        if (seen.Marked is { } marked)
        {
            return (null, false, marked);
        }

        if (closed.IsCompleted)
        {
            return (terms.ClosedError, true, null);
        }

        return _verdict is { } verdict ? (verdict, false, null) : null;
    }

    /// <summary>
    /// The window that passed: after an ended hold, the markers' timeout; else the bridge's answer about the hold, a new window
    /// or a verdict. True when the ask failed on the connection.
    /// </summary>
    private async Task<bool> ExpireAsync(HoldRead seen, Task closed, CancellationToken limit)
    {
        _waited += _deadline!.Budget;
        _deadline.Dispose();
        _deadline = null;
        if (_ended)
        {
            _verdict = ScratchRun.MarkerTimeout("pace", _waited, seen.StdoutArrived, seen.StderrArrived);
            return false;
        }

        JsonNode? reply;
        try
        {
            reply = await probe(limit);
        }
        catch (Exception e) when (e is not (TimeoutException or OperationCanceledException) && (e is IOException || closed.IsCompleted))
        {
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or SessionException)
        {
            _verdict = HoldUnread(e.Message);
            return false;
        }

        _deadline = Judge(reply, limit);
        return false;
    }

    /// <summary>
    /// What the bridge's report of the hold means: this hold running further than last seen, a new window; running no further,
    /// stood still; ended, the readers' allowance; anything else, no hold. Null when it set a verdict.
    /// </summary>
    private LoadDeadline? Judge(JsonNode? reply, CancellationToken limit)
    {
        string? state = Text(reply, "state");
        bool ours = Text(reply, "marker") == terms.Marker;
        if (ours && state == "running" && Seconds(reply) is { } seconds)
        {
            if (seconds > _seen)
            {
                _seen = seconds;
                return clock.Start(terms.Window, limit);
            }

            _verdict = HoldStoodStill(_seen, terms.GameMs, terms.Window);
            return null;
        }

        if (ours && state == "ended")
        {
            _ended = true;
            return clock.Start(terms.Readers, limit);
        }

        _verdict = HoldNotRunning;
        return null;
    }

    private static string? Text(JsonNode? reply, string field) =>
        reply is JsonObject answer && answer[field] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static double? Seconds(JsonNode? reply) =>
        reply is JsonObject answer && answer["seconds"] is JsonValue value && value.TryGetValue(out double seconds) ? seconds : null;
}
