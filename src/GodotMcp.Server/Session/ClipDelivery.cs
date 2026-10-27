using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Server.Session;

/// <summary>
/// Moves a finished clip, real-time or cut from a Movie Maker movie, from recordings/ to the destination its start named,
/// checked again against the places found at the start, so no git runs at the finish.
/// </summary>
internal static class ClipDelivery
{
    /// <summary>The extension a clip's named destination ends in.</summary>
    public const string Extension = ".mp4";

    /// <summary>
    /// Delivers <paramref name="clip"/> to <paramref name="destination"/>; a refusal or a failed move keeps the clip where it
    /// is, and a copy whose source could not be deleted is at the destination.
    /// </summary>
    /// <returns>Where the clip is now, and a warning naming both paths when something went wrong; null when nothing did.</returns>
    public static (string Path, string? Warning) Deliver(string clip, OutputTarget destination, bool overwrite) =>
        Deliver(clip, destination, overwrite, OutputDestination.MoveReplacing);

    /// <summary>Delivers with <paramref name="move"/>, which returns why its source is left after the copy, or null.</summary>
    internal static (string Path, string? Warning) Deliver(
        string clip,
        OutputTarget destination,
        bool overwrite,
        Func<string, string, bool, string?> move
    )
    {
        string target;
        string? leftover;
        try
        {
            target = OutputDestination.Check(destination.Places, destination.Requested, Extension);
            leftover = move(clip, target, overwrite);
        }
        catch (Exception e) when (e is McpException or IOException or UnauthorizedAccessException)
        {
            return (clip, $"the clip could not be moved to {destination.FullPath}: {e.Message}; it is kept at {clip}");
        }

        return leftover is null
            ? (target, null)
            : (target, $"the clip was moved to {target}, but its copy at {clip} could not be deleted: {leftover}");
    }
}
