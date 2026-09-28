using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GodotMcp.Tests.Session;

/// <summary>The ceiling the ToolProcess tests run under, the line a child reports its grandchild on, and killing one.</summary>
internal static partial class ProcessProbe
{
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    [GeneratedRegex(@"grandchild:(\d+)")]
    public static partial Regex GrandchildLine();

    public static void KillIfRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
            // It is the temp folder's working directory until it has exited, which Kill does not wait for.
            process.WaitForExit(5000);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // It has already exited.
        }
    }

    public static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
