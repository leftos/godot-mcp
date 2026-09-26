using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GodotMcp.Server.Session;

/// <summary>What <c>run_project</c> asked for, with <see cref="ProjectPath"/> the project folder.</summary>
internal sealed record LaunchRequest(
    string ProjectPath,
    string? Scene,
    IReadOnlyList<string> EngineArgs,
    IReadOnlyList<string> UserArgs,
    bool Background
);

/// <summary>Where the bridge dials and the token it proves itself with.</summary>
internal sealed record BridgeEndpoint(int Port, string Token);

/// <summary>Builds the Godot process for a run: <c>--path &lt;p&gt; [scene] &lt;engineArgs&gt; [-- &lt;userArgs&gt;]</c>.</summary>
internal static class GodotCommandLine
{
    public const string PortVariable = "GODOT_MCP_PORT";
    public const string TokenVariable = "GODOT_MCP_TOKEN";
    public const string BackgroundVariable = "GODOT_MCP_BACKGROUND";

    public static List<string> BuildArguments(LaunchRequest request)
    {
        List<string> arguments = ["--path", request.ProjectPath];
        if (!string.IsNullOrWhiteSpace(request.Scene))
        {
            arguments.Add(request.Scene);
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
        if (request.Background)
        {
            startInfo.Environment[BackgroundVariable] = "1";
        }
        else
        {
            startInfo.Environment.Remove(BackgroundVariable);
        }

        return startInfo;
    }
}
