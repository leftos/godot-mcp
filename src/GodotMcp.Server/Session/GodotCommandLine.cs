using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GodotMcp.Server.Session;

/// <summary>
/// What <c>run_project</c> asked for, with <see cref="ProjectPath"/> the project folder. With <see cref="Quiet"/> the window is
/// created unfocused, off-screen and click-through, with the Dummy audio driver; with
/// <see cref="ShutOutRealGamepads"/> it shuts the machine's real pads out of the game.
/// </summary>
internal sealed record LaunchRequest(
    string ProjectPath,
    string? Scene,
    IReadOnlyList<string> EngineArgs,
    IReadOnlyList<string> UserArgs,
    bool Quiet,
    bool ShutOutRealGamepads
);

/// <summary>Where the bridge dials and the token it proves itself with.</summary>
internal sealed record BridgeEndpoint(int Port, string Token);

/// <summary>Builds the Godot process for a run: <c>--path &lt;p&gt; [scene] &lt;engineArgs&gt; [-- &lt;userArgs&gt;]</c>.</summary>
internal static class GodotCommandLine
{
    public const string PortVariable = "GODOT_MCP_PORT";
    public const string TokenVariable = "GODOT_MCP_TOKEN";
    public const string QuietVariable = "GODOT_MCP_QUIET";
    public const string ShutOutRealGamepadsVariable = "GODOT_MCP_SHUT_OUT_REAL_GAMEPADS";

    /// <summary>
    /// A quiet run's audio driver. The Dummy driver still mixes on its own thread, so playback advances
    /// (4.7.2 <c>servers/audio/audio_driver_dummy.cpp</c> L49-51, L56-73), and plays nothing.
    /// </summary>
    public static readonly IReadOnlyList<string> QuietAudioDriverArgs = ["--audio-driver", "Dummy"];

    public static List<string> BuildArguments(LaunchRequest request)
    {
        List<string> arguments = ["--path", request.ProjectPath];
        if (!string.IsNullOrWhiteSpace(request.Scene))
        {
            arguments.Add(request.Scene);
        }

        // Godot keeps the last --audio-driver it reads (main.cpp L1234-1237 in 4.7.2), so one in EngineArgs wins.
        if (request.Quiet)
        {
            arguments.AddRange(QuietAudioDriverArgs);
        }

        arguments.AddRange(request.EngineArgs);
        if (request.UserArgs.Count > 0)
        {
            arguments.Add("--");
            arguments.AddRange(request.UserArgs);
        }

        return arguments;
    }

    /// <summary>
    /// The start info for a run. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, which quotes each one,
    /// so an argument with spaces reaches Godot whole. stdin is redirected so Godot never inherits the server's: that is
    /// the MCP pipe, and on Windows starting a child that inherits a pipe the server is blocked reading stalls the start
    /// until the read completes.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string godotPath, LaunchRequest request, BridgeEndpoint bridge)
    {
        ProcessStartInfo startInfo = new(godotPath)
        {
            WorkingDirectory = request.ProjectPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in BuildArguments(request))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment[PortVariable] = bridge.Port.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment[TokenVariable] = bridge.Token;
        SetFlag(startInfo, QuietVariable, request.Quiet);
        SetFlag(startInfo, ShutOutRealGamepadsVariable, request.ShutOutRealGamepads);
        return startInfo;
    }

    /// <summary>Sets the variable to 1 when the flag is on, and removes one the server's own environment passed down when off.</summary>
    private static void SetFlag(ProcessStartInfo startInfo, string variable, bool on)
    {
        if (on)
        {
            startInfo.Environment[variable] = "1";
        }
        else
        {
            startInfo.Environment.Remove(variable);
        }
    }
}
