using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// The game output log of one scratch run's scene, and the folder's share of them: named for the scene's session and the run,
/// so repeated runs of one scene keep a log each, and pruned to the newest <see cref="Kept"/> when a run starts; and the shot
/// folder each run's screenshots are kept under, pruned to the newest <see cref="Kept"/> the same way.
/// </summary>
internal static partial class ScratchRunLogs
{
    /// <summary>How many scratch run logs, and how many scratch shot folders, a project's log folder keeps, the newest by last write time.</summary>
    public const int Kept = 20;

    /// <summary>
    /// The file a scratch session's game writes its whole output to:
    /// <c>run-&lt;session name&gt;-&lt;run id&gt;.log</c> in <paramref name="logFolder"/>, a name of its own for every run.
    /// </summary>
    /// <param name="logFolder">The project's log folder, as <see cref="ProjectPrep.LogFolder"/> gives it.</param>
    /// <param name="sessionName">The scene's scratch session name.</param>
    /// <param name="runId">The scratch run's id, in <see cref="ScratchBackgroundRun.NewRunId"/>'s form.</param>
    internal static string PathFor(string logFolder, string sessionName, string runId) => Path.Combine(logFolder, $"run-{sessionName}-{runId}.log");

    /// <summary>
    /// Deletes every scratch run log in <paramref name="logFolder"/> but the <paramref name="keep"/> newest by last write time,
    /// so repeated runs of a scene leave the newest ones behind; a folder that cannot be listed or a file that cannot be deleted
    /// is logged and left, and the run goes on.
    /// </summary>
    /// <param name="logFolder">The project's log folder, as <see cref="ProjectPrep.LogFolder"/> gives it.</param>
    /// <param name="keep">How many of the newest run logs to keep.</param>
    /// <param name="logger">Where a failure to list or delete is reported.</param>
    internal static void Prune(string logFolder, int keep, ILogger logger)
    {
        if (!Directory.Exists(logFolder))
        {
            return;
        }

        List<string> older;
        try
        {
            older =
            [
                .. Directory
                    .GetFiles(logFolder, "run-*.log")
                    .Where(file => IsRunLog(Path.GetFileName(file)))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ThenByDescending(Path.GetFileName, StringComparer.Ordinal)
                    .Skip(keep),
            ];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.ScratchRunLogPruneFailed(logger, e, logFolder);
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
                Log.ScratchRunLogDeleteFailed(logger, e, file);
            }
        }
    }

    /// <summary>
    /// Deletes every scratch run's shot folder in <paramref name="logFolder"/> but the <paramref name="keep"/> newest by last
    /// write time, so repeated runs leave the newest ones behind; a folder that cannot be listed or removed is logged and left,
    /// and a folder that is a link is only unlinked, so the delete never follows it out of the project.
    /// </summary>
    /// <param name="logFolder">The project's log folder, as <see cref="ProjectPrep.LogFolder"/> gives it.</param>
    /// <param name="keep">How many of the newest shot folders to keep.</param>
    /// <param name="logger">Where a failure to list or remove is reported.</param>
    internal static void PruneShots(string logFolder, int keep, ILogger logger)
    {
        if (!Directory.Exists(logFolder))
        {
            return;
        }

        List<string> older;
        try
        {
            older =
            [
                .. Directory
                    .EnumerateDirectories(logFolder)
                    .Where(folder => IsShotFolder(Path.GetFileName(folder)))
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .ThenByDescending(Path.GetFileName, StringComparer.Ordinal)
                    .Skip(keep),
            ];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.ScratchShotFolderListFailed(logger, e, logFolder);
            return;
        }

        foreach (string folder in older)
        {
            PruneShotFolder(folder, logger);
        }
    }

    /// <summary>
    /// Deletes one shot folder <paramref name="folder"/>; one that cannot be removed is logged to <paramref name="logger"/> and left,
    /// and one already gone, another run's prune having deleted it first, is done and logs nothing.
    /// </summary>
    internal static void PruneShotFolder(string folder, ILogger logger)
    {
        try
        {
            DeleteFolder(folder);
        }
        catch (Exception e) when (e is DirectoryNotFoundException or FileNotFoundException)
        {
            // Another run pruned it between this run's listing and its delete: the folder is gone, as this prune wanted.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.ScratchShotFolderPruneFailed(logger, e, folder);
        }
    }

    /// <summary>Deletes a shot folder and everything in it; a folder that is itself a link is only unlinked.</summary>
    private static void DeleteFolder(string folder)
    {
        DirectoryInfo info = new(folder);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            info.Delete();
            return;
        }

        info.Delete(recursive: true);
    }

    private static bool IsRunLog(string name) => RunLogName().IsMatch(name);

    private static bool IsShotFolder(string name) => ShotFolderName().IsMatch(name);

    [GeneratedRegex(@"^run-.+\.scratch-.+-\d{8}T\d{6}-[0-9a-f]{4}\.log$", RegexOptions.CultureInvariant)]
    private static partial Regex RunLogName();

    [GeneratedRegex(@"^scratch-\d{8}T\d{6}-[0-9a-f]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShotFolderName();
}
