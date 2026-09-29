using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>The staleness and import checks as the prep makes them, and whether a folder sits inside a git repository.</summary>
internal static class PrepAssertions
{
    public static bool IsStale(string game) =>
        PrepScan.IsStale(PrepScan.AssemblyPath(game, "Game"), PrepScan.StampPath(game), PrepScan.Scan(game, NullLogger.Instance).BuildInputs);

    public static bool IsImportNeeded(string game) => PrepScan.ImportNeeded(game, PrepScan.Scan(game, NullLogger.Instance), NullLogger.Instance);

    public static bool IsInsideGit(string folder)
    {
        try
        {
            Git.Run(folder, "rev-parse", "--show-toplevel");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
