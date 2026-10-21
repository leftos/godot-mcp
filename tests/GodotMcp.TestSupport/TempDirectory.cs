using System.Diagnostics;
using GodotMcp.Server.Session;

namespace GodotMcp.TestSupport;

/// <summary>
/// A fresh folder under the system temp directory, deleted on dispose. It is outside every git repository, which the
/// git tests rely on; the repository's own <c>.tmp/</c> would not be.
/// </summary>
/// <remarks>
/// A test process its gate kills, or a teardown that throws before its delete, runs no <see cref="Dispose"/>, so each
/// folder carries a marker beside it naming the process that made it, and the first folder a process makes starts a
/// sweep of the root, off the test path, that takes the folders whose process has exited.
/// </remarks>
public sealed class TempDirectory : IDisposable
{
    /// <summary>The suffix of the file beside a folder that names the process which made it.</summary>
    private const string MarkerSuffix = ".owner";

    private const int RetryIntervalMs = 20;
    private const int RetryBudgetMs = 2000;
    private const int FolderNameLength = 32;
    private const int SweepNotStarted = 0;
    private const int SweepStarted = 1;

    /// <summary>
    /// An unmarked folder is taken only when it is older than any run could be, so a concurrent run of a build from
    /// before the marker keeps its folder.
    /// </summary>
    private static readonly TimeSpan UnmarkedFolderAge = TimeSpan.FromHours(24);

    /// <summary>Whether this process has started its one sweep of the shared root.</summary>
    private static int _sweepState;

    public TempDirectory()
    {
        string root = Root;
        Directory.CreateDirectory(root);
        StartSweepOnce();
        Path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
        File.WriteAllText(MarkerOf(Path), OverrideOwner.Current.ToString());
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>The folder every <see cref="TempDirectory"/> is made under.</summary>
    private static string Root => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "godot-mcp-tests");

    /// <summary>
    /// Run after a delete attempt a holder refused and before the next one: a test holding a file releases it here, so
    /// the retry it proves is the folder's own and not whatever the thread pool does with a scheduled release.
    /// </summary>
    public Action AfterRefusedAttempt { get; set; } = static () => { };

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>
    /// Deletes the folder, retrying for up to 2 s one that another process still holds, and then its marker: a refused
    /// folder delete throws once the budget is spent, and a refused marker delete is reported and left to the next sweep.
    /// </summary>
    public void Dispose()
    {
        DeleteFolderRetrying();
        TryDelete(() => File.Delete(MarkerOf(Path)), MarkerOf(Path));
    }

