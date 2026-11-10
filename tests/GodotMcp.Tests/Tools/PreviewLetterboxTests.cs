using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// The warning every capture's result carries when it is not the window's size, which says both sizes; none for a crop, a
/// scaled capture or a reply with no window.
/// </summary>
public sealed class PreviewLetterboxTests
{
    [Fact]
    public void ACaptureTheSizeOfTheWindowCarriesNoWarning() => Assert.Null(PreviewTools.DescribeLetterbox(1000, 900, 1000, 900));

    [Fact]
    public void ACaptureSmallerThanItsWindowSaysBothSizes() =>
        Assert.Equal(
            "the capture is 1000x562, the window 1000x900: the project's or the scene's stretch settings draw the game at "
                + "another size (letterbox bars under aspect keep, the base size under stretch mode viewport), "
                + "so coordinates read off the capture are the capture's",
            PreviewTools.DescribeLetterbox(1000, 562, 1000, 900)
        );

    [Fact]
    public void ACaptureAFewPixelsShortSaysBothExactSizes() =>
        Assert.Equal(
            "the capture is 1498x843, the window 1500x843: the project's or the scene's stretch settings draw the game at "
                + "another size (letterbox bars under aspect keep, the base size under stretch mode viewport), "
                + "so coordinates read off the capture are the capture's",
            PreviewTools.DescribeLetterbox(1498, 843, 1500, 843)
        );

    [Fact]
    public void AKeepCaptureAPixelShortOfItsWindowSaysBothSizes() =>
        Assert.Equal(
            "the capture is 1365x768, the window 1366x768: the project's or the scene's stretch settings draw the game at "
                + "another size (letterbox bars under aspect keep, the base size under stretch mode viewport), "
                + "so coordinates read off the capture are the capture's",
            RuntimeTools.LetterboxWarning(Capture(1365, 768, new CaptureSize(1366, 768)), cropped: false, scale: 1)
        );

    [Fact]
    public void ACaptureTheSizeOfItsWindowHasNoLetterboxWarning() =>
        Assert.Null(RuntimeTools.LetterboxWarning(Capture(1366, 768, new CaptureSize(1366, 768)), cropped: false, scale: 1));

    [Fact]
    public void ACroppedCaptureHasNoLetterboxWarning() =>
        Assert.Null(RuntimeTools.LetterboxWarning(Capture(120, 80, new CaptureSize(1366, 768)), cropped: true, scale: 1));

    [Fact]
    public void AScaledCaptureHasNoLetterboxWarning() =>
        Assert.Null(RuntimeTools.LetterboxWarning(Capture(1280, 720, new CaptureSize(640, 360)), cropped: false, scale: 2));

    [Fact]
    public void ACaptureWhoseReplyNamesNoWindowHasNoLetterboxWarning() =>
        Assert.Null(RuntimeTools.LetterboxWarning(Capture(1365, 768, null), cropped: false, scale: 1));

    private static ScreenshotFiles Capture(int width, int height, CaptureSize? window) =>
        new("shot.png", width, height, null, null, null, window, new CaptureSize(640, 360));
}
