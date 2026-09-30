namespace GodotMcp.Tests.Dotnet.Marks;

/// <summary>
/// The same mark a game declares in its own namespace, so a read that matches the attribute by name alone finds this
/// one too.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GodotMcpToolAttribute(string description) : Attribute
{
    public string Description { get; } = description;

    public string? Name { get; init; }
}

/// <summary>A class marked with the attribute from this namespace.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "The method exists to carry a mark the tests read, not to run."
)]
public sealed class MarkedBench
{
    [GodotMcpTool("Clears the room of every enemy.", Name = "clear_room")]
    public bool Clear(int room) => room > 0;
}
