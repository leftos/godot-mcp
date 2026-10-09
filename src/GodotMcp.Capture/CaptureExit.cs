namespace GodotMcp.Capture;

/// <summary>The helper's exit codes, one per kind of end, and the stderr lines that name a failure.</summary>
internal static class CaptureExit
{
    public const int Done = 0;
    public const int Usage = 2;
    public const int Resized = 3;
    public const int AudioActivation = 4;
    public const int AudioUnread = 5;
    public const int CaptureStart = 6;
    public const int DeviceLost = 7;
    public const int ReaderGone = 8;

    /// <summary>
    /// The exit code of a run whose video ended with <paramref name="end"/>: a failure's own code, else 0 when the audio
    /// (if any) reached its reader, else <see cref="AudioUnread"/>.
    /// </summary>
    public static int For(CaptureEnd end, bool audioRead) =>
        end switch
        {
            CaptureEnd.Resized => Resized,
            CaptureEnd.DeviceLost => DeviceLost,
            CaptureEnd.ReaderGone => ReaderGone,
            _ => audioRead ? Done : AudioUnread,
        };

    public static string ReaderGoneLine(int frames) => FormattableString.Invariant($"the reader closed the video stream after {frames} frames.");

    public static string DeviceLostLine(int hresult, int frames) =>
        FormattableString.Invariant(
            $"the D3D11 device was lost reading frame {frames + 1} back (HRESULT 0x{hresult:X8}), so the capture stopped after {frames} frames."
        );

    public static string CaptureStartLine(string what, int hresult, string detail) =>
        FormattableString.Invariant($"the capture could not start: {what} failed (HRESULT 0x{hresult:X8}): {detail}");
}
