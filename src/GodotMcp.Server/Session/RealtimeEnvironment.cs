using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>Where ffmpeg was looked for: its path, or the <c>FFMPEG_PATH</c> that names no file, or neither.</summary>
internal sealed record FfmpegLookup(string? Path, string? ConfiguredButMissing);

/// <summary>
/// A process a real-time recording drives: ffmpeg, whose stdin it writes, or the capture helper, whose stdout it reads.
/// Disposing it lets go of the process; it never kills it.
/// </summary>
internal interface IRealtimeProcess : IDisposable
{
    /// <summary>The process's stdin, for a request that keeps it.</summary>
    Stream StandardInput { get; }

    /// <summary>The process's stdout, for a request that pipes it.</summary>
    Stream StandardOutput { get; }

    /// <summary>Waits for the exit under the request's ceiling, backstop and stall limit; called once per process.</summary>
    Task<ToolProcessResult> WaitAsync(CancellationToken cancellationToken);

    /// <summary>Kills the process tree and waits until Windows lets go of it.</summary>
    Task KillAsync();
}

/// <summary>A <see cref="ToolProcess.Running"/> as a real-time recording drives it.</summary>
internal sealed class ToolRealtimeProcess(ToolProcess.Running run) : IRealtimeProcess
{
    public Stream StandardInput => run.StandardInput;

    public Stream StandardOutput => run.StandardOutput;

    public Task<ToolProcessResult> WaitAsync(CancellationToken cancellationToken) => ToolProcess.WaitAsync(run, cancellationToken);

    public Task KillAsync() => run.KillAsync();

    public void Dispose() => run.Dispose();
}

/// <summary>
/// What a real-time recording finds and reads on the machine, and how it starts and runs its processes: ffmpeg and the capture
/// helper, the game window's ancestry and rectangles, the clock, and the encoder probes, cached for the registry's life. The
/// server's is <see cref="System"/>; a test sets fakes on <see cref="SessionRegistry.Realtime"/>.
/// </summary>
internal sealed class RealtimeEnvironment
{
    /// <summary>Where ffmpeg is: <c>FFMPEG_PATH</c>, else <c>PATH</c>.</summary>
    public required Func<FfmpegLookup> FindFfmpeg { get; init; }

    /// <summary>The capture helper's path, or null when it is not installed.</summary>
    public required Func<string?> FindCaptureHelper { get; init; }

    /// <summary>Where the capture helper belongs beside the server, as the refusal of a missing one names it.</summary>
    public required string ExpectedCaptureHelper { get; init; }

    /// <summary>Whether a window handle is a top-level window rather than one embedded in another.</summary>
    public required Func<long, bool> IsTopLevel { get; init; }

    /// <summary>A live window's rectangles and minimized state (<see cref="WindowRect.Read"/>).</summary>
    public required Func<long, WindowReading> ReadWindow { get; init; }

    /// <summary>The time the deadline file, a recording's age and its clip's length are read on.</summary>
    public required TimeProvider Time { get; init; }

    /// <summary>The clock the processes' ceilings and the wait for the first frame run on.</summary>
    public required LoadClock Clock { get; init; }

    /// <summary>Starts a process the recording drives while it runs: the helper's probe and capture, and ffmpeg's encode.</summary>
    public required Func<ToolProcessRequest, IRealtimeProcess> Start { get; init; }

    /// <summary>Runs a process to its end: an encoder probe, or the remux.</summary>
    public required Func<ToolProcessRequest, CancellationToken, Task<ToolProcessResult>> Run { get; init; }

    /// <summary>The encoder probes, run once each for the environment's life.</summary>
    public EncoderProbeCache Encoders { get; } = new();

    /// <summary>The machine's: ffmpeg and the helper where the server finds them, the window read through Win32, real processes.</summary>
    public static RealtimeEnvironment System(ILogger logger) =>
        new()
        {
            FindFfmpeg = () => new FfmpegLookup(Installation.FindFfmpeg(out string? configuredButMissing), configuredButMissing),
            FindCaptureHelper = Installation.FindCaptureHelper,
            ExpectedCaptureHelper = Path.Combine(AppContext.BaseDirectory, "capture", Installation.CaptureHelperFileName),
            IsTopLevel = WindowRect.IsTopLevel,
            ReadWindow = WindowRect.Read,
            Time = TimeProvider.System,
            Clock = LoadClock.Shared,
            Start = request => new ToolRealtimeProcess(ToolProcess.Start(request, logger)),
            Run = (request, cancellationToken) => ToolProcess.RunAsync(request, logger, cancellationToken),
        };
}
