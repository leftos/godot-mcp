using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>
/// An editor session's output without the engine's <c>--script</c> editor noise. The lines are the engine's own, from a 4.7.2
/// editor started with <c>--editor --script</c> (the GMCP-407 spike's runA2 log).
/// </summary>
public sealed class EditorOutputNoiseTests
{
    private readonly List<string> _kept = [];
    private readonly EditorOutputNoise _noise;

    public EditorOutputNoiseTests() => _noise = new EditorOutputNoise(_kept.Add);

    [Fact]
    public void DropsTheProgressDialogErrorParameterCurrentWindowIsNullAndItsAtLine()
    {
        Feed("ERROR: Parameter \"current_window\" is null.", "   at: _reparent_and_show (editor/gui/progress_dialog.cpp:183)", "PROBE_PRINT after");

        Assert.Equal(["PROBE_PRINT after"], _kept);
    }

    [Fact]
    public void DropsTheGdscriptBacktraceUnderAProgressDialogError()
    {
        Feed(
            "ERROR: Parameter \"current_window\" is null.",
            "   at: _reparent_and_show (editor/gui/progress_dialog.cpp:183)",
            "   GDScript backtrace (most recent call first):",
            "       [0] _step_edit_save (X:/probes/editor_probe.gd:293)",
            "       [1] _run_step (X:/probes/editor_probe.gd:184)",
            "after the save"
        );

        Assert.Equal(["after the save"], _kept);
    }

    [Fact]
    public void KeepsAnOrdinaryErrorWithItsAtLineAndBacktrace()
    {
        string[] error =
        [
            "ERROR: PROBE_PUSH_ERROR from the probe",
            "   at: push_error (core/variant/variant_utility.cpp:1023)",
            "   GDScript backtrace (most recent call first):",
            "       [0] _step_ticker (X:/probes/editor_probe.gd:256)",
        ];

        Feed(error);

        Assert.Equal(error, _kept);
    }

    [Fact]
    public void DropsTheExitLeakReportFromRidAllocationsWereLeakedAtExitToTheEndOfTheStream()
    {
        Feed(
            "last line of the session",
            "ERROR: 9 RID allocations of type 'N5GLES312LightStorage11ShadowAtlasE' were leaked at exit.",
            "ERROR: 8 RID allocations of type 'N16RendererViewport8ViewportE' were leaked at exit.",
            "anything after the report starts"
        );

        Assert.Equal(["last line of the session"], _kept);
    }

    [Fact]
    public void DropsTheStderrLeakReportFromRidsOfTypeCanvasWereLeakedThroughObjectDbInstances()
    {
        Feed(
            "WARNING: 8 RIDs of type \"Canvas\" were leaked.",
            "   at: _free_rids (servers/rendering/renderer_canvas_cull.cpp:2735)",
            "ERROR: Texture with GL ID of 19: leaked 20 bytes.",
            "   at: ~Utilities (drivers/gles3/storage/utilities.cpp:82)",
            "WARNING: 246 ObjectDB instances were leaked at exit (run with `--verbose` for details).",
            "   at: cleanup (core/object/object.cpp:2536)"
        );

        Assert.Empty(_kept);
    }

    [Fact]
    public void AGlTextureLeakLineAloneStartsTheReport()
    {
        Feed("ERROR: Texture with GL ID of 490: leaked 131072 bytes.", "   at: ~Utilities (drivers/gles3/storage/utilities.cpp:82)");

        Assert.Empty(_kept);
    }

    [Fact]
    public void AProjectLineThatMentionsALeakIsKept()
    {
        Feed("ERROR: the inventory leaked 3 items at exit.", "next");

        Assert.Equal(["ERROR: the inventory leaked 3 items at exit.", "next"], _kept);
    }

    private void Feed(params string[] lines)
    {
        foreach (string line in lines)
        {
            _noise.Add(line);
        }
    }
}
