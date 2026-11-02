using System.Text.RegularExpressions;

namespace GodotMcp.Server.Session;

/// <summary>
/// One captured stream of an editor session's output, without the engine noise every <c>--script</c> editor prints and that
/// says nothing about the project. The editor makes a SceneTree before it reads <c>--script</c> and never frees it (4.7.2
/// <c>main/main.cpp</c> L4361-4363, L4388-4389), so each progress dialog logs
/// <c>ERROR: Parameter "current_window" is null.</c> from <c>editor/gui/progress_dialog.cpp</c> L183 (the only
/// <c>ERR_FAIL_NULL(current_window)</c> in the engine), and the exit prints a leak report. The error line is dropped with its
/// <c>at:</c> line and any GDScript backtrace under it; the leak report from its first line to the end of the stream, since the
/// engine prints it last. One instance per stream, since a line is told apart by the ones before it.
/// </summary>
internal sealed partial class EditorOutputNoise(Action<string> next)
{
    /// <summary>The progress dialog's error line, exactly as the engine prints it.</summary>
    internal const string ProgressDialogError = "ERROR: Parameter \"current_window\" is null.";

    /// <summary>Where the progress dialog's error comes from, as its <c>at:</c> line names it.</summary>
    internal const string ProgressDialogSite = "progress_dialog.cpp:183";

    private bool _inDetail;
    private bool _inLeakReport;

    /// <summary>Passes <paramref name="line"/> on unless it is noise.</summary>
    public void Add(string line)
    {
        if (!IsNoise(line))
        {
            next(line);
        }
    }

    private bool IsNoise(string line)
    {
        if (_inLeakReport || LeakReportLine().IsMatch(line))
        {
            _inLeakReport = true;
            return true;
        }

        if (line == ProgressDialogError || line.Contains(ProgressDialogSite, StringComparison.Ordinal))
        {
            _inDetail = true;
            return true;
        }

        _inDetail = _inDetail && DetailLine().IsMatch(line);
        return _inDetail;
    }

    /// <summary>
    /// The first line of each kind the exit's leak report starts with, from the engine's own text: RID allocations, canvas RIDs,
    /// GL textures and ObjectDB instances.
    /// </summary>
    [GeneratedRegex(
        @"\A(ERROR: \d+ RID allocations of type '.*' were leaked at exit\."
            + @"|WARNING: \d+ RIDs of type "".*"" were leaked\."
            + @"|ERROR: Texture with GL ID of \d+: leaked \d+ bytes\."
            + @"|WARNING: \d+ ObjectDB instances were leaked at exit)"
    )]
    private static partial Regex LeakReportLine();

    /// <summary>A line the engine prints under an error: its <c>at:</c> line, a backtrace's heading or one of its frames.</summary>
    [GeneratedRegex(@"\A(   at: |   GDScript backtrace |\s+\[\d+\] )")]
    private static partial Regex DetailLine();
}
