using System.IO.Pipes;

namespace GodotMcp.Capture;

/// <summary>
/// The named pipe a run's audio goes to, created before the reader opens it. Its own file, free of WASAPI, so the
/// name-taken refusal is unit-tested beside the argument and crop files.
/// </summary>
internal static class AudioPipe
{
    /// <summary>
    /// The pipe server for <paramref name="pipeName"/>; throws <see cref="AudioPipeException"/> when the name is in use
    /// or refused, which a run reports as an audio activation failure (exit 4).
    /// </summary>
    public static NamedPipeServerStream Create(string pipeName)
    {
        try
        {
            return new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new AudioPipeException(
                FormattableString.Invariant($"the audio pipe '{pipeName}' could not be created: {failure.Message}"),
                failure
            );
        }
    }
}
