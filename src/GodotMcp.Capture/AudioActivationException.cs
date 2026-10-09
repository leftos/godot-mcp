namespace GodotMcp.Capture;

/// <summary>
/// Raised when a run's audio cannot start: the process-loopback client cannot be activated or initialised, or the pipe
/// the audio goes to cannot be created (<see cref="AudioPipeException"/>).
/// </summary>
internal class AudioActivationException : Exception
{
    public AudioActivationException() { }

    public AudioActivationException(string message)
        : base(message) { }

    public AudioActivationException(string message, Exception innerException)
        : base(message, innerException) { }
}
