using System.Runtime.InteropServices;
using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>The window rectangle arithmetic a real-time recording crops by, and the Windows read of a handle that is gone.</summary>
public sealed class WindowRectTests
{
    private const string WindowsOnly = "Window handles are a Windows feature.";

    // The spike's measurement at 100%: a 640x360 client in a 642x392 extended frame, its corner at (1, 31) of the frame.
    [Fact]
    public void TheCropIsTheClientsPlaceInTheFrame()
    {
        PixelRect crop = WindowRect.Crop(new PixelRect(100, 100, 642, 392), (101, 131), (640, 360));

        Assert.Equal(new PixelRect(1, 31, 640, 360), crop);
    }

    [Fact]
    public void AnEmptyClientIsRefused()
    {
        SessionException refused = Assert.Throws<SessionException>(() => WindowRect.Crop(new PixelRect(100, 100, 642, 392), (101, 131), (0, 0)));

        Assert.Equal(
            "the game's client area (101,131 0x0) is empty, so there is nothing to crop from its window frame (100,100 642x392).",
            refused.Message
        );
    }

    [Theory]
    [InlineData(0, 360)]
    [InlineData(640, -1)]
    public void AClientWithASideBelowOneIsRefused(int width, int height) =>
        Assert.Throws<SessionException>(() => WindowRect.Crop(new PixelRect(100, 100, 642, 392), (101, 131), (width, height)));

    [Theory]
    [InlineData(99, 131)]
    [InlineData(101, 99)]
    [InlineData(103, 131)]
    [InlineData(101, 133)]
    public void AClientOutsideTheFrameIsRefused(int clientX, int clientY)
    {
        SessionException refused = Assert.Throws<SessionException>(() =>
            WindowRect.Crop(new PixelRect(100, 100, 642, 392), (clientX, clientY), (640, 360))
        );

        Assert.Equal(
            $"the game's client area ({clientX},{clientY} 640x360) does not lie within its window frame (100,100 642x392), so it cannot be "
                + "cropped from it.",
            refused.Message
        );
    }

    [Fact]
    public void AReadingsClientAreaIsCropped()
    {
        WindowReading reading = new(new PixelRect(100, 100, 642, 392), (101, 131), (640, 360), Minimized: false, new PixelRect(-1920, 0, 3840, 1080));

        Assert.Equal(new PixelRect(1, 31, 640, 360), WindowRect.Crop(reading));
    }

    [Fact]
    public void AMinimizedWindowIsRefused()
    {
        WindowReading minimized = new(
            new PixelRect(100, 100, 642, 392),
            (101, 131),
            (640, 360),
            Minimized: true,
            new PixelRect(-1920, 0, 3840, 1080)
        );

        SessionException refused = Assert.Throws<SessionException>(() => WindowRect.Crop(minimized));

        Assert.Equal(
            "The game's window is minimized, so Windows composes no picture of it; restore it, then record_mark start again.",
            refused.Message
        );
    }

    [Fact]
    public void OddSidesRoundUpToEven()
    {
        Assert.Equal((642, 360), WindowRect.OutputSize(new PixelRect(1, 31, 641, 359)));
        Assert.Equal((640, 360), WindowRect.OutputSize(new PixelRect(1, 31, 640, 360)));
        Assert.Equal((2, 2), WindowRect.OutputSize(new PixelRect(0, 0, 1, 1)));
    }

    [Fact]
    public void AClientPartlyOffTheDesktopIsWarnedWithBothRects()
    {
        PixelRect desktop = new(-1920, 0, 3840, 1080);

        string? warning = WindowRect.DescribeOffDesktop(new PixelRect(1600, 900, 642, 392), desktop);

        Assert.Equal(
            "the game's window (1600,900 642x392) lies partly outside the desktop (-1920,0 3840x1080); the parts outside it keep their first "
                + "picture and do not update.",
            warning
        );
        Assert.NotNull(WindowRect.DescribeOffDesktop(new PixelRect(-1930, 10, 642, 392), desktop));
    }

    [Fact]
    public void AClientFullyOnTheDesktopHasNoWarning()
    {
        PixelRect desktop = new(-1920, 0, 3840, 1080);

        Assert.Null(WindowRect.DescribeOffDesktop(new PixelRect(100, 100, 642, 392), desktop));
        Assert.Null(WindowRect.DescribeOffDesktop(desktop, desktop));
        // A client flush with the desktop's right edge: the frame's 1 px border past it is not the client's, so it does not warn.
        Assert.Null(WindowRect.DescribeOffDesktop(new PixelRect(1278, 0, 642, 360), desktop));
    }

    [Fact]
    public void ReadingAHandleThatIsGoneFails()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        SessionException failed = Assert.Throws<SessionException>(() => WindowRect.Read(0x7FFFFFFF));

        Assert.Equal("window 2147483647 no longer exists.", failed.Message);
    }

    [Fact]
    public void AFailedReadLeavesTheThreadsDpiAwarenessContextAlone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        nint before = GetThreadDpiAwarenessContext();

        Assert.Throws<SessionException>(() => WindowRect.Read(0x7FFFFFFF));

        Assert.True(AreDpiAwarenessContextsEqual(before, GetThreadDpiAwarenessContext()));
    }

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
}
