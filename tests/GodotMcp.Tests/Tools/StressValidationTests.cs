using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>stress_input's argument checks, which refuse before anything reaches a game; no Godot runs here.</summary>
public sealed class StressValidationTests
{
    private const string EmptyPool = "pool is empty: give at least one of actions, keys, elements";

    [Fact]
    public void AnEmptyPoolIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => StressTools.CheckPool(new StressPool()));

        Assert.Equal(EmptyPool, refused.Message);
    }

    [Fact]
    public void AMissingPoolIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => StressTools.CheckPool(null));

        Assert.Equal(EmptyPool, refused.Message);
    }

    [Fact]
    public void ABlankEntryIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => StressTools.CheckPool(new StressPool(Keys: ["probe_jump", "  "])));

        Assert.Equal("pool.keys[1] is blank", refused.Message);
    }

    [Fact]
    public void APoolOf201EntriesIsRefused()
    {
        StressPool pool = new(Actions: [.. Enumerable.Range(0, 201).Select(index => $"probe_{index}")]);

        McpException refused = Assert.Throws<McpException>(() => StressTools.CheckPool(pool));

        Assert.Equal("pool has 201 entries; at most 200", refused.Message);
    }

    [Fact]
    public void AZeroCountIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => StressTools.CheckCount(0));

        Assert.Equal("count must be 1-1000; got 0", refused.Message);
    }

    [Fact]
    public void ACountOverTheMaximumIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => StressTools.CheckCount(1001));

        Assert.Equal("count must be 1-1000; got 1001", refused.Message);
    }

    [Fact]
    public void ANegativeGapIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => StressTools.CheckGap(new StressOptions(-1)));

        Assert.Equal("options.gapMs must be 0-5000; got -1", refused.Message);
    }

    [Fact]
    public void AGapOverTheMaximumIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => StressTools.CheckGap(new StressOptions(5001)));

        Assert.Equal("options.gapMs must be 0-5000; got 5001", refused.Message);
    }

    [Fact]
    public void ANegativeSeedIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => StressTools.CheckSeed(-1));

        Assert.Equal("seed must be 0 or more; got -1.", refused.Message);
    }

    [Fact]
    public void AStressCallInARecordingIsAllowedItsGapInClipFrames()
    {
        Assert.Equal(TimeSpan.FromSeconds(41.2), StressTools.ReplyTimeout(2, 5000, recording: true));

        Assert.Equal(TimeSpan.FromSeconds(11.2), StressTools.ReplyTimeout(2, 0, recording: true));
    }

    [Fact]
    public void ANonRecordedStressCallKeepsItsRealTimeAllowance()
    {
        TimeSpan allowance = StressTools.ReplyTimeout(2, 5000, recording: false);

        Assert.Equal(TimeSpan.FromSeconds(15.2), allowance);
    }
}
