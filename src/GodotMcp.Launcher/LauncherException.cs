namespace GodotMcp.Launcher;

/// <summary>A refusal the launcher writes as its one stderr line before it exits 1.</summary>
internal sealed class LauncherException : Exception
{
    public LauncherException() { }

    public LauncherException(string message)
        : base(message) { }

    public LauncherException(string message, Exception innerException)
        : base(message, innerException) { }
}
