using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>Which lines of an import's log make the prep run it a second time, bare or behind a gate's stamp.</summary>
public sealed class ImportErrorLineTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("ERROR: Cannot open file", true)]
    [InlineData("2026-11-09T10:00:00+00:00 err ERROR: Cannot open file", true)]
    [InlineData("2026-01-01T00:00:00 out ERROR: x", true)]
    [InlineData("SCRIPT ERROR: x", false)]
    [InlineData("2026-01-01T00:00:00 out SCRIPT ERROR: x", false)]
    [InlineData("2026-01-01T00:00:00 gat gate: FAILED (status 1) ERROR: x", false)]
    [InlineData("2026-01-01T00:00:00 out note: ERROR: in the middle", false)]
    [InlineData("   at: _foo (editor/x.cpp:1)", false)]
    [InlineData("[12:00:01] ERROR: x", false)]
    public void TheLineIsAnImportErrorOnlyWhenTheEngineTextStartsIt(string line, bool expected) =>
        Assert.Equal(expected, ProjectPrep.IsImportError(line));

    [Fact]
    public void SecondPassReasonReadsAStampedLog()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectFiles files = PrepScan.Scan(game, NullLogger.Instance);
        string log = _temp.Combine("import.log");
        File.WriteAllText(log, "2026-01-01T00:00:00 err ERROR: x\nGodot Engine v4.7.2.stable\n");

        Assert.Equal("its first pass logged errors", ProjectPrep.SecondPassReason(log, null, game, files));
    }

    [Fact]
    public void SecondPassReasonIgnoresAScriptErrorLine()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectFiles files = PrepScan.Scan(game, NullLogger.Instance);
        string log = _temp.Combine("import.log");
        File.WriteAllText(log, "SCRIPT ERROR: x\nGodot Engine v4.7.2.stable\n");

        Assert.Null(ProjectPrep.SecondPassReason(log, null, game, files));
    }
}
