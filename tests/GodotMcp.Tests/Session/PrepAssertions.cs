using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>The staleness and import checks as the prep makes them, and whether a folder sits inside a git repository.</summary>
internal static class PrepAssertions
{
    public static bool IsStale(string game) =>
        PrepScan.IsStale(PrepScan.AssemblyPath(game, "Game"), PrepScan.StampPath(game), game, PrepScan.Scan(game, NullLogger.Instance).BuildInputs);

    public static bool IsImportNeeded(string game)
    {
        ProjectFiles files = PrepScan.Scan(game, NullLogger.Instance);
        IReadOnlyDictionary<string, string> fingerprints = ImportFingerprints.Read(game, NullLogger.Instance);
        IReadOnlyList<string> missingSidecars = PrepScan.MissingSidecarSources(game, PrepScan.SourcesWithoutSidecars(game, files), fingerprints);
        return PrepScan.ImportNeeded(game, files, missingSidecars, fingerprints, NullLogger.Instance);
    }

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
