namespace GodotMcp.TestSupport;

/// <summary>
/// A fresh folder under the system temp directory, deleted on dispose. It is outside every git repository, which the
/// git tests rely on; the repository's own <c>.tmp/</c> would not be.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "godot-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
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
}
