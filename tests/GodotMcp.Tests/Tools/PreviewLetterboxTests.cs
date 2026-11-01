using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>preview_scene's warning for a capture that is not the window's size, which says both sizes.</summary>
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
}
