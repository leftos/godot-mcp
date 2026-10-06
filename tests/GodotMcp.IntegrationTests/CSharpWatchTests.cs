using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// watch's signal tracks against the CsProbe: a CsSignals node (tests/fixtures/CsProbe/CsSignals.cs) added under /root as
/// Signals, whose [Signal]s of 0, 3 and 6 arguments its EmitAll emits once each, called as the watch's options.call.
/// </summary>
public sealed class CSharpWatchTests(SharedCsProbeSession shared) : IClassFixture<SharedCsProbeSession>
{
    private const int TestTimeoutMs = 150_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string Signals = "/root/Signals";

    private const string AddSignalsScript = """
        extends RefCounted


        func execute(scene_tree: SceneTree) -> Variant:
        	if not scene_tree.root.has_node("Signals"):
        		var node: Node = load("res://CsSignals.cs").new()
        		node.name = "Signals"
        		scene_tree.root.add_child(node)
        	return true

        """;

    private readonly RuntimeTools _tools = new(shared.Sessions, shared.Bridge);

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CSharpSignalsOfZeroThreeAndSixArgumentsAreRecordedWithTheirValues()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonNode added = JsonNode.Parse(await _tools.RunScriptAsync(AddSignalsScript, ScriptTimeoutMs, cancellationToken: cancellation))!;
        Assert.True(added["value"]?.GetValue<bool>() == true, added.ToJsonString());
        WatchTracks tracks = new(
            Signals:
            [
                new WatchSignalTrack(Node: Signals, Signal: "Pinged"),
                new WatchSignalTrack(Node: Signals, Signal: "Hit"),
                new WatchSignalTrack(Node: Signals, Signal: "Dealt"),
            ]
        );

        JsonObject timeline = JsonNode
            .Parse(
                await _tools.WatchAsync(
                    "run",
                    tracks,
                    new WatchWindow(Frames: 5),
                    new WatchOptions(Call: new MethodCall(Signals, "EmitAll")),
                    cancellationToken: cancellation
                )
            )!
            .AsObject();

        JsonArray events = timeline["events"]!.AsArray();
        Assert.Equal(["Pinged", "Hit", "Dealt"], events.Select(item => item![3]!.GetValue<string>()));
        // EmitAll runs in the watch's first frame, before its first sample: frame 0.
        Assert.All(events, item => Assert.Equal((0, Signals), (Int(item![0]), item[2]!.GetValue<string>())));
        Assert.Equal("[]", events[0]![4]!.ToJsonString());
        JsonArray hit = events[1]![4]!.AsArray();
        Assert.Equal((3, "sam", 1, 2), (Int(hit[0]), hit[1]!.GetValue<string>(), Int(hit[2]!["x"]), Int(hit[2]!["y"])));
        JsonArray dealt = events[2]![4]!.AsArray();
        Assert.Equal(6, dealt.Count);
        Assert.Equal(
            (2, 0.5, "ace", 3, 4),
            (Int(dealt[0]), dealt[1]!.GetValue<double>(), dealt[2]!.GetValue<string>(), Int(dealt[3]!["x"]), Int(dealt[3]!["y"]))
        );
        Assert.Contains(Signals, dealt[4]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("""[1,"two"]""", dealt[5]!.ToJsonString().Replace("1.0", "1", StringComparison.Ordinal));
        Assert.Equal(1, Int(timeline["eventCounts"]![$"{Signals}:Dealt"]));
    }

    // GDScript's JSON may write an integer as a float, so numbers are read as doubles.
    private static int Int(JsonNode? node) => (int)node!.GetValue<double>();
}
