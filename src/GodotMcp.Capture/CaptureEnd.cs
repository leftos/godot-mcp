namespace GodotMcp.Capture;

/// <summary>What ended a run's video once its first frame was written.</summary>
internal enum CaptureEnd
{
    /// <summary>The deadline passed.</summary>
    Finished,

    /// <summary>The window closed; the frames written are kept.</summary>
    WindowClosed,

    /// <summary>The window changed size, which a run's fixed output size cannot follow.</summary>
    Resized,

    /// <summary>The D3D11 device was lost while a frame was read back.</summary>
    DeviceLost,

    /// <summary>The reader closed the video stream.</summary>
    ReaderGone,
}
