using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace GodotMcp.Server.Session;

/// <summary>
/// What <c>run_project</c> asked for, with <see cref="ProjectPath"/> the project folder. With <see cref="Quiet"/> the window is
/// created unfocused, off-screen and click-through, with the Dummy audio driver; with
/// <see cref="ShutOutRealGamepads"/> it shuts the machine's real pads out of the game. With <see cref="Prepare"/> (prepare
/// "auto") a stale C# assembly is built and missing imports are run before the launch. With <see cref="Record"/> each start
/// of the run is recorded with Godot's Movie Maker.
/// </summary>
internal sealed record LaunchRequest(
    string ProjectPath,
    string? Scene,
    IReadOnlyList<string> EngineArgs,
    IReadOnlyList<string> UserArgs,
    bool Quiet,
    bool ShutOutRealGamepads,
    bool Prepare
)
{
    /// <summary>Whether the run is recorded from launch with Godot's Movie Maker.</summary>
    public bool Record { get; init; }

    /// <summary>Whether the recording's clips drop frames identical to the one before, and their audio.</summary>
    public bool DropIdle { get; init; }

    /// <summary>
    /// Whether the run is preview_scene's: the bridge pauses the game before its scene's first frame and frames a 3D scene
    /// that has no current camera.
    /// </summary>
    public bool Preview { get; init; }
}

/// <summary>Where the bridge dials and the token it proves itself with.</summary>
internal sealed record BridgeEndpoint(int Port, string Token);

/// <summary>Builds the Godot process for a run: <c>--path &lt;p&gt; [scene] &lt;engineArgs&gt; [-- &lt;userArgs&gt;]</c>.</summary>
internal static partial class GodotCommandLine
{
    public const string PortVariable = "GODOT_MCP_PORT";
    public const string TokenVariable = "GODOT_MCP_TOKEN";
    public const string QuietVariable = "GODOT_MCP_QUIET";
    public const string ShutOutRealGamepadsVariable = "GODOT_MCP_SHUT_OUT_REAL_GAMEPADS";
    public const string PreviewVariable = "GODOT_MCP_PREVIEW";

    /// <summary>
    /// The window size the engine arguments' --resolution asks for, as <c>WIDTHxHEIGHT</c>. Windows holds a window created larger
    /// than the desktop to the desktop's size (4.7.2 <c>display_server_windows.cpp</c> <c>_create_window</c> L7175-7257), and a
    /// resize after start is not held (<c>window_set_size</c>'s <c>MoveWindow</c>, L2492-2520), so the bridge resizes the window
    /// to it once the game has started.
    /// </summary>
    public const string WindowSizeVariable = "GODOT_MCP_WINDOW_SIZE";

    /// <summary>
    /// The largest --resolution side: 16384 pixels, the largest 2D texture D3D12 and common Vulkan GPUs allow, whose square is
    /// Godot's <c>Image::MAX_PIXELS</c> (4.7.2 <c>core/io/image.h</c> L71), the most a screenshot of the window can hold.
    /// </summary>
    public const int MaxWindowSide = 16384;

    private const string ResolutionFlag = "--resolution";

    /// <summary>Set for a run started on the server's hidden desktop, where the bridge leaves a quiet window at (0, 0).</summary>
    public const string HiddenDesktopVariable = "GODOT_MCP_HIDDEN_DESKTOP";

    /// <summary>Set for a recording run to <see cref="MovieFramesPerSecond"/>: the bridge plays gesture durations in its movie frames.</summary>
    public const string MovieFpsVariable = "GODOT_MCP_MOVIE_FPS";

    /// <summary>
    /// Set to "1" for every headless run: its <c>--script</c> run reads a live session's override.cfg (4.7.2 <c>main.cpp</c>
    /// L2107) and so loads the bridge, which then stays off without looking for a server to dial.
    /// </summary>
    public const string OffVariable = "GODOT_MCP_OFF";

    /// <summary>Whether a run starts on the server's hidden desktop: a quiet run on Windows.</summary>
    [SupportedOSPlatformGuard("windows")]
    public static bool UsesHiddenDesktop(bool quiet) => quiet && OperatingSystem.IsWindows();

    /// <summary>
    /// A quiet run's audio driver. The Dummy driver still mixes on its own thread, so playback advances
    /// (4.7.2 <c>servers/audio/audio_driver_dummy.cpp</c> L49-51, L56-73), and plays nothing.
    /// </summary>
    public static readonly IReadOnlyList<string> QuietAudioDriverArgs = ["--audio-driver", "Dummy"];

