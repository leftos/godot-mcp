namespace GodotMcp.Server.Session;

/// <summary>A session operation that failed for a reason the caller can act on; the message says what and how.</summary>
internal class SessionException : Exception
{
    public SessionException() { }

    public SessionException(string message)
        : base(message) { }

    public SessionException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// A start whose Godot exited with <see cref="GodotSession.BootCrashExitCode"/> before it was sent anything, so it can be
/// started once more (<see cref="SessionRegistry.RetryBootCrashOnce"/>); caught as any other <see cref="SessionException"/>.
/// </summary>
internal sealed class BootCrashException : SessionException
{
    public BootCrashException() { }

    public BootCrashException(string message)
        : base(message) { }

    public BootCrashException(string message, Exception innerException)
        : base(message, innerException) { }
}
