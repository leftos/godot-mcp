using System.Globalization;

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

/// <summary>The killReason stop_project reports when it had to kill the game.</summary>
internal static class GameKillReason
{
    /// <summary>The game left a ping unanswered, so the stop killed it without asking it to quit.</summary>
    public const string Silent = "the game did not answer a ping, so it was killed at once";

    /// <summary>Why a game that was still running when the stop's grace ended was killed.</summary>
    /// <param name="request">What became of the quit request.</param>
    /// <param name="grace">How long the stop waited for the game to exit.</param>
    public static string AfterGrace(QuitRequest request, TimeSpan grace)
    {
        string seconds = grace.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        return request switch
        {
            QuitRequest.Acknowledged => $"the game acknowledged the quit but was still shutting down after {seconds} s",
            QuitRequest.Unanswered => $"the game did not answer the quit request within {seconds} s",
            _ => $"the game had no bridge connection to ask it to quit, and had not exited after {seconds} s",
        };
    }
}
