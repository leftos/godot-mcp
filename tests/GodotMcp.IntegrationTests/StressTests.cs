using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// stress_input against the InputProbe running in the real Godot: one shared run with the machine's real pads shut out,
/// reset before each test. The probe's SmallButton counts its presses, so a drawn element shows as one, and PadProbe
/// counts the presses of probe_jump, so a drawn action does. A resting real pad's jitter moves the focus between a
/// click's press and its release, and BaseButton's FOCUS_EXIT then clears press_attempt, so the release emits no pressed:
/// that reads as a click this tool lost, though it played it. InputTests pins the engine behaviour on its own.
/// </summary>
public sealed class StressTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;
    private static readonly StressPool JumpAndButton = new(Actions: ["probe_jump"], Elements: ["SmallButton"]);
    private readonly SharedProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());
    private readonly StressTools _stress = new(shared.Sessions);

    public async ValueTask InitializeAsync() => await _shared.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SameSeedDrawsTheSameCounts()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Counts before = await ReadCountsAsync();

        JsonNode first = JsonNode.Parse(await _stress.StressInputAsync(JumpAndButton, 20, 7, null, cancellationToken: cancellation))!;
        Counts afterFirst = await ReadCountsAsync();
        JsonNode again = JsonNode.Parse(await _stress.StressInputAsync(JumpAndButton, 20, 7, null, cancellationToken: cancellation))!;
        Counts afterAgain = await ReadCountsAsync();

        Assert.True(first["survived"]!.GetValue<bool>(), first.ToJsonString());
        Assert.True(again["survived"]!.GetValue<bool>(), again.ToJsonString());
        Assert.Equal(7, first["seed"]!.GetValue<int>());
        Assert.Equal(20, first["iterations"]!.GetValue<int>());
        Assert.Equal(first["drawn"]!.ToJsonString(), again["drawn"]!.ToJsonString());
        // Every entry of this pool plays: the action is in the project's InputMap and the element is a Control the game
        // has, so a refusal would leave the counts below one draw short.
        Assert.Empty(first["skipped"]!.AsArray());
        Assert.Empty(again["skipped"]!.AsArray());
        AssertDraws(first, before, afterFirst);
        AssertDraws(again, afterFirst, afterAgain);
        Assert.True(first["drawn"]!["actions"]!.GetValue<int>() > 0, first.ToJsonString());
        Assert.True(first["drawn"]!["elements"]!.GetValue<int>() > 0, first.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task NewErrorsCarryTheirFirstIteration()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await SetAsync("Main/SmallButton", "fail_on_press", "true");

        JsonNode result = JsonNode.Parse(
            await _stress.StressInputAsync(new StressPool(Elements: ["SmallButton"]), 5, 11, null, cancellationToken: cancellation)
        )!;
        await SetAsync("Main/SmallButton", "fail_on_press", "false");

        Assert.True(result["survived"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal(5, result["iterations"]!.GetValue<int>());
        JsonNode error = result["errors"]!.AsArray().Select(item => item!).First(item => item["file"]?.GetValue<string>() == "res://small_button.gd");
        // small_button.gd line 17: missing.call("free") on a null Object, raised by every press the click makes.
        Assert.Equal(17, error["line"]!.GetValue<int>());
        Assert.Equal(1, error["iteration"]!.GetValue<int>());
        Assert.Equal(5, error["count"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RefusedDrawsAreSkippedNotFatal()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Counts before = await ReadCountsAsync();

        JsonNode result = JsonNode.Parse(
            await _stress.StressInputAsync(new StressPool(Actions: ["probe_jump", "no_such_action"]), 20, 7, null, cancellationToken: cancellation)
        )!;
        Counts after = await ReadCountsAsync();

        Assert.True(result["survived"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal(20, result["iterations"]!.GetValue<int>());
        Assert.Equal(20, result["drawn"]!["actions"]!.GetValue<int>());
        JsonNode refused = Assert.Single(result["skipped"]!.AsArray().Select(item => item!));
        Assert.Equal("no_such_action", refused["entry"]!.GetValue<string>());
        Assert.Equal("actions", refused["kind"]!.GetValue<string>());
        // The bridge's own words for the refusal, its "event 0: " prefix from the event list included, and none of the
        // wrapper the connection adds around it.
        Assert.Equal("event 0: no input action 'no_such_action' in the project's InputMap", refused["reason"]!.GetValue<string>());
        Assert.InRange(refused["firstIteration"]!.GetValue<int>(), 1, 20);
        // Every draw was an action: the ones that reached the game are the jumps the probe counted, the rest were refused.
        Assert.Equal(20 - (after.Jumps - before.Jumps), refused["count"]!.GetValue<int>());
        Assert.Equal(0, after.Presses - before.Presses);
    }

    private static void AssertDraws(JsonNode result, Counts before, Counts after)
    {
        Assert.Equal(result["drawn"]!["actions"]!.GetValue<int>(), after.Jumps - before.Jumps);
        Assert.Equal(result["drawn"]!["elements"]!.GetValue<int>(), after.Presses - before.Presses);
    }

    private async Task<Counts> ReadCountsAsync()
    {
        JsonNode counts = await RunAsync(
            "return [scene_tree.root.get_node(\"Main/SmallButton\").press_count, scene_tree.root.get_node(\"Main/PadProbe\").jump_count]"
        );
        return new Counts(counts[0]!.GetValue<int>(), counts[1]!.GetValue<int>());
    }

    private async Task<JsonNode> SetAsync(string node, string property, string valueJson) =>
        JsonNode.Parse(
            await _tools.SetPropertyAsync(
                node,
                property,
                JsonSerializer.Deserialize<JsonElement>(valueJson),
                cancellationToken: TestContext.Current.CancellationToken
            )
        )!;

    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }

    private sealed record Counts(int Presses, int Jumps);
}
