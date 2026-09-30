using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace GodotMcp.Server.CSharp;

/// <summary>
/// The copies of the C# helper the server hands Godot. A game holds the shim and helper dlls locked until it exits, so the
/// server never gives Godot the build folder: it gives it a copy under <c>&lt;cacheRoot&gt;/&lt;content hash&gt;/</c>, whose name
/// changes with the folder's bytes (a <c>pwsh run.ps1 dotnet</c> while a server runs therefore gives the next call a fresh
/// copy). A copy is complete once its <c>.complete</c> marker is written, so a half-made copy is never loaded.
/// </summary>
internal sealed class HelperCache(string cacheRoot)
{
    /// <summary>The name of the extension inside the copy, and of the file <see cref="Prepare"/> returns.</summary>
    public const string ExtensionFileName = "godot_mcp_dotnet.gdextension";

    private const string CompleteMarker = ".complete";
    private const int HashCharacters = 16;
    private const int RetryIntervalMs = 20;
    private const int RetryBudgetMs = 2000;

    private bool _pruned;

    /// <summary>Where a published server and a checkout's build share copies: <c>%LOCALAPPDATA%\godot-mcp-cache\dotnet</c>.</summary>
    public static string DefaultRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "godot-mcp-cache", "dotnet");

    /// <summary>
    /// The absolute path of <see cref="ExtensionFileName"/> inside the copy of <paramref name="sourceDir"/> named by its
    /// content hash, making that copy when it is not already there and complete.
    /// </summary>
    public string Prepare(string sourceDir)
    {
        string hash = Hash(sourceDir);
        string destination = Path.Combine(cacheRoot, hash);
        if (!IsComplete(destination))
        {
            CopyInto(sourceDir, destination);
        }

        if (!_pruned)
        {
            _pruned = true;
            Prune(destination);
        }

        return Path.Combine(destination, ExtensionFileName);
    }

    /// <summary>
    /// The first <see cref="HashCharacters"/> lowercase hex characters of the SHA-256 over every file under
    /// <paramref name="sourceDir"/>, each contributing its relative path (with <c>/</c> separators, UTF-8) and then its bytes,
    /// in ordinal order of that path.
    /// </summary>
    public static string Hash(string sourceDir)
    {
        string root = Path.GetFullPath(sourceDir);
        string[] relativePaths =
        [
            .. Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
                .Order(StringComparer.Ordinal),
        ];
        byte[] buffer = new byte[64 * 1024];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string relative in relativePaths)
        {
            hasher.AppendData(Encoding.UTF8.GetBytes(relative));
            using FileStream stream = File.OpenRead(Path.Combine(root, relative));
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset())[..HashCharacters];
    }

    /// <summary>Whether the copy at <paramref name="destination"/> is whole: its <c>.complete</c> marker is written.</summary>
    internal static bool IsComplete(string destination) => File.Exists(Path.Combine(destination, CompleteMarker));

    /// <summary>Copies the tree into a temp folder beside the destination and moves it in, marker first.</summary>
    internal static void CopyInto(string sourceDir, string destination)
    {
        if (Directory.Exists(destination))
        {
            if (IsComplete(destination))
            {
                // Another server published the same copy while this call decided to make one; its copy serves as well as ours.
                return;
            }

            Retry(() => Delete(destination));
        }

        string temp = $"{destination}.tmp-{Guid.NewGuid():N}";
        CopyTree(sourceDir, temp);
        File.WriteAllBytes(Path.Combine(temp, CompleteMarker), []);
        try
        {
            Retry(() => MoveUnlessPublished(temp, destination));
        }
        catch (Exception e) when (IsFileSystemRefusal(e))
        {
            TryDelete(temp);
            throw new InvalidOperationException(
                $"The C# helper's copy could not be moved into place at {destination}: {e.Message}; "
                    + $"something (an antivirus scan?) held it for over {RetryBudgetMs / 1000} s.",
                e
            );
        }

        if (Directory.Exists(temp))
        {
            // Another server published the same copy while this one was being made; its copy serves as well as ours.
            TryDelete(temp);
        }
    }

    private static void MoveUnlessPublished(string temp, string destination)
    {
        if (!IsComplete(destination))
        {
            Directory.Move(temp, destination);
        }
    }

    /// <summary>
    /// Runs <paramref name="attempt"/> until it succeeds, retrying a file-system refusal every <see cref="RetryIntervalMs"/>
    /// for up to <see cref="RetryBudgetMs"/>: an on-write scan holds a just-written file for tens of milliseconds, and NTFS
    /// refuses to rename or delete its folder meanwhile. The refusal that outlasts the budget is thrown.
    /// </summary>
    private static void Retry(Action attempt)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                attempt();
                return;
            }
            catch (Exception e) when (IsFileSystemRefusal(e) && clock.ElapsedMilliseconds < RetryBudgetMs)
            {
                Thread.Sleep(RetryIntervalMs);
            }
        }
    }

    private static bool IsFileSystemRefusal(Exception e) => e is IOException or UnauthorizedAccessException;

    /// <summary>Deletes a temp copy, logging a refusal rather than throwing it: the next server's prune removes what is left.</summary>
    private static void TryDelete(string temp)
    {
        try
        {
            Delete(temp);
        }
        catch (Exception e) when (IsFileSystemRefusal(e))
        {
            // Logging may be gone if this runs while the server exits, so this goes straight to stderr like the prune.
            Console.Error.WriteLine($"godot-mcp: could not remove the C# helper's temp copy {temp}: {e.Message}");
        }
    }

    private static void CopyTree(string sourceDir, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(sourceDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>
    /// Deletes every entry under the cache root but <paramref name="keep"/>: the hashes of other builds, and the temp folders
    /// a failed copy left. A delete a running game's locked dlls refuse is logged and skipped, never thrown.
    /// </summary>
    private void Prune(string keep)
    {
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        string kept = Path.GetFullPath(keep);
        foreach (string entry in Directory.EnumerateFileSystemEntries(cacheRoot))
        {
            if (string.Equals(Path.GetFullPath(entry), kept, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Delete(entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging may be gone if this runs while the server exits, so this goes straight to stderr like the shutdown path.
                Console.Error.WriteLine($"godot-mcp: could not remove the stale C# helper copy {entry}: {e.Message}");
            }
        }
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }
    }
}
