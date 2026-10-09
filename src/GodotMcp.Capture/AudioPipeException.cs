namespace GodotMcp.Capture;

/// <summary>Raised when the pipe a run's audio goes to cannot be created, its name being in use or refused (exit 4).</summary>
internal sealed class AudioPipeException(string message, Exception innerException) : AudioActivationException(message, innerException);
