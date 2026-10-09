using GodotMcp.Capture;

namespace GodotMcp.Tests.Capture;

public sealed class CaptureArgsTests
{
    [Fact]
    public void EveryArgumentIsParsed()
    {
        var parsed = CaptureArgs.Parse(
            [
                "--hwnd",
                "131234",
                "--crop",
                "1,31,640,360",
                "--fps",
                "60",
                "--until-file",
                @"C:\rec\stop.txt",
                "--audio-pid",
                "4242",
                "--audio-pipe",
                "godot-mcp-audio",
            ],
            out string? error
        );

        Assert.Null(error);
        Assert.Equal(new CaptureArgs(131234, new PixelRect(1, 31, 640, 360), 60, @"C:\rec\stop.txt", false, 4242, "godot-mcp-audio"), parsed);
    }

    [Fact]
    public void AProbeNeedsNoDeadlineAndTakesTheDefaultRate()
    {
        var parsed = CaptureArgs.Parse(["--hwnd", "7", "--probe"], out string? error);

        Assert.Null(error);
        Assert.Equal(new CaptureArgs(7, null, CaptureArgs.DefaultFps, null, true, null, null), parsed);
    }

    [Fact]
    public void AMissingHwndIsRefused()
    {
        Assert.Null(CaptureArgs.Parse(["--until-file", "stop.txt"], out string? error));
        Assert.Equal("--hwnd is required.", error);
    }

    [Theory]
    [InlineData("1,31,640")]
    [InlineData("1,31,640,360,5")]
    [InlineData("-1,31,640,360")]
    [InlineData("1,31,0,360")]
    [InlineData("a,31,640,360")]
    [InlineData("1, 31,640,360")]
    public void AMalformedCropIsRefused(string crop)
    {
        Assert.Null(CaptureArgs.Parse(["--hwnd", "7", "--probe", "--crop", crop], out string? error));
        Assert.Equal($"--crop takes x,y,w,h: four decimals, the corner 0 or more and the size above 0, not '{crop}'.", error);
    }

    [Fact]
    public void WithoutACropTheWholeItemIsCopied()
    {
        PixelRect? crop = Crop.Resolve(null, 642, 392, out string? error);

        Assert.Null(error);
        Assert.Equal(new PixelRect(0, 0, 642, 392), crop);
    }

    [Fact]
    public void ACropInsideTheItemIsKept()
    {
        PixelRect? crop = Crop.Resolve(new PixelRect(1, 31, 640, 360), 642, 392, out string? error);

        Assert.Null(error);
        Assert.Equal(new PixelRect(1, 31, 640, 360), crop);
    }

    [Theory]
    [InlineData(1, 31, 642, 360)]
    [InlineData(1, 33, 640, 360)]
    [InlineData(0, 0, 2000, 2000)]
    public void ACropOutsideTheItemIsRefusedNamingBothRectangles(int x, int y, int width, int height)
    {
        PixelRect? crop = Crop.Resolve(new PixelRect(x, y, width, height), 642, 392, out string? error);

        Assert.Null(crop);
        Assert.Equal(
            $"--crop {x},{y},{width},{height} reaches outside the capture item, which is 642x392 (0,0,642,392); a crop must lie within it.",
            error
        );
    }

    [Fact]
    public void ACropReachingPastIntRangeIsRefusedNotWrapped()
    {
        Assert.Null(Crop.Resolve(new PixelRect(int.MaxValue, 0, 10, 10), 642, 392, out string? error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(641, 361, 642, 362)]
    [InlineData(640, 360, 640, 360)]
    [InlineData(1, 1, 2, 2)]
    public void AnOddSideIsPaddedToEven(int width, int height, int outputWidth, int outputHeight)
    {
        PixelRect crop = new(1, 31, width, height);

        Assert.Equal(outputWidth, Crop.OutputWidth(crop));
        Assert.Equal(outputHeight, Crop.OutputHeight(crop));
    }

    [Fact]
    public void TheProbeLineIsThePaddedCropSize()
    {
        Assert.Equal("642x362", Crop.ProbeLine(new PixelRect(1, 31, 641, 361)));
        Assert.Equal("1922x1112", Crop.ProbeLine(new PixelRect(0, 0, 1922, 1112)));
    }

    [Theory]
    [InlineData("--title")]
    [InlineData("--seconds")]
    [InlineData("--audio-only")]
    public void DroppedFlagsAreRefusedAsUnknown(string flag)
    {
        Assert.Null(CaptureArgs.Parse(["--hwnd", "7", "--probe", flag, "5"], out string? error));
        Assert.Equal($"unknown argument '{flag}'.", error);
    }

    [Fact]
    public void ARunNeedsTheDeadlineFile()
    {
        Assert.Null(CaptureArgs.Parse(["--hwnd", "7"], out string? error));
        Assert.Equal("--until-file is required unless --probe is given.", error);
    }

    [Theory]
    [InlineData("--audio-pid", "4242", "--audio-pid needs --audio-pipe.")]
    [InlineData("--audio-pipe", "godot-mcp-audio", "--audio-pipe needs --audio-pid.")]
    public void AudioNeedsBothItsArguments(string name, string value, string expected)
    {
        Assert.Null(CaptureArgs.Parse(["--hwnd", "7", "--until-file", "stop.txt", name, value], out string? error));
        Assert.Equal(expected, error);
    }

    [Fact]
    public void AnArgumentWithoutItsValueIsRefused()
    {
        Assert.Null(CaptureArgs.Parse(["--hwnd", "7", "--fps", "--probe"], out string? error));
        Assert.Equal("--fps needs a value.", error);
    }

    [Theory]
    [InlineData("--hwnd", "0", "--hwnd takes a window handle as a positive decimal, not '0'.")]
    [InlineData("--fps", "0", "--fps takes a positive decimal, not '0'.")]
    [InlineData("--audio-pid", "x", "--audio-pid takes a process id as a positive decimal, not 'x'.")]
    public void ANonPositiveNumberIsRefused(string name, string value, string expected)
    {
        Assert.Null(CaptureArgs.Parse(["--hwnd", "7", "--probe", name, value], out string? error));
        Assert.Equal(expected, error);
    }
}
