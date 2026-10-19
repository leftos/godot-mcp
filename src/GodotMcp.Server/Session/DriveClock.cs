using System.Diagnostics;

namespace GodotMcp.Server.Session;

/// <summary>
/// The drive clock: one stopwatch for the whole server, started as the server starts, whose readings stamp when a drive's
/// steps began and ended, in milliseconds to 0.1 ms.
/// </summary>
internal static class DriveClock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    /// <summary>Starts the clock from zero; the server calls it once, as it starts.</summary>
    public static void Start() => Watch.Restart();

    /// <summary>The milliseconds since the server started, rounded to 0.1 ms.</summary>
    public static double NowMs => Math.Round(Watch.Elapsed.TotalMilliseconds, 1);
}
