using System.Diagnostics;
using System.Text;

namespace GodotMcp.Server.Session;

/// <summary>
/// The machine-wide list of the project folders servers have written a marked <c>override.cfg</c> in, one folder a line, so
/// a server that starts after another was killed removes the file the killed one left (<see cref="Sweep"/>). Every server
/// opens the list alone (no sharing), retrying a refusal for up to <see cref="RetryBudgetMs"/>, since several may start or
/// launch at once; a list it cannot open or write is reported and never stops the server.
/// </summary>
/// <param name="listPath">The list file.</param>
/// <param name="errors">Where failures are reported: stderr in the server.</param>
internal sealed class OverrideFolders(string listPath, TextWriter errors)
{
    private const int RetryIntervalMs = 20;
    private const int RetryBudgetMs = 2000;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The servers' shared list: <c>%LOCALAPPDATA%\godot-mcp-cache\override-folders.txt</c>.</summary>
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "godot-mcp-cache", "override-folders.txt");

    /// <summary>The servers' shared list, reporting to stderr.</summary>
    public static OverrideFolders Default { get; } = new(DefaultPath, Console.Error);

    public string ListPath => listPath;

    /// <summary>
    /// Adds the folder to the list, then runs <paramref name="write"/> while the list is still held, so a sweep never finds
    /// the folder listed before its file is written. A list that cannot be opened or written is reported and
    /// <paramref name="write"/> runs all the same: a missing entry loses only the cleanup after a kill.
    /// </summary>
    public void RecordWhile(string projectDir, Action write)
    {
        using FileStream? list = TryOpen($"recording {projectDir}");
        if (list is not null)
        {
            TryAdd(list, projectDir);
        }

        write();
    }

    /// <summary>
    /// Runs <paramref name="action"/> while the list is held alone, recording nothing, so a release of a folder's
    /// override.cfg or a write of its armed.json never interleaves with another server's read-modify-write of them. A list that
    /// cannot be opened is reported and <paramref name="action"/> runs all the same. Never call it while this thread already
    /// holds the list: the open would be refused until the retry budget runs out.
    /// </summary>
    /// <param name="purpose">What the hold is for, as a failure to open the list reports it.</param>
    /// <param name="action">The work done under the hold.</param>
    public T Hold<T>(string purpose, Func<T> action)
    {
        using (TryOpen(purpose))
        {
            return action();
        }
    }

    /// <inheritdoc cref="Hold{T}(string, Func{T})"/>
    public void Hold(string purpose, Action action) =>
        Hold(
            purpose,
            () =>
            {
                action();
                return true;
            }
        );

    /// <summary>
    /// Deletes the folder's marked override.cfg when no live server owns it, under the list's hold; a file a live server owns
    /// stays, this server's own included.
    /// </summary>
    public void RemoveStaleOverride(string projectDir) => Hold($"clearing {projectDir}", () => SweepOverride(projectDir));

    /// <summary>
    /// Deletes the marked override.cfg and the armed.json of every listed folder whose owners have all exited, and drops from
    /// the list every folder that no longer holds either (an unmarked override.cfg is never touched). Never throws: a failure
    /// is reported with its folder, and a folder whose file could not be removed stays listed for the next sweep.
    /// </summary>
    public void Sweep()
    {
        if (!File.Exists(listPath))
        {
            return;
        }

        using FileStream? list = TryOpen("the startup sweep");
        if (list is null)
        {
            return;
        }

        try
        {
            List<string> kept = [.. ReadFolders(list).Where(KeepAfterSweep)];
            Rewrite(list, kept);
        }
        catch (Exception e) when (IsFileSystemFailure(e))
        {
            errors.WriteLine($"godot-mcp: rewriting {listPath} after the startup sweep failed: {Describe(e)}");
        }
    }

    /// <summary>
    /// Removes the folder's stale marked override.cfg and its stale armed.json (<see cref="ArmFile"/>); returns whether the
    /// folder stays listed: either file is still in use, or its removal failed.
    /// </summary>
    private bool KeepAfterSweep(string projectDir)
    {
        bool keepArm = SweepOne(projectDir, "armed.json", SweepArmFile);
        bool keepOverride = SweepOne(projectDir, OverrideFile.FileName, SweepOverride);
        return keepArm || keepOverride;
    }

    /// <summary>Runs one file's sweep; a failure is reported with its folder and keeps the folder listed.</summary>
    private bool SweepOne(string projectDir, string file, Func<string, bool> sweep)
    {
        try
        {
            return sweep(projectDir);
        }
        catch (Exception e) when (IsFileSystemFailure(e))
        {
            errors.WriteLine($"godot-mcp: removing the leftover {file} in {projectDir} at startup failed: {Describe(e)}");
            return true;
        }
    }

    /// <summary>Deletes the folder's marked override.cfg when its owners have all exited; returns whether a live one is left.</summary>
    private static bool SweepOverride(string projectDir)
    {
        string path = OverrideFile.PathIn(projectDir);
        if (!File.Exists(path) || !OverrideFile.IsOurs(path))
        {
            return false;
        }

        if (OverrideFile.LiveOwners(projectDir).Count > 0)
        {
            return true;
        }

        File.Delete(path);
        return false;
    }

    /// <summary>Deletes the folder's armed.json when its owners have all exited or it lists none; returns whether a live one is left.</summary>
    private static bool SweepArmFile(string projectDir)
    {
        string path = ArmFile.PathIn(projectDir);
        if (!File.Exists(path))
        {
            return false;
        }

        if (ArmFile.LiveOwners(projectDir).Count > 0)
        {
            return true;
        }

        File.Delete(path);
        return false;
    }

    private void TryAdd(FileStream list, string projectDir)
    {
        try
        {
            List<string> folders = ReadFolders(list);
            if (!folders.Any(folder => ProjectPaths.AreSame(folder, projectDir)))
            {
                folders.Add(ProjectPaths.Normalise(projectDir));
                Rewrite(list, folders);
            }
        }
        catch (Exception e) when (IsFileSystemFailure(e))
        {
            errors.WriteLine($"godot-mcp: adding {projectDir} to {listPath} failed: {Describe(e)}");
        }
    }

    /// <summary>The list opened alone, created when missing; null, reported, when it stays refused past the retry budget.</summary>
    private FileStream? TryOpen(string purpose)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(listPath)!);
                return new FileStream(listPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (clock.ElapsedMilliseconds < RetryBudgetMs)
            {
                Thread.Sleep(RetryIntervalMs);
            }
            catch (Exception e) when (IsFileSystemFailure(e))
            {
                errors.WriteLine($"godot-mcp: opening {listPath} for {purpose} failed: {Describe(e)}");
                return null;
            }
        }
    }

    /// <summary>The listed folders, blank lines and repeats left out.</summary>
    private static List<string> ReadFolders(FileStream list)
    {
        list.Position = 0;
        using StreamReader reader = new(list, Utf8NoBom, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        List<string> folders = [];
        while (reader.ReadLine() is { } line)
        {
            string folder = line.Trim();
            if (folder.Length > 0 && !folders.Any(listed => ProjectPaths.AreSame(listed, folder)))
            {
                folders.Add(folder);
            }
        }

        return folders;
    }

    private static void Rewrite(FileStream list, List<string> folders)
    {
        byte[] content = Utf8NoBom.GetBytes(string.Concat(folders.Select(folder => folder + "\n")));
        list.Position = 0;
        list.SetLength(0);
        list.Write(content);
        list.Flush();
    }

    /// <summary>What a file operation on a folder the list names can throw, a malformed line's path included.</summary>
    private static bool IsFileSystemFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static string Describe(Exception e) => $"{e.GetType().Name}: {e.Message}";
}
