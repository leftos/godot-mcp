namespace GodotMcp.Capture;

/// <summary>Raised when the capture item, the D3D11 device or the frame pool cannot be created; the message names which.</summary>
internal sealed class CaptureStartException : Exception
{
    public CaptureStartException() { }

    public CaptureStartException(string message)
        : base(message) { }

    public CaptureStartException(string message, Exception innerException)
        : base(message, innerException) => HResult = innerException.HResult;
}
