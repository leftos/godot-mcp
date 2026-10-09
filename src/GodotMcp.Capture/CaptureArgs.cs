using System.Globalization;

namespace GodotMcp.Capture;

/// <summary>
/// The helper's command line: the window by handle, an optional crop, the frame rate, the deadline file, the probe, and
/// the process whose audio goes to a named pipe. Parsing names the first problem and builds nothing else.
/// </summary>
internal sealed record CaptureArgs(nint Hwnd, PixelRect? Crop, int Fps, string? UntilFile, bool Probe, int? AudioPid, string? AudioPipe)
{
    public const int DefaultFps = 30;

    public const string Usage =
        "usage: godot-mcp-capture --hwnd <decimal> [--crop x,y,w,h] [--fps 30] (--until-file <path> | --probe)"
        + " [--audio-pid <pid> --audio-pipe <name>]\n"
        + "--until-file names a file holding one UTC instant in round-trip (\"o\") format; the run ends when it passes, and\n"
        + "the file may be rewritten while the run goes on. --crop is in the window capture's pixels and defaults to all of\n"
        + "it. --probe prints the frame size a run writes (the crop, an odd side padded to even) as WxH and exits.";

    private const string Flag = "--";

    /// <summary>Returns null, with the reason in <paramref name="error"/>, for an unknown, incomplete or contradictory command line.</summary>
    public static CaptureArgs? Parse(IReadOnlyList<string> args, out string? error)
    {
        Builder builder = new();
        for (int i = 0; i < args.Count; i++)
        {
            error = ReadArgument(builder, args, ref i);
            if (error is not null)
            {
                return null;
            }
        }
        CaptureArgs parsed = builder.Build();
        error = parsed.Refuse();
        return error is null ? parsed : null;
    }

    /// <summary>Applies the argument at <paramref name="index"/>, and the value after it when it takes one.</summary>
    private static string? ReadArgument(Builder builder, IReadOnlyList<string> args, ref int index)
    {
        string name = args[index];
        if (name == "--probe")
        {
            builder.Probe = true;
            return null;
        }
        if (!Builder.TakesValue(name))
        {
            return $"unknown argument '{name}'.";
        }
        if (index + 1 >= args.Count || args[index + 1].StartsWith(Flag, StringComparison.Ordinal))
        {
            return $"{name} needs a value.";
        }
        index++;
        return builder.Set(name, args[index]);
    }

    private string? Refuse()
    {
        if (Hwnd == nint.Zero)
        {
            return "--hwnd is required.";
        }
        if (!Probe && UntilFile is null)
        {
            return "--until-file is required unless --probe is given.";
        }
        return RefuseAudioPairing();
    }

    private string? RefuseAudioPairing()
    {
        if (AudioPipe is not null && AudioPid is null)
        {
            return "--audio-pipe needs --audio-pid.";
        }
        // stdout carries the video, so the audio needs a pipe; a probe only activates the audio.
        return AudioPid is not null && AudioPipe is null && !Probe ? "--audio-pid needs --audio-pipe." : null;
    }

    private sealed class Builder
    {
        private static readonly string[] ValueNames = ["--hwnd", "--crop", "--fps", "--until-file", "--audio-pid", "--audio-pipe"];

        private nint _hwnd;
        private PixelRect? _crop;
        private int _fps = DefaultFps;
        private string? _untilFile;
        private int? _audioPid;
        private string? _audioPipe;

        public bool Probe { get; set; }

        public static bool TakesValue(string name) => ValueNames.Contains(name, StringComparer.Ordinal);

        /// <summary>Sets the value of <paramref name="name"/>, one of <see cref="ValueNames"/>; returns why a value is refused, else null.</summary>
        public string? Set(string name, string value) =>
            name switch
            {
                "--hwnd" => SetHwnd(value),
                "--crop" => SetCrop(value),
                "--fps" => SetFps(value),
                "--until-file" => SetUntilFile(value),
                "--audio-pid" => SetAudioPid(value),
                "--audio-pipe" => SetAudioPipe(value),
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not an argument that takes a value"),
            };

        public CaptureArgs Build() => new(_hwnd, _crop, _fps, _untilFile, Probe, _audioPid, _audioPipe);

        private string? SetHwnd(string value)
        {
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long handle) || handle == 0)
            {
                return $"--hwnd takes a window handle as a positive decimal, not '{value}'.";
            }
            _hwnd = (nint)handle;
            return null;
        }

        private string? SetCrop(string value)
        {
            if (!PixelRect.TryParse(value, out PixelRect crop))
            {
                return $"--crop takes x,y,w,h: four decimals, the corner 0 or more and the size above 0, not '{value}'.";
            }
            _crop = crop;
            return null;
        }

        private string? SetFps(string value)
        {
            if (!TryReadPositive(value, out int fps))
            {
                return $"--fps takes a positive decimal, not '{value}'.";
            }
            _fps = fps;
            return null;
        }

        private string? SetUntilFile(string value)
        {
            _untilFile = value;
            return null;
        }

        private string? SetAudioPid(string value)
        {
            if (!TryReadPositive(value, out int pid))
            {
                return $"--audio-pid takes a process id as a positive decimal, not '{value}'.";
            }
            _audioPid = pid;
            return null;
        }

        private string? SetAudioPipe(string value)
        {
            _audioPipe = value;
            return null;
        }

        private static bool TryReadPositive(string value, out int parsed) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed > 0;
    }
}