    /// <summary>A recording's frame rate: every written frame advances game time by 1/60 s.</summary>
    public const int MovieFramesPerSecond = 60;

    /// <summary>
    /// A recording's cap: 10 minutes of frames. --quit-after counts main-loop iterations (4.7.2 main.cpp L1792-1794,
    /// compared with the process frame count at L5157), and the movie writer adds one frame per iteration (L5149-5151), so
    /// it is the number of frames written (command_line_tutorial.rst L127).
    /// </summary>
    public const int MaxMovieFrames = MovieFramesPerSecond * 60 * 10;

    /// <summary>What refuses a recording of a headless run.</summary>
    public const string HeadlessRecordingRefusal =
        "options.record cannot record a --headless run: the headless renderer draws nothing. Drop --headless; a quiet run is already hidden.";

    /// <summary>What refuses a recording on the headless display driver, which --headless selects.</summary>
    public const string HeadlessDisplayRecordingRefusal =
        "options.record cannot record a --display-driver headless run: the headless renderer draws nothing. Drop --display-driver "
        + "headless; a quiet run is already hidden.";

    /// <summary>What refuses options.dropIdle on a run that does not record.</summary>
    public const string DropIdleWithoutRecordRefusal =
        "options.dropIdle drops the idle frames of a recording's clips, and this run does not record. Add options.record: true, or "
        + "drop options.dropIdle.";

    /// <summary>
    /// The engine arguments a recording sets itself, each with its refusal: Godot keeps the last value it reads, so one in
    /// the engine arguments would silently replace the recording's.
    /// </summary>
    private static readonly (string Flag, string Refusal)[] RecordingOwnedFlags =
    [
        (
            "--fixed-fps",
            "options.record sets --fixed-fps itself (60, which record_mark's frames and the clip cuts rely on); drop it from "
                + "engineArgs or godot-mcp.json."
        ),
        (
            "--write-movie",
            "options.record sets --write-movie itself (the movie under .godot/godot-mcp/recordings/, which stop_project finalises "
                + "and cuts); drop it from engineArgs or godot-mcp.json."
        ),
        ("--quit-after", "options.record sets --quit-after itself (36000 frames, the 10 minute cap); drop it from engineArgs or godot-mcp.json."),
    ];

    /// <summary>
    /// The launch's arguments. <paramref name="moviePath"/> is the file a recording run writes, required when
    /// <see cref="LaunchRequest.Record"/> is set and ignored otherwise.
    /// </summary>
    /// <exception cref="ArgumentException">The request records and <paramref name="moviePath"/> is null.</exception>
    public static List<string> BuildArguments(LaunchRequest request, string? moviePath)
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

