using System.Text.Json.Nodes;
using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The params a scratch step's wait carries to end its window (<see cref="ScratchRun.EndsWindow"/>): its end marker, and on the
/// last played step's wait the exit hold the bridge runs on from that cut, its length and the marker that ends it.
/// </summary>
public sealed class ScratchHoldTests
{
    [Fact]
    public void AWaitWithoutAHoldCarriesOnlyItsEndMarker()
    {
        JsonObject wait = new() { ["kind"] = "gameMs" };

        ScratchRun.EndsWindow(wait, "[godot-mcp] scratch step 0 end n", hold: null);

        Assert.Equal("""{"kind":"gameMs","endMarker":"[godot-mcp] scratch step 0 end n"}""", wait.ToJsonString());
    }

    [Fact]
    public void TheLastStepsWaitCarriesTheHoldsLengthAndEndMarker()
    {
        JsonObject wait = new() { ["kind"] = "gameMs" };

        ScratchRun.EndsWindow(wait, "[godot-mcp] scratch step 1 end n", new ScratchRun.HoldEnd(2000, "[godot-mcp] scratch pace end n"));

        Assert.Equal(
            """{"kind":"gameMs","endMarker":"[godot-mcp] scratch step 1 end n","holdGameMs":2000,"holdEndMarker":"[godot-mcp] scratch pace end n"}""",
            wait.ToJsonString()
        );
    }
}
