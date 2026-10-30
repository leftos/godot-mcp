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
    /// Delivers <paramref name="clip"/> to <paramref name="destination"/>; a refusal, a failed move, or a destination whose
    /// final path an earlier clip of the recording was delivered to keeps the clip where it is, and a copy whose source could
    /// not be deleted is at the destination.
    /// </summary>
    /// <param name="clip">The finished clip.</param>
    /// <param name="destination">Where its start mark asked for it.</param>
    /// <param name="overwrite">Whether an existing file there may be replaced.</param>
    /// <param name="delivered">The final paths earlier clips of the same recording were delivered to, by 1-based clip number.</param>
    /// <returns>
    /// Where the clip is now; the final path it was delivered to, null when it was kept; and a warning naming both paths when
    /// something went wrong, null when nothing did.
    /// </returns>
    public static (string Path, string? FinalPath, string? Warning) Deliver(
        string clip,
        OutputTarget destination,
        bool overwrite,
        IReadOnlyDictionary<string, int> delivered
    ) => Deliver(clip, destination, overwrite, delivered, OutputDestination.MoveReplacing);

    /// <summary>Delivers with <paramref name="move"/>, which returns why its source is left after the copy, or null.</summary>
    internal static (string Path, string? FinalPath, string? Warning) Deliver(
        string clip,
        OutputTarget destination,
        bool overwrite,
        IReadOnlyDictionary<string, int> delivered,
        Func<string, string, bool, string?> move
    )
    {
        OutputTarget target;
        string? leftover;
        try
        {
            target = OutputDestination.Check(destination.Places, destination.Requested, Extension);
            if (delivered.TryGetValue(target.FinalPath, out int earlier))
            {
                return (
                    clip,
                    null,
                    $"the clip could not be moved to {destination.FullPath}: clip {earlier} of this recording was delivered to "
                        + $"{target.FinalPath}; it is kept at {clip}"
                );
            }

            leftover = move(clip, target.FullPath, overwrite);
        }
        catch (Exception e) when (e is McpException or IOException or UnauthorizedAccessException)
        {
            return (clip, null, $"the clip could not be moved to {destination.FullPath}: {e.Message}; it is kept at {clip}");
        }

        return leftover is null
            ? (target.FullPath, target.FinalPath, null)
            : (
                target.FullPath,
                target.FinalPath,
                $"the clip was moved to {target.FullPath}, but its copy at {clip} could not be deleted: {leftover}"
            );
    }
}
