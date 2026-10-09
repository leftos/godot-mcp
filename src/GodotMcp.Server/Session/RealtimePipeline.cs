using System.ComponentModel;
using System.Text;

namespace GodotMcp.Server.Session;

/// <summary>How one start attempt's wait for ffmpeg to open its encoder ended.</summary>
internal enum FirstFrameEnd
{
    /// <summary>ffmpeg opened its encoder on the first frame: its first progress report that goes on came.</summary>
    Opened,

    /// <summary>The capture helper exited, or its frames ended, before it.</summary>
    HelperEnded,

    /// <summary>ffmpeg exited, or stopped taking frames, before it: the encoder failed at its start.</summary>
    EncoderFailed,

    /// <summary>Neither happened within the first-frame allowance.</summary>
    TimedOut,
}

/// <summary>
/// Copies the capture helper's frames from its stdout into ffmpeg's stdin, 1 MiB at a time, counting the bytes, and notes when
/// the first bytes arrived. When the helper's stdout ends, ffmpeg's stdin is closed, so ffmpeg finalises its file; when ffmpeg
/// stops taking frames, the helper's stdout is let go of, which ends the helper (it exits 8).
/// </summary>
internal sealed class FramePump(Stream frames, Stream encoder, TimeProvider time)
{
    private const int BufferBytes = 1 << 20;

    private long _bytes;
    private volatile Exception? _writeFailure;
    private volatile bool _framesEnded;

    /// <summary>The bytes copied into ffmpeg so far.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>When the first bytes arrived, as a <see cref="TimeProvider"/> timestamp.</summary>
    public long FirstBytesTimestamp { get; private set; }

    /// <summary>When the first bytes arrived, in UTC.</summary>
    public DateTimeOffset FirstBytesUtc { get; private set; }

    /// <summary>Why ffmpeg stopped taking frames, when it did.</summary>
    public Exception? WriteFailure => _writeFailure;

    /// <summary>Whether the helper's frames ended, set before ffmpeg's stdin is closed for it.</summary>
    public bool FramesEnded => _framesEnded;

    /// <summary>Copies until the helper's stdout ends or ffmpeg stops taking frames, then lets go of both.</summary>
    public async Task RunAsync()
    {
        byte[] buffer = new byte[BufferBytes];
        bool started = false;
        int read;
        while ((read = await ReadAsync(buffer)) > 0)
        {
            if (!started)
            {
                started = true;
                FirstBytesTimestamp = time.GetTimestamp();
                FirstBytesUtc = time.GetUtcNow();
            }

            if (!await WriteAsync(buffer.AsMemory(0, read)))
            {
                break;
            }

            Interlocked.Add(ref _bytes, read);
        }

        _framesEnded = read == 0;
        frames.Dispose();
        CloseEncoder();
    }

    private async Task<int> ReadAsync(byte[] buffer)
    {
        try
        {
            return await frames.ReadAsync(buffer);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The helper's stdout broke or was let go of at a kill: that is the end of its frames, and its exit result,
            // which the recording reports, says why it ended.
            return 0;
        }
    }

    private async Task<bool> WriteAsync(ReadOnlyMemory<byte> chunk)
    {
        try
        {
            await encoder.WriteAsync(chunk);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            _writeFailure = e;
            return false;
        }
    }

    private void CloseEncoder()
    {
        try
        {
            encoder.Dispose();
        }
        catch (IOException e)
        {
            _writeFailure ??= e;
        }
    }
}

/// <summary>
/// One start attempt's processes: ffmpeg, started first with its stdin kept and its progress reports read from its stdout,
/// and the capture helper, with its stdout piped; the copy between them, both exits, and when the helper's exit was seen.
/// </summary>
internal sealed class RealtimePipeline : IDisposable
{
    /// <summary>
    /// ffmpeg's arguments for its progress reports on stdout, ten a second, its status line on stderr turned off. ffmpeg makes
    /// its first report only once its output's header is written, which needs the encoder open; an encoder that fails at its
    /// start gets one report, the last, which says <c>progress=end</c>.
    /// </summary>
    public static readonly string[] ProgressArguments = ["-progress", "pipe:1", "-stats_period", "0.1", "-nostats"];

    /// <summary>The line that ends a progress report while the encode goes on.</summary>
    private const string ProgressGoesOn = "progress=continue";

    private readonly IRealtimeProcess _ffmpeg;
    private readonly IRealtimeProcess _helper;
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private Task<(ToolProcessResult Helper, ToolProcessResult Ffmpeg)>? _abandoned;
    private Task _helperKill = Task.CompletedTask;

    // Whether the processes are let go of, or about to be, under _gate: a kill from then on has nothing to kill.
    private bool _lettingGo;
    private bool _disposed;

    private RealtimePipeline(IRealtimeProcess ffmpeg, Task<ToolProcessResult> ffmpegExit, IRealtimeProcess helper, FramePump pump, TimeProvider time)
    {
        _ffmpeg = ffmpeg;
        _helper = helper;
        Pump = pump;
        FfmpegExit = ffmpegExit;
        HelperExit = helper.WaitAsync(CancellationToken.None);
        HelperEndedAt = StampAsync(HelperExit, time);
        Stream progress = ffmpeg.StandardOutput;
        Progress = Task.Run(() => ReadProgressAsync(progress, _opened));
        Copy = Task.Run(pump.RunAsync);
    }

    public FramePump Pump { get; }

    public Task<ToolProcessResult> FfmpegExit { get; }

    public Task<ToolProcessResult> HelperExit { get; }

