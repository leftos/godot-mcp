using System.Diagnostics;
using GodotMcp.Server.Session;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>The launch request, script and process check the session lifecycle and stop tests share for their InputProbe runs.</summary>
internal static class ProbeRuns
{
    // Starts a ping that outlives the game and returns its pid.
    public const string LingeringChildScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\treturn OS.create_process(\"ping\", [\"-n\", \"10\", \"127.0.0.1\"])\n";

    private static readonly TimeSpan ChildExitWait = TimeSpan.FromSeconds(5);

    /// <summary>A launch of the probe at <paramref name="directory"/> that prepares it first, quiet unless told otherwise.</summary>
    public static LaunchRequest Request(string directory, string[]? userArgs, bool quiet, bool shutOutRealGamepads) =>
        new(directory, null, [], userArgs ?? [], quiet, shutOutRealGamepads, Prepare: true);

    /// <summary>Whether the process is gone, or goes within five seconds.</summary>
    public static async Task<bool> ProcessGoneAsync(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            return await ProcessExit.WaitUntilGoneAsync(process, ChildExitWait);
        }
    }
}
