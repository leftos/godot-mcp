using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>capture_frames' argument checks, which refuse before anything reaches a game, and its default allowance; no Godot runs here.</summary>
public sealed class CaptureFramesValidationTests : IDisposable
{
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly RuntimeTools _tools;

    public CaptureFramesValidationTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        _tools = new RuntimeTools(_sessions, TestCSharp.Unused());
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _listener.Dispose();
    }

    [Theory]
    [InlineData(false, false, null, null)]
    [InlineData(false, true, null, null)]
    [InlineData(true, true, 0.1, 0.3)]
    [InlineData(true, true, 0.1, null)]
    [InlineData(false, true, 0.1, null)]
    [InlineData(false, true, null, 0.3)]
    public async Task NeitherFormOrBothIsRefused(bool atGiven, bool optionsGiven, double? every, double? span)
    {
        double[]? at = atGiven ? [0.5] : null;
        CaptureFramesOptions? options = optionsGiven ? new CaptureFramesOptions(every, span, TimeoutMs: 1000) : null;

        McpException refused = await RefusedAsync(at, options);

        Assert.Equal("pass at (a list of seconds) or options.every and options.for, not both.", refused.Message);
    }

    [Fact]
    public async Task AnEmptyAtIsRefused()
    {
        McpException refused = await RefusedAsync([], null);

        Assert.Equal("at takes 1 to 1000 points; got 0.", refused.Message);
    }

    [Fact]
    public async Task MoreThanAThousandPointsAreRefused()
    {
        double[] at = [.. Enumerable.Range(0, 1001).Select(index => index * 0.1)];

        McpException refused = await RefusedAsync(at, null);

        Assert.Equal("at takes 1 to 1000 points; got 1001.", refused.Message);
    }

    [Theory]
    [InlineData(-0.5, "at[1] must be a number of seconds from 0 to 120; got -0.5.")]
    [InlineData(double.NaN, "at[1] must be a number of seconds from 0 to 120; got NaN.")]
    [InlineData(double.PositiveInfinity, "at[1] must be a number of seconds from 0 to 120; got Infinity.")]
    [InlineData(120.5, "at[1] must be a number of seconds from 0 to 120; got 120.5.")]
    public async Task APointOutOfRangeIsRefused(double point, string message)
    {
        McpException refused = await RefusedAsync([0, point], null);

        Assert.Equal(message, refused.Message);
    }

    [Fact]
    public async Task DescendingPointsAreRefused()
    {
        McpException refused = await RefusedAsync([0.5, 1.2, 1.0], null);

        Assert.Equal("at must be ascending; at[2] is 1, less than at[1], 1.2.", refused.Message);
    }

    [Theory]
    [InlineData(0.0, "options.every must be a number of seconds greater than 0; got 0.")]
    [InlineData(-0.1, "options.every must be a number of seconds greater than 0; got -0.1.")]
    [InlineData(double.NaN, "options.every must be a number of seconds greater than 0; got NaN.")]
    public async Task AnEveryOfZeroOrLessIsRefused(double every, string message)
    {
        McpException refused = await RefusedAsync(null, new CaptureFramesOptions(every, 1));

        Assert.Equal(message, refused.Message);
    }

    [Theory]
    [InlineData(0.0, "options.for must be greater than 0 and at most 120 seconds; got 0.")]
    [InlineData(120.001, "options.for must be greater than 0 and at most 120 seconds; got 120.001.")]
    public async Task AForOutOfRangeIsRefused(double span, string message)
    {
        McpException refused = await RefusedAsync(null, new CaptureFramesOptions(1, span));

        Assert.Equal(message, refused.Message);
    }

    [Fact]
    public async Task EveryAndForMakingMoreThanAThousandPointsAreRefused()
    {
        McpException refused = await RefusedAsync(null, new CaptureFramesOptions(0.01, 120));

        Assert.Equal("options.every 0.01 over options.for 120 makes 12000 points; at most 1000 are allowed.", refused.Message);
    }

    [Fact]
    public async Task AnEveryLongerThanForIsRefused()
    {
        McpException refused = await RefusedAsync(null, new CaptureFramesOptions(2, 1));

        Assert.Equal("options.every 2 is longer than options.for 1, so there is no point to capture.", refused.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(600_001)]
    public async Task ATimeoutOutOfRangeIsRefused(int timeoutMs)
    {
        McpException refused = await RefusedAsync([0.5], new CaptureFramesOptions(TimeoutMs: timeoutMs));

        Assert.Equal($"options.timeoutMs must be between 1 and 600000; got {timeoutMs}.", refused.Message);
    }

    [Fact]
    public async Task AnEmptyCropIsRefusedAsTakeScreenshotRefusesIt()
    {
        McpException refused = await RefusedAsync([0.5], new CaptureFramesOptions(Crop: new ScreenshotCrop(0, 0, 0, 10)));

        Assert.Equal("A crop needs a width and a height of at least 1 pixel; got 0x10.", refused.Message);
    }

    [Fact]
    public void EqualNeighboursAreAccepted() => Assert.Equal([0.1, 0.3, 0.3], RuntimeTools.CapturePoints([0.1, 0.3, 0.3], null));

    [Fact]
    public void EveryAndForGiveEachPointUpToFor() => Assert.Equal([0.5, 1.0], RuntimeTools.CapturePoints(null, new CaptureFramesOptions(0.5, 1.2)));

    [Fact]
    public void AForThatIsAMultipleOfEveryIsItselfAPoint()
    {
        double[] points = RuntimeTools.CapturePoints(null, new CaptureFramesOptions(0.1, 0.3));

        Assert.Equal(3, points.Length);
        Assert.Equal(0.3, points[^1], 9);
    }

    [Fact]
    public async Task AValidCallWithoutASessionSaysNoneIsRunning()
    {
        McpException refused = await RefusedAsync(
            [0, 0.5, 120],
            new CaptureFramesOptions(Crop: new ScreenshotCrop(0, 0, 10, 10), TimeoutMs: 600_000)
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultAllowanceIsTheLastPointPlusTenSecondsAndATenthOfASecondAPoint()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1200 + 10_000 + 300), RuntimeTools.CaptureAllowance([0.1, 0.5, 1.2], null));
        Assert.Equal(TimeSpan.FromMilliseconds(10_100), RuntimeTools.CaptureAllowance([0], null));
    }

    [Fact]
    public void TimeoutMsReplacesTheDefaultAllowance() =>
        Assert.Equal(TimeSpan.FromMilliseconds(1500), RuntimeTools.CaptureAllowance([0.05, 5.0], 1500));

    private async Task<McpException> RefusedAsync(double[]? at, CaptureFramesOptions? options) =>
        await Assert.ThrowsAsync<McpException>(() => _tools.CaptureFramesAsync(at, options, null, TestContext.Current.CancellationToken));
}
