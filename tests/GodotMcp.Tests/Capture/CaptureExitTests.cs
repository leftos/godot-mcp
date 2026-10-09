using GodotMcp.Capture;

namespace GodotMcp.Tests.Capture;

public sealed class CaptureExitTests
{
    [Theory]
    [InlineData(nameof(CaptureEnd.Finished), true, 0)]
    [InlineData(nameof(CaptureEnd.Finished), false, 5)]
    [InlineData(nameof(CaptureEnd.WindowClosed), true, 0)]
    [InlineData(nameof(CaptureEnd.WindowClosed), false, 5)]
    [InlineData(nameof(CaptureEnd.Resized), true, 3)]
    [InlineData(nameof(CaptureEnd.Resized), false, 3)]
    [InlineData(nameof(CaptureEnd.DeviceLost), true, 7)]
    [InlineData(nameof(CaptureEnd.ReaderGone), false, 8)]
    public void EachEndHasItsExitCode(string end, bool audioRead, int code) =>
        Assert.Equal(code, CaptureExit.For(Enum.Parse<CaptureEnd>(end), audioRead));

    [Fact]
    public void TheNamedCodesAreDistinct()
    {
        int[] codes =
        [
            CaptureExit.Done,
            CaptureExit.Usage,
            CaptureExit.Resized,
            CaptureExit.AudioActivation,
            CaptureExit.AudioUnread,
            CaptureExit.CaptureStart,
            CaptureExit.DeviceLost,
            CaptureExit.ReaderGone,
        ];

        Assert.Equal([0, 2, 3, 4, 5, 6, 7, 8], codes);
    }

    [Fact]
    public void AReaderGoneSaysHowManyFramesItGot() =>
        Assert.Equal("the reader closed the video stream after 42 frames.", CaptureExit.ReaderGoneLine(42));

    [Fact]
    public void ADeviceLostNamesItsResult() =>
        Assert.Equal(
            "the D3D11 device was lost reading frame 11 back (HRESULT 0x887A0005), so the capture stopped after 10 frames.",
            CaptureExit.DeviceLostLine(unchecked((int)0x887A0005), 10)
        );

    [Fact]
    public void AFailedStartNamesWhatFailed() =>
        Assert.Equal(
            "the capture could not start: creating the capture item for the window failed (HRESULT 0x80070057): The parameter is incorrect.",
            CaptureExit.CaptureStartLine("creating the capture item for the window", unchecked((int)0x80070057), "The parameter is incorrect.")
        );
}
