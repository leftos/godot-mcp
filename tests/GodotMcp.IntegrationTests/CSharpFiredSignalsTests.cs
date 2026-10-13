using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// A click on the CsProbe's CsFiredButton (tests/fixtures/CsProbe/CsFiredButton.cs), a Button whose own press handler
/// raises its [Signal] Played and which subscribes to that signal with +=, added under /root as Played by a run_script.
/// The fired list holds Played with no listeners, since Godot cannot see a C# += on a [Signal], before the engine's
/// pressed with its one C# subscriber, and the Played handler ran once.
/// </summary>
public sealed class CSharpFiredSignalsTests(SharedCsProbeSession shared) : IClassFixture<SharedCsProbeSession>
{
    private const int TestTimeoutMs = 150_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string Played = "/root/Played";

    private const string AddPlayedScript = """
        extends RefCounted


        func execute(scene_tree: SceneTree) -> Variant:
        	if not scene_tree.root.has_node("Played"):
        		var button: Button = load("res://CsFiredButton.cs").new()
        		button.name = "Played"
        		button.text = "Played"
        		button.position = Vector2(400, 250)
        		button.size = Vector2(120, 40)
        		scene_tree.root.add_child(button)
        	await scene_tree.process_frame
        	await scene_tree.process_frame
        	return true

        """;

    private readonly RuntimeTools _tools = new(shared.Sessions, shared.Bridge);

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ACSharpSignalIsListedWithoutAListenerBeforeTheEnginePressedWithItsSubscriber()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonNode added = JsonNode.Parse(await _tools.RunScriptAsync(AddPlayedScript, ScriptTimeoutMs, cancellationToken: cancellation))!;
        Assert.True(added["value"]?.GetValue<bool>() == true, added.ToJsonString());

        JsonNode clicked = JsonNode.Parse(await _tools.ClickAsync(new InputTarget("Played"), "left", false, cancellationToken: cancellation))!;

        AssertOrder(clicked, (Played, "Played"), (Played, "pressed"));
        JsonObject played = Find(clicked, Played, "Played");
        Assert.True(played.ContainsKey("listeners"), clicked.ToJsonString());
        Assert.Null(played["listeners"]);
        Assert.Equal(1, Find(clicked, Played, "pressed")["listeners"]!.GetValue<int>());
        Assert.Contains(Played, ListenedOn(clicked));
        Assert.Equal(1, Int(await RunAsync("return int(scene_tree.root.get_node(\"Played\").get_meta(\"plays\"))", cancellation)));
    }

    private static void AssertOrder(JsonNode result, params (string Node, string Signal)[] expected)
    {
        JsonObject[] fired = Entries(result);
        int[] indexes = [.. expected.Select(item => IndexOf(fired, item.Node, item.Signal))];
        bool ordered = indexes.All(index => index >= 0) && indexes.Zip(indexes.Skip(1)).All(pair => pair.First < pair.Second);
        Assert.True(ordered, $"expected {string.Join(", ", expected)} in that order: {result.ToJsonString()}");
    }

    private static JsonObject Find(JsonNode result, string node, string signal)
    {
        JsonObject? entry = Entries(result).FirstOrDefault(item => Is(item, node, signal));
        Assert.True(entry is not null, $"no {signal} on {node}: {result.ToJsonString()}");
        return entry;
    }

    private static int IndexOf(JsonObject[] fired, string node, string signal) => Array.FindIndex(fired, entry => Is(entry, node, signal));

    private static bool Is(JsonObject entry, string node, string signal) => Node(entry) == node && entry["signal"]!.GetValue<string>() == signal;

    private static string Node(JsonObject entry) => entry["node"]!.GetValue<string>();

    private static JsonObject[] Entries(JsonNode result) => [.. result["fired"]!.AsArray().Select(entry => entry!.AsObject())];

    private static string[] ListenedOn(JsonNode result) => [.. result["listenedOn"]!.AsArray().Select(path => path!.GetValue<string>())];

    // GDScript's JSON may write an integer as a float, so numbers are read as doubles.
    private static int Int(JsonNode? node) => (int)node!.GetValue<double>();

    private async Task<JsonNode> RunAsync(string body, CancellationToken cancellation)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation);
        return JsonNode.Parse(json)!["value"]!;
    }
}
