namespace GodotMcp.Dotnet.Core;

/// <summary>
/// A call that cannot be made: its name is not a method it can call, a type argument names no one type, or no overload, or
/// more than one, takes the arguments given, in which case the message lists the candidates.
/// </summary>
public sealed class OverloadException : Exception
{
    public OverloadException() { }

    public OverloadException(string message)
        : base(message) { }

    public OverloadException(string message, Exception innerException)
        : base(message, innerException) { }
}
