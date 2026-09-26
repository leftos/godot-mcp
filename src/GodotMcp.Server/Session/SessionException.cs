namespace GodotMcp.Server.Session;

/// <summary>A session operation that failed for a reason the caller can act on; the message says what and how.</summary>
internal sealed class SessionException : Exception
{
    public SessionException() { }

    public SessionException(string message)
        : base(message) { }

    public SessionException(string message, Exception innerException)
        : base(message, innerException) { }
}
