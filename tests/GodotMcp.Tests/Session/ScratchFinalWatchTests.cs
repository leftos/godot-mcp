using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Tests.Wire;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A scratch scene's watch once its game has stopped (<see cref="ScratchRun.FinalWatch"/>): the bridge's watchFinal frame
/// replaces the timeline peeked at the shutdown; without it the peeked one stays, ends at the shutdown, and a warning says so.
/// </summary>
public sealed class ScratchFinalWatchTests
{
    private const string NotFinal = "options.watch could not be collected at the game's exit; the timeline ends at the shutdown";

    [Fact]
    public void AWatchWithoutTheFinalFrameKeepsThePeekedTimelineEndedAtTheShutdownAndSaysSo()
    {
        JsonObject peeked = new() { ["frames"] = 3 };
        List<string> warnings = [];

        JsonObject? watch = ScratchRun.FinalWatch(peeked, final: null, new SessionClock(), warnings);

        Assert.Same(peeked, watch);
        Assert.Equal("shutdown", watch?["stopped"]?.GetValue<string>());
        Assert.Equal([NotFinal], warnings);
    }

    [Fact]
    public async Task AWatchWhoseFinalHandlerCouldNotBeSetKeepsThePeekedTimelineAndSaysSo()
    {
        await using RegistryHarness harness = new();
        using FakeBridge game = await harness.AttachFakeGameAsync(harness.Project("game"), "game", new FakeHello());
        JsonObject? frame = null;
        JsonObject peeked = new() { ["frames"] = 3 };
        List<string> warnings = [];

        // An attached game has no run of the server's own, so no handler can be set on it, as on a run whose game has ended.
        bool listening = harness.Sessions.Resolve("game").OnWatchFinal(final => frame = final);
        JsonObject? watch = ScratchRun.FinalWatch(peeked, frame, new SessionClock(), warnings);

        Assert.False(listening, "a handler was set on a session with no running run");
        Assert.Same(peeked, watch);
        Assert.Equal("shutdown", watch?["stopped"]?.GetValue<string>());
        Assert.Equal([NotFinal], warnings);
    }

    [Fact]
    public void AWatchFinalFrameReplacesThePeekedTimeline()
    {
        JsonObject peeked = new() { ["frames"] = 3 };
        JsonObject final = new()
        {
            ["type"] = "watchFinal",
            ["timeline"] = new JsonObject
            {
                ["frames"] = 4,
                ["stopped"] = "quit",
                ["tracks"] = new JsonArray(),
            },
        };
        List<string> warnings = [];

        JsonObject? watch = ScratchRun.FinalWatch(peeked, final, new SessionClock(), warnings);

        Assert.Equal(4, watch?["frames"]?.GetValue<int>());
        Assert.Equal("quit", watch?["stopped"]?.GetValue<string>());
        Assert.Empty(warnings);
    }
}
