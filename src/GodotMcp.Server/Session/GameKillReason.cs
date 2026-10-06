namespace GodotMcp.Server.Session;

/// <summary>What became of the quit request a stop sends the game's bridge.</summary>
internal enum QuitRequest
{
    /// <summary>The run had no open bridge connection, so no request went out.</summary>
    NotSent,

    /// <summary>The request went out and no acknowledgement came back.</summary>
    Unanswered,

    /// <summary>The bridge acknowledged the request.</summary>
    Acknowledged,
}

/// <summary>
/// The stop's grace as it ended: its budget of load-adjusted time, the wall and load-adjusted time it ran, the share of the
/// machine free on average, and whether its backstop (<see cref="LoadClock.BackstopFactor"/> times the budget in wall time)
/// ended it rather than the budget.
/// </summary>
internal readonly record struct GraceSpent(TimeSpan Grace, TimeSpan Wall, TimeSpan Adjusted, double MeanFree, bool Backstop)
{
    /// <summary>
    /// The figures of an ended grace deadline; a wait that ran past the backstop in wall time counts as ended by it, even when
    /// the deadline's own timer had not yet said so.
    /// </summary>
    public static GraceSpent Of(LoadDeadline deadline) =>
        new(
            deadline.Budget,
            deadline.Wall,
            deadline.Adjusted,
            deadline.MeanFree,
            deadline.Reason == DeadlineReason.Backstop || deadline.Wall >= deadline.Backstop
        );

    /// <summary>
    /// "3 s of load-adjusted time (wall 8.4 s, machine free 37% on average)", or at the backstop
    /// "15 s of wall time, 5 x its 3 s grace (load-adjusted 1.2 s, machine free 8% on average)".
    /// </summary>
    public string Text
    {
        get
        {
            string free = $"machine free {LoadDeadline.Percent(MeanFree)}% on average";
            return Backstop
                ? $"{LoadDeadline.Seconds(Wall)} s of wall time, {LoadClock.BackstopFactor} x its {LoadDeadline.Seconds(Grace)} s grace "
                    + $"(load-adjusted {LoadDeadline.Seconds(Adjusted)} s, {free})"
                : $"{LoadDeadline.Seconds(Grace)} s of load-adjusted time (wall {LoadDeadline.Seconds(Wall)} s, {free})";
        }
    }
}

/// <summary>What the stop measured about a game still running when its grace ended, read before the kill.</summary>
/// <param name="Request">What became of the quit request: acknowledged or unanswered; one never sent is <see cref="GameKillReason.NotSent"/>.</param>
/// <param name="Grace">How long the stop waited for the game to exit.</param>
/// <param name="ClosedAfter">
/// How long after the quit was acknowledged (or, unanswered, sent) the bridge's connection closed; null while it was still
/// open at the kill.
/// </param>
/// <param name="ProcessState">The game's process state, as the hang probe describes it.</param>
/// <param name="StderrLines">The run's last stderr lines; null for an attached game, which has no captured output.</param>
internal sealed record GraceKill(
    QuitRequest Request,
    GraceSpent Grace,
    TimeSpan? ClosedAfter,
    string ProcessState,
    IReadOnlyList<string>? StderrLines
);

/// <summary>The killReason stop_project reports when it had to kill the game.</summary>
internal static class GameKillReason
{
    /// <summary>The game left a ping unanswered, so the stop killed it without asking it to quit.</summary>
    public const string Silent = "the game did not answer a ping, so it was killed at once";

    /// <summary>Why a game that was never asked to quit, having no bridge connection, was killed when the grace ended.</summary>
    /// <param name="grace">How long the stop waited for the game to exit.</param>
    public static string NotSent(GraceSpent grace) => $"the game had no bridge connection to ask it to quit, and had not exited after {grace.Text}";

    /// <summary>
    /// Why a game that was asked to quit and was still running when the grace ended was killed: the grace, whether the bridge's
    /// connection had closed by then, and the game's process state and last stderr lines.
    /// </summary>
    public static string AfterGrace(GraceKill kill)
    {
        string first =
            kill.Request == QuitRequest.Acknowledged
                ? $"the game acknowledged the quit but was still shutting down after {kill.Grace.Text}"
                : $"the game did not answer the quit request within {kill.Grace.Text}";
        string text = $"{first}; {Connection(kill)}.\n{kill.ProcessState}";
        if (kill.StderrLines is null)
        {
            return text;
        }

        string lines = kill.StderrLines.Count == 0 ? "(none)" : string.Join('\n', kill.StderrLines);
        return $"{text}\nLast stderr lines:\n{lines}";
    }

    private static string Connection(GraceKill kill)
    {
        if (kill.ClosedAfter is not { } closed)
        {
            return "the bridge's connection was still open, so the scene tree had not been freed";
        }

        string after = kill.Request == QuitRequest.Acknowledged ? "acknowledged" : "sent";
        return $"the bridge's connection had closed {closed.TotalMilliseconds:0} ms after the quit was {after}, "
            + "so the time went into the engine's or .NET's teardown";
    }
}
