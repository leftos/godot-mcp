namespace GodotMcp.Dotnet.Core;

/// <summary>A handle id that is malformed, from before a restart, or no longer (or never) in the table.</summary>
public sealed class HandleException : Exception
{
    public HandleException() { }

    public HandleException(string message)
        : base(message) { }

    public HandleException(string message, Exception innerException)
        : base(message, innerException) { }
}