    /// <summary>
    /// Deletes the folders a run that died left behind: one whose marker names a process that has exited, and one with
    /// no marker that is older than any run could be. Every other entry of <paramref name="root"/> is left alone, and an
    /// entry this process cannot read, or a folder it cannot delete, is reported and left for the next sweep.
    /// </summary>
    public static void SweepStale(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
            {
                SweepEntry(root, entry);
            }
        }
        catch (Exception e) when (IsFileSystemRefusal(e))
        {
            Console.Error.WriteLine($"godot-mcp-tests: could not list {root} to sweep it: {e.Message}");
        }
    }

    /// <summary>The process's one background sweep of the shared root, started by the first folder it makes.</summary>
    private static void StartSweepOnce()
    {
        if (Interlocked.Exchange(ref _sweepState, SweepStarted) == SweepNotStarted)
        {
            _ = Task.Run(SweepSharedRoot);
        }
    }

    /// <summary>The shared root's sweep: a test waits on none of it, and a failure of it is reported and never rethrown.</summary>
    private static void SweepSharedRoot()
    {
        try
        {
            SweepStale(Root);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"godot-mcp-tests: the sweep of {Root} failed: {e.Message}");
        }
    }

    /// <summary>Sweeps one entry; what this process cannot read or delete is reported and the next entry goes on.</summary>
    private static void SweepEntry(string root, string entry)
    {
        try
        {
            string name = System.IO.Path.GetFileName(entry);
            if (name.EndsWith(MarkerSuffix, StringComparison.Ordinal))
            {
                SweepDanglingMarker(root, entry, name[..^MarkerSuffix.Length]);
            }
            else if (IsFolderName(name) && Directory.Exists(entry))
            {
                SweepFolder(entry);
            }
        }
        catch (Exception e) when (IsFileSystemRefusal(e))
        {
            Console.Error.WriteLine($"godot-mcp-tests: could not read {entry} to sweep it: {e.Message}");
        }
    }

    /// <summary>Deletes the folder, retrying for up to 2 s one that another process still holds.</summary>
    private void DeleteFolderRetrying()
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                DeleteAttempt();
                return;
            }
            catch (Exception e) when (IsFileSystemRefusal(e) && clock.ElapsedMilliseconds < RetryBudgetMs)
            {
                if (!Directory.Exists(Path))
                {
                    return;
                }

                AfterRefusedAttempt();
                Thread.Sleep(RetryIntervalMs);
            }
        }
    }

    /// <summary>
    /// One delete attempt. A killed game keeps its project folder for tens of milliseconds after it reports exited, and
    /// a scanner holds a fresh file, so a refusal here is retried by <see cref="DeleteFolderRetrying"/>: NTFS refuses to
    /// delete a folder whose file is held meanwhile.
    /// </summary>
    private void DeleteAttempt() => DeleteFolder(Path);

    /// <summary>Deletes a folder, clearing the read-only attribute git writes on the object files inside it.</summary>
    private static void DeleteFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(folder, recursive: true);
    }

    /// <summary>
    /// Takes a folder named as a <see cref="TempDirectory"/>'s: one whose marker names a process that has exited, or one
    /// with no marker that is older than any run could be. A folder whose marker it cannot read is left alone.
    /// </summary>
    private static void SweepFolder(string folder)
    {
        string marker = MarkerOf(folder);
        if (File.Exists(marker))
        {
            if (ReadOwner(marker) is not { } owner || owner.IsAlive())
            {
                return;
            }
        }
        else if (DateTime.UtcNow - Directory.GetCreationTimeUtc(folder) <= UnmarkedFolderAge)
        {
            return;
        }

        if (TryDelete(() => DeleteFolder(folder), folder))
        {
            TryDelete(() => File.Delete(marker), marker);
        }
    }

    /// <summary>Deletes the marker of an owner that has exited and whose folder is gone; leaves every other marker alone.</summary>
    private static void SweepDanglingMarker(string root, string marker, string folderName)
    {
        if (!IsFolderName(folderName) || ReadOwner(marker) is not { } owner || owner.IsAlive())
        {
            return;
        }

        if (!Directory.Exists(System.IO.Path.Combine(root, folderName)))
        {
            TryDelete(() => File.Delete(marker), marker);
        }
    }

    /// <summary>The owner a marker names, or null when the marker does not parse.</summary>
    private static OverrideOwner? ReadOwner(string marker) =>
        OverrideOwner.TryParse(File.ReadAllText(marker), out OverrideOwner owner) ? owner : null;

    /// <summary>Runs a delete, reporting a refusal and leaving the entry for the next sweep instead of throwing it.</summary>
    private static bool TryDelete(Action delete, string entry)
    {
        try
        {
            delete();
            return true;
        }
        catch (Exception e) when (IsFileSystemRefusal(e))
        {
            Console.Error.WriteLine($"godot-mcp-tests: left {entry} for the next sweep: {e.Message}");
            return false;
        }
    }

    /// <summary>Whether a name is one this class gives a folder: 32 lowercase hex characters.</summary>
    private static bool IsFolderName(string name) => name.Length == FolderNameLength && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string MarkerOf(string folder) => folder + MarkerSuffix;

    private static bool IsFileSystemRefusal(Exception e) => e is IOException or UnauthorizedAccessException;
}
