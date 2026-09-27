namespace GodotMcp.Dotnet.Core;

/// <summary>
/// A JSON value that does not fit its target type. <see cref="Reason"/> says what did not fit and <see cref="Location"/>
/// where, as a path of keys and indexes (empty at the top level); the message joins them as <c>reason at location</c>.
/// </summary>
public sealed class ValueConversionException : Exception
{
    public ValueConversionException() => Reason = "";

    public ValueConversionException(string message)
        : base(message) => Reason = message;

    public ValueConversionException(string message, Exception innerException)
        : base(message, innerException) => Reason = message;

    private ValueConversionException(string reason, string location)
        : base($"{reason} at {location}")
    {
        Reason = reason;
        Location = location;
    }

    public string Reason { get; }

    public string Location { get; } = "";

    /// <summary>The same failure one level further out: <paramref name="segment"/> is a key or an <c>[index]</c>.</summary>
    internal ValueConversionException Within(string segment)
    {
        string location =
            Location.Length == 0 ? segment
            : Location.StartsWith('[') ? segment + Location
            : $"{segment}.{Location}";
        return new ValueConversionException(Reason, location);
    }
}
