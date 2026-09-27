namespace GodotMcp.Dotnet.Core;

/// <summary>A member path that does not parse, or a walk along one that fails; the message names the segment.</summary>
public sealed class MemberPathException : Exception
{
    public MemberPathException() { }

    public MemberPathException(string message)
        : base(message) { }

    public MemberPathException(string message, Exception innerException)
        : base(message, innerException) { }
}