    /// <summary>Completes once ffmpeg has opened its encoder, as its first progress report that goes on says.</summary>
    public Task EncoderOpened => _opened.Task;

    /// <summary>When the helper's exit was seen, as a <see cref="TimeProvider"/> timestamp.</summary>
    public Task<long> HelperEndedAt { get; }

    /// <summary>The copy from the helper into ffmpeg; it never throws.</summary>
    public Task Copy { get; }

    /// <summary>The reading of ffmpeg's progress reports to their end; it never throws.</summary>
    private Task Progress { get; }

    /// <summary>Starts ffmpeg, then the helper, and the copy between them; a helper that cannot start takes ffmpeg down with it.</summary>
    /// <exception cref="SessionException">Either could not be started.</exception>
    public static async Task<RealtimePipeline> StartAsync(RealtimeEnvironment environment, ToolProcessRequest encode, ToolProcessRequest capture)
    {
        IRealtimeProcess ffmpeg = StartProcess(environment, encode, "ffmpeg");
        Task<ToolProcessResult> ffmpegExit = ffmpeg.WaitAsync(CancellationToken.None);
        IRealtimeProcess helper;
        try
        {
            helper = StartProcess(environment, capture, "The capture helper");
        }
        catch (SessionException)
        {
            CloseInput(ffmpeg);
            await ffmpeg.KillAsync();
            await ffmpegExit;
            ffmpeg.Dispose();
            throw;
        }

        FramePump pump = new(helper.StandardOutput, ffmpeg.StandardInput, environment.Time);
        return new RealtimePipeline(ffmpeg, ffmpegExit, helper, pump, environment.Time);
    }

    /// <summary>Starts a process through the environment, a failure to start named as <paramref name="what"/>'s.</summary>
    /// <exception cref="SessionException">It could not be started.</exception>
    public static IRealtimeProcess StartProcess(RealtimeEnvironment environment, ToolProcessRequest request, string what)
    {
        try
        {
            return environment.Start(request);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new SessionException($"{what} could not be started from {request.FileName}: {e.Message}", e);
        }
    }

    /// <summary>How the wait for the encoder ended, read once one of the pipeline's tasks has completed or the wait ran out.</summary>
    public FirstFrameEnd Classify()
    {
        if (EncoderOpened.IsCompleted)
        {
            return FirstFrameEnd.Opened;
        }

        if (Pump.WriteFailure is not null)
        {
            return FirstFrameEnd.EncoderFailed;
        }

        // The frames ending closes ffmpeg's stdin, so ffmpeg may exit before the helper's exit is seen: that is still the helper's end.
        if (Pump.FramesEnded || HelperExit.IsCompleted)
        {
            return FirstFrameEnd.HelperEnded;
        }

        return FfmpegExit.IsCompleted ? FirstFrameEnd.EncoderFailed : FirstFrameEnd.TimedOut;
    }

    /// <summary>
    /// Kills the helper alone, which ends its frames: ffmpeg then finalises what it has. Once the processes are let go of,
    /// there is nothing to kill, and it returns at once.
    /// </summary>
    public Task KillHelperAsync()
    {
        lock (_gate)
        {
            return _lettingGo ? Task.CompletedTask : _helperKill = _helper.KillAsync();
        }
    }

    /// <summary>Kills both processes, waits for them and the copy, and lets go of them; once, whoever calls it again.</summary>
    public Task<(ToolProcessResult Helper, ToolProcessResult Ffmpeg)> AbandonAsync() => _abandoned ??= KillAndFinishAsync();

    /// <summary>Waits for the copy, the progress reports and both exits, and for a kill under way, then lets go of both processes.</summary>
    public async Task<(ToolProcessResult Helper, ToolProcessResult Ffmpeg)> FinishAsync()
    {
        await Copy;
        await Progress;
        ToolProcessResult helper = await HelperExit;
        ToolProcessResult ffmpeg = await FfmpegExit;
        Task kill;
        lock (_gate)
        {
            _lettingGo = true;
            kill = _helperKill;
        }

        await kill;
        Dispose();
        return (helper, ffmpeg);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _lettingGo = true;
            _disposed = true;
        }

        _helper.Dispose();
        _ffmpeg.Dispose();
    }

    private async Task<(ToolProcessResult Helper, ToolProcessResult Ffmpeg)> KillAndFinishAsync()
    {
        await KillHelperAsync();
        await _ffmpeg.KillAsync();
        return await FinishAsync();
    }

    /// <summary>
    /// Reads ffmpeg's progress reports to their end, so ffmpeg never waits on a full pipe, and completes
    /// <paramref name="opened"/> at the first that says the encode goes on.
    /// </summary>
    private static async Task ReadProgressAsync(Stream progress, TaskCompletionSource opened)
    {
        try
        {
            using StreamReader reader = new(progress, Encoding.UTF8);
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Trim() == ProgressGoesOn)
                {
                    opened.TrySetResult();
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // ffmpeg's stdout broke or was let go of at a kill: that is the end of its reports, and its exit result,
            // which the recording reports, says why it ended.
        }
    }

    /// <summary>Closes ffmpeg's stdin, so an ffmpeg still running ends its file before the kill that follows.</summary>
    private static void CloseInput(IRealtimeProcess ffmpeg)
    {
        try
        {
            ffmpeg.StandardInput.Dispose();
        }
        catch (IOException)
        {
            // ffmpeg is already gone and its pipe broken; the kill that follows has nothing left to end either.
        }
    }

    private static async Task<long> StampAsync(Task exit, TimeProvider time)
    {
        await exit;
        return time.GetTimestamp();
    }
}
