namespace GodotMcp.Capture;

/// <summary>Raised when reading a frame back fails because the D3D11 device was lost (removed or reset).</summary>
internal sealed class DeviceLostException : Exception
{
    public DeviceLostException() { }

    public DeviceLostException(string message)
        : base(message) { }

    public DeviceLostException(string message, Exception innerException)
        : base(message, innerException) => HResult = innerException.HResult;
}
