using System.Diagnostics;

namespace GodotMcp.TestSupport;

/// <summary>
/// A fresh folder under the system temp directory, deleted on dispose. It is outside every git repository, which the
/// git tests rely on; the repository's own <c>.tmp/</c> would not be.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    private const int RetryIntervalMs = 20;
    private const int RetryBudgetMs = 2000;

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "godot-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>
    /// Run after a delete attempt a holder refused and before the next one: a test holding a file releases it here, so
    /// the retry it proves is the folder's own and not whatever the thread pool does with a scheduled release.
    /// </summary>
    public Action AfterRefusedAttempt { get; set; } = static () => { };

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Deletes the folder, retrying for up to 2 s one that another process still holds.</summary>
    public void Dispose()
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
    /// a scanner holds a fresh file, so a refusal here is retried by <see cref="Dispose"/>: NTFS refuses to delete a
    /// folder whose file is held meanwhile.
    /// </summary>
    private void DeleteAttempt()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        // git writes its object files read-only, which Directory.Delete refuses on Windows.
        foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Path, recursive: true);
    }

    private static bool IsFileSystemRefusal(Exception e) => e is IOException or UnauthorizedAccessException;
}