        if (request.Record)
        {
            arguments.AddRange(MovieArguments(moviePath ?? throw new ArgumentException("A recording run needs a movie path.", nameof(moviePath))));
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
    /// Refuses a recording the engine cannot make: with --headless the dummy renderer has no viewport texture for the movie
    /// writer to read. Refuses idle dropping on a run that does not record.
    /// </summary>
    /// <exception cref="SessionException">
    /// The request records and its engine arguments hold --headless or a flag the recording sets; or it drops idle frames and
    /// does not record.
    /// </exception>
    public static void RefuseUnrecordable(LaunchRequest request)
    {
        if (!request.Record)
        {
            if (request.DropIdle)
            {
                throw new SessionException(DropIdleWithoutRecordRefusal);
            }

            return;
        }

        IReadOnlyList<string> engineArgs = request.EngineArgs;
        if (engineArgs.Contains("--headless"))
        {
            throw new SessionException(HeadlessRecordingRefusal);
        }

        if (engineArgs.Index().Any(entry => entry.Item == "--display-driver" && engineArgs.ElementAtOrDefault(entry.Index + 1) == "headless"))
        {
            throw new SessionException(HeadlessDisplayRecordingRefusal);
        }

        foreach ((string flag, string refusal) in RecordingOwnedFlags)
        {
            if (engineArgs.Contains(flag))
            {
                throw new SessionException(refusal);
            }
        }
    }

    /// <summary>
    /// The window size the engine arguments ask for: the last --resolution's, since Godot reads each one and keeps the last
    /// (4.7.2 main.cpp L1438-1461); null when there is none. Every --resolution is checked, since Godot aborts on any it cannot read.
    /// </summary>
    /// <exception cref="SessionException">
    /// A --resolution has no value, its value is not WIDTHxHEIGHT, or a side is outside 1 to <see cref="MaxWindowSide"/>.
    /// </exception>
    public static WindowSize? RequestedWindowSize(IReadOnlyList<string> engineArgs)
    {
        WindowSize? size = null;
        for (int index = 0; index < engineArgs.Count; index++)
        {
            if (engineArgs[index] == ResolutionFlag)
            {
                size = ParseResolution(engineArgs.ElementAtOrDefault(index + 1));
            }
        }

        return size;
    }

    /// <summary>What a launch result says when the game's window is not the size --resolution asked for; null when it is.</summary>
    public static string? DescribeWindowMismatch(WindowSize? asked, WindowSize? window) =>
        asked is null || window is null || asked == window
            ? null
            : $"--resolution asked for a {asked} window and the game's window is {window}: the system did not give it the size asked for, "
                + "so screenshots and input work in the window it has.";

    private static WindowSize ParseResolution(string? value)
    {
        if (value is null)
        {
            throw new SessionException("--resolution is the last engine argument and has no WIDTHxHEIGHT after it, e.g. 1280x720.");
        }

        Match match = ResolutionValue().Match(value);
        if (!match.Success)
        {
            throw new SessionException(
                $"--resolution is \"{value}\"; it must be WIDTHxHEIGHT in pixels, with a lowercase x and no spaces, e.g. 1280x720."
            );
        }

        int width = ReadSide(match.Groups[1].Value);
        int height = ReadSide(match.Groups[2].Value);
        if (width is < 1 or > MaxWindowSide || height is < 1 or > MaxWindowSide)
        {
            throw new SessionException(
                $"--resolution {value} has a side outside 1 to {MaxWindowSide} pixels, the largest window Godot can draw and screenshot; "
                    + "pick a size within it."
            );
        }

        return new WindowSize(width, height);
    }

    /// <summary>A side's digits as a number; one too large for an int reads as <see cref="int.MaxValue"/>, which is out of range.</summary>
    private static int ReadSide(string digits) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int side) ? side : int.MaxValue;

    [GeneratedRegex(@"\A([0-9]+)x([0-9]+)\z")]
    private static partial Regex ResolutionValue();

    /// <summary>
    /// Movie Maker from launch: --write-movie picks the writer by the file's extension and forces --fixed-fps 60 unless one
    /// is given (4.7.2 main.cpp L1953-1971), which is passed anyway so the rate is explicit; --quit-after caps the frames.
    /// </summary>
    private static string[] MovieArguments(string moviePath) =>
        [
            "--write-movie",
            moviePath,
            "--fixed-fps",
            MovieFramesPerSecond.ToString(CultureInfo.InvariantCulture),
            "--quit-after",
            MaxMovieFrames.ToString(CultureInfo.InvariantCulture),
        ];

    /// <summary>
    /// The start info for a run. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, which quotes each one,
    /// so an argument with spaces reaches Godot whole. stdin is redirected so Godot never inherits the server's: that is
    /// the MCP pipe, and on Windows starting a child that inherits a pipe the server is blocked reading stalls the start
    /// until the read completes.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string godotPath, LaunchRequest request, BridgeEndpoint bridge, string? moviePath)
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
        foreach (string argument in BuildArguments(request, moviePath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment[PortVariable] = bridge.Port.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment[TokenVariable] = bridge.Token;
        SetFlag(startInfo, QuietVariable, request.Quiet);
        SetFlag(startInfo, ShutOutRealGamepadsVariable, request.ShutOutRealGamepads);
        SetFlag(startInfo, PreviewVariable, request.Preview);
        SetFlag(startInfo, HiddenDesktopVariable, UsesHiddenDesktop(request.Quiet));
        if (request.Record)
        {
            startInfo.Environment[MovieFpsVariable] = MovieFramesPerSecond.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            startInfo.Environment.Remove(MovieFpsVariable);
        }

        if (RequestedWindowSize(request.EngineArgs) is { } size)
        {
            startInfo.Environment[WindowSizeVariable] = size.ToString();
        }
        else
        {
            startInfo.Environment.Remove(WindowSizeVariable);
        }

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
