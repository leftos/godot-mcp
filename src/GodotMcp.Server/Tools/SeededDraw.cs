namespace GodotMcp.Server.Tools;

/// <summary>
/// The draw stress_input picks its pool entries with: SplitMix64, whose sequence the algorithm's own specification fixes
/// rather than a runtime's, so one seed replays one sequence of inputs on any .NET version. An index comes from Lemire's
/// multiply-shift, which has none of the bias a remainder would put on the low indices.
/// </summary>
internal sealed class SeededDraw(ulong seed)
{
    private const ulong Increment = 0x9E3779B97F4A7C15;
    private const ulong MixFirst = 0xBF58476D1CE4E5B9;
    private const ulong MixSecond = 0x94D049BB133111EB;

    private ulong _state = seed;

    /// <summary>The next value of the sequence.</summary>
    public ulong Next()
    {
        _state += Increment;
        ulong mixed = _state;
        mixed = (mixed ^ (mixed >> 30)) * MixFirst;
        mixed = (mixed ^ (mixed >> 27)) * MixSecond;
        return mixed ^ (mixed >> 31);
    }

    /// <summary>A uniform index in 0 to <paramref name="bound"/> - 1.</summary>
    public int NextIndex(int bound)
    {
        ulong width = (ulong)bound;
        UInt128 product = (UInt128)Next() * width;
        // The values below (2^64 mod width) land in a short last bucket; drawing again for them drops the bias.
        ulong remainder = unchecked(0UL - width) % width;
        while ((ulong)product < remainder)
        {
            product = (UInt128)Next() * width;
        }

        return (int)(product >> 64);
    }
}
