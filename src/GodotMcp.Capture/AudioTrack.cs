using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace GodotMcp.Capture;

/// <summary>
/// One run's audio: a process-loopback capture and the named pipe its raw s16le goes to, for ffmpeg to open as a second
/// input. The pipe server exists from <see cref="Open"/> on, so it is there before ffmpeg opens it. A probe opens the
/// track without a pipe, only to activate the capture, and never records.
/// </summary>
internal sealed class AudioTrack(int processId, ProcessAudioCapture capture, NamedPipeServerStream? pipe) : IDisposable
{
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(15);

    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );

    /// <summary>
    /// Activates the capture and creates the pipe; throws <see cref="AudioActivationException"/> when activation fails or
    /// the pipe cannot be created.
    /// </summary>
    public static AudioTrack Open(int processId, string? pipeName)
    {
        ProcessAudioCapture capture = new(processId);
        NamedPipeServerStream? pipe = pipeName is null ? null : AudioPipe.Create(pipeName);
        return new AudioTrack(processId, capture, pipe);
    }

    /// <summary>
    /// Records audio until <paramref name="deadline"/> passes. The clock starts now, with the video's first frame, and the
    /// audio is held until the reader connects. Returns false when the reader never connected or the capture failed, so
    /// the run's own exit code is the audio-unread one; the video's outcome is never hidden by it.
    /// </summary>
    public async Task<bool> RecordAsync(Deadline deadline)
    {
        NamedPipeServerStream output = pipe ?? throw new InvalidOperationException("an audio track opened without a pipe cannot record.");
        Task<bool> connecting = ConnectAsync(output);
        string format = $"{ProcessAudioCapture.SampleRate} Hz stereo s16le";
        await Console
            .Error.WriteLineAsync($"godot-mcp-capture: audio of process {processId} and its children, {format} {deadline.Describe()}")
            .ConfigureAwait(false);
        Task<long> capturing = Task.Factory.StartNew(
            () => capture.Run(deadline, _chunks.Writer),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        bool connected = await connecting.ConfigureAwait(false);
        long written = connected ? await CopyAsync(output).ConfigureAwait(false) : 0;
        long captured;
        try
        {
            captured = await capturing.ConfigureAwait(false);
        }
        catch (COMException failure)
        {
            await Console
                .Error.WriteLineAsync($"godot-mcp-capture: the audio capture failed (HRESULT 0x{failure.HResult:X8}): {failure.Message}")
                .ConfigureAwait(false);
            return false;
        }
        await Console
            .Error.WriteLineAsync(
                $"godot-mcp-capture: audio {written} frames written; {captured} came from the loopback stream and the rest is silence filling"
            )
            .ConfigureAwait(false);
        return connected;
    }

    public void Dispose()
    {
        pipe?.Dispose();
        capture.Dispose();
    }

    private static async Task<bool> ConnectAsync(NamedPipeServerStream output)
    {
        using CancellationTokenSource timeout = new(ConnectWait);
        try
        {
            await output.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            await Console
                .Error.WriteLineAsync($"godot-mcp-capture: nothing opened the audio pipe in {ConnectWait.TotalSeconds} s; no audio was written.")
                .ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Copies the captured chunks to the reader; returns how many frames reached it.</summary>
    private async Task<long> CopyAsync(NamedPipeServerStream output)
    {
        long frames = 0;
        try
        {
            await foreach (byte[] chunk in _chunks.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await output.WriteAsync(chunk).ConfigureAwait(false);
                frames += chunk.Length / ProcessAudioCapture.BytesPerFrame;
            }
            await output.FlushAsync().ConfigureAwait(false);
            // Closing the server end must not cut off what ffmpeg has not read yet, so the run ends once the pipe is drained.
            output.WaitForPipeDrain();
        }
        catch (IOException closed)
        {
            // ffmpeg closes its inputs when it stops (on q, at the same deadline), which can land before the last few
            // milliseconds were written.
            await Console
                .Error.WriteLineAsync($"godot-mcp-capture: the reader closed the audio stream early ({closed.Message}).")
                .ConfigureAwait(false);
        }
        return frames;
    }
}
