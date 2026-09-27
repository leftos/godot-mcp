namespace GodotMcp.Dotnet.Core;

/// <summary>No overload, or more than one, takes the arguments given; the message lists the candidates.</summary>
public sealed class OverloadException : Exception
{
    public OverloadException() { }

    public OverloadException(string message)
        : base(message) { }

    public OverloadException(string message, Exception innerException)
        : base(message, innerException) { }
}
