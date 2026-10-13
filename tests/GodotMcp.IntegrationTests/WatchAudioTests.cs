using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// watch's audio players against the InputProbe: an AudioRig under the root holding pausable AudioStreamPlayers (2D and 3D
/// ones for a start's position), each with a
/// 440 Hz AudioStreamWAV tone run_script builds, played by a button's click or by run_script, on Master or on a bus a test
/// adds; and watch's bus levels, asserted with inequalities only, since a quiet run mixes in bursts of about 93 ms. One
/// shared quiet run (the Dummy driver mixes it, so nothing is heard), reset before each test (added buses removed, Master
/// and the bridge's mute restored), after which a watch a failed test left behind is stopped.
/// </summary>
public sealed class WatchAudioTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;
    private const int ToneMixRate = 22_050;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());

    public async ValueTask InitializeAsync()
    {
        await shared.ResetAsync(TestContext.Current.CancellationToken);
        try
        {
            await WatchAsync("stop", TestContext.Current.CancellationToken);
        }
        catch (McpException)
        {
            // No watch was left running: the usual case.
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AClickWhoseHandlerPlaysAToneIsAStartThenAnEndFinished()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string rig = await AddRigAsync(
            "var button := Button.new()\n\tbutton.name = \"ToneButton\"\n\tbutton.text = \"Tone\"\n\t"
                + "button.focus_mode = Control.FOCUS_NONE\n\tbutton.action_mode = BaseButton.ACTION_MODE_BUTTON_PRESS\n\t"
                + "button.position = Vector2(540, 300)\n\tbutton.size = Vector2(80, 40)\n\tbutton.pressed.connect(tone.play)\n\t"
                + "scene_tree.root.add_child(button)\n\t",
            ("Tone", 250, 1)
        );
        string tone = $"{rig}/Tone";

        JsonObject started = await StartAsync(rig, cancellation);
        await _tools.ClickAsync(new InputTarget("ToneButton"), "left", false, cancellationToken: cancellation);
        // The press plays the tone before the click answers, so the tone's end is read from its state, which a slow round
        // trip cannot miss as it could miss the finished signal.
        await _tools.WaitForAsync(new WaitCondition(Node: tone, Expression: "not node.has_stream_playback()"), cancellationToken: cancellation);
        await WaitFramesAsync(3, cancellation);
        JsonObject audio = await StopAsync(cancellation);

        JsonObject snapshot = started["audio"]!.AsObject();
        Assert.False(string.IsNullOrEmpty(snapshot["driver"]!.GetValue<string>()), snapshot.ToJsonString());
        Assert.True(snapshot["mixRate"]!.GetValue<double>() > 0, snapshot.ToJsonString());
        Assert.Contains(snapshot["bridgeMuted"]!.GetValueKind(), new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Equal(1, Int(snapshot["players"]!["count"]));
        Assert.Empty(snapshot["players"]!["playing"]!.AsArray());
        JsonArray voices = audio["voices"]!.AsArray();
        Assert.Equal(["start", "end"], Events(voices));
        Assert.All(voices, voice => Assert.Equal(tone, voice![3]!.GetValue<string>()));
        Assert.Equal("AudioStreamWAV", voices[0]![4]!["stream"]!["class"]!.GetValue<string>());
        Assert.Equal("Master", voices[0]![4]!["bus"]!.GetValue<string>());
        Assert.Equal("finished", voices[1]![4]!["reason"]!.GetValue<string>());
        Assert.True(Int(voices[1]![0]) > Int(voices[0]![0]), voices.ToJsonString());
        Assert.Equal((1, 1), Counts(audio, tone));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AQueueFreeWhilePlayingEndsFreed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string rig = await AddRigAsync("", ("Doomed", 2000, 1));
        string doomed = $"{rig}/Doomed";

        await StartAsync(rig, cancellation);
        await CallOnAsync(doomed, "play");
        await WaitFramesAsync(3, cancellation);
        await CallOnAsync(doomed, "queue_free");
        await WaitFramesAsync(3, cancellation);
        JsonArray voices = (await StopAsync(cancellation))["voices"]!.AsArray();

        Assert.Equal(["start", "end"], Events(voices));
        Assert.Equal("freed", voices[1]![4]!["reason"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AStopEndsStopped()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string rig = await AddRigAsync("", ("Halted", 2000, 1));
        string halted = $"{rig}/Halted";

        await StartAsync(rig, cancellation);
        await CallOnAsync(halted, "play");
        await WaitFramesAsync(3, cancellation);
        await CallOnAsync(halted, "stop");
        await WaitFramesAsync(3, cancellation);
        JsonArray voices = (await StopAsync(cancellation))["voices"]!.AsArray();

        Assert.Equal(["start", "end"], Events(voices));
        Assert.Equal("stopped", voices[1]![4]!["reason"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task FrameControlPauseReadsPauseAndResumeReadsResume()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string rig = await AddRigAsync("", ("Music", 3000, 1));
        string music = $"{rig}/Music";

        await StartAsync(rig, cancellation);
        await CallOnAsync(music, "play");
        await WaitFramesAsync(3, cancellation);
        await _tools.FrameControlAsync("pause", cancellationToken: cancellation);
        // wait_for refuses to wait while the game is paused, but process_frame is still emitted, so the watch has read
        // paused frames once three have passed.
        await RunAsync("for _frame in 3:\n\t\tawait scene_tree.process_frame\n\treturn true");
        await _tools.FrameControlAsync("resume", cancellationToken: cancellation);
        await WaitFramesAsync(3, cancellation);
        JsonObject audio = await StopAsync(cancellation);

        Assert.Equal(["start", "pause", "resume"], Events(audio["voices"]!.AsArray()));
        Assert.Equal((1, 0), Counts(audio, music));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AFireAndForgetPlayerThatOutlivesItsFirstFrameIsSeen()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string rig = await AddRigAsync("");

        JsonObject started = await StartAsync(rig, cancellation);
        string shot = (
            await RunAsync(
                $"var rig: Node = scene_tree.root.get_node(\"{rig}\")\n\t"
                    + PlayerLines("Shot", 250, 1, "rig", "AudioStreamPlayer")
                    + "shot.finished.connect(shot.queue_free)\n\tshot.play()\n\treturn str(shot.get_path())"
            )
        ).GetValue<string>();
        await _tools.WaitForAsync(new WaitCondition(Node: shot, Exists: false), cancellationToken: cancellation);
        await WaitFramesAsync(3, cancellation);
        JsonObject audio = await StopAsync(cancellation);

        Assert.Equal(0, Int(started["audio"]!["players"]!["count"]));
        JsonArray voices = audio["voices"]!.AsArray();
        Assert.Equal(["start", "end"], Events(voices));
        Assert.All(voices, voice => Assert.Equal(shot, voice![3]!.GetValue<string>()));
        Assert.Equal("finished", voices[1]![4]!["reason"]!.GetValue<string>());
        Assert.Equal(1, Int(audio["playersFollowed"]));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TwoPlaysOnAPolyphonicPlayerAreTwoStarts()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string rig = await AddRigAsync("", ("Poly", 2000, 2));
        string poly = $"{rig}/Poly";

        await StartAsync(rig, cancellation);
        await CallOnAsync(poly, "play");
        await WaitFramesAsync(3, cancellation);
        await CallOnAsync(poly, "play");
        await WaitFramesAsync(3, cancellation);
        JsonObject audio = await StopAsync(cancellation);

        JsonArray voices = audio["voices"]!.AsArray();
        Assert.Equal(["start", "start"], Events(voices));
        Assert.All(voices, voice => Assert.Equal(2, Int(voice![4]!["maxPolyphony"])));
        Assert.Equal((2, 0), Counts(audio, poly));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A2DAndA3DStartCarryThePlayersGlobalPosition()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string rig = await AddRigAsync(
            "var stage_2d := Node2D.new()\n\tstage_2d.name = \"Stage2D\"\n\tstage_2d.position = Vector2(100, 50)\n\t"
                + "rig.add_child(stage_2d)\n\t"
                + PlayerLines("Flat", 2000, 1, "stage_2d", "AudioStreamPlayer2D")
                + "flat.position = Vector2(10, 5)\n\t"
                + "var stage_3d := Node3D.new()\n\tstage_3d.name = \"Stage3D\"\n\tstage_3d.position = Vector3(1, 2, 3)\n\t"
                + "rig.add_child(stage_3d)\n\t"
                + PlayerLines("Deep", 2000, 1, "stage_3d", "AudioStreamPlayer3D")
                + "deep.position = Vector3(0, 0, 1)\n\t"
        );
        string flat = $"{rig}/Stage2D/Flat";
        string deep = $"{rig}/Stage3D/Deep";

        await StartAsync(rig, cancellation);
        await CallOnAsync(flat, "play");
        await CallOnAsync(deep, "play");
        await WaitFramesAsync(3, cancellation);
        JsonArray voices = (await StopAsync(cancellation))["voices"]!.AsArray();

        JsonNode flatAt = StartOf(voices, flat)["position"]!;
        Assert.Equal((110.0, 55.0), (flatAt["x"]!.GetValue<double>(), flatAt["y"]!.GetValue<double>()));
        JsonNode deepAt = StartOf(voices, deep)["position"]!;
        Assert.Equal((1.0, 2.0, 4.0), (deepAt["x"]!.GetValue<double>(), deepAt["y"]!.GetValue<double>(), deepAt["z"]!.GetValue<double>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AToneOnAnAddedBusIsHeardFromItsStartAndNotBefore()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string rig = await AddRigAsync(BusLines("Effects") + "tone.bus = &\"Effects\"\n\t", ("Tone", 500, 1));
        string tone = $"{rig}/Tone";

        await StartAsync(new WatchAudioTrack(new WatchAudioPlayers(rig), ["Effects"]), cancellation);
        await WaitFramesAsync(3, cancellation);
        await CallOnAsync(tone, "play");
        await WaitUntilSilentAsync(tone, cancellation);
        await WaitFramesAsync(3, cancellation);
        JsonObject audio = await StopAsync(cancellation);

        JsonObject effects = Bus(audio, "Effects");
        JsonArray heard = effects["heard"]!.AsArray();
        Assert.NotEmpty(heard);
        Assert.InRange(Int(effects["heardFrames"]), 1, Int(effects["sampledFrames"]) - 1);
        Assert.True(effects["peakMaxDb"]!.GetValue<double>() > -60, effects.ToJsonString());
        Assert.Equal(0, Int(effects["volumeDb"]!["changes"]));
        JsonArray voices = audio["voices"]!.AsArray();
        Assert.Equal(["start", "end"], Events(voices));
        Assert.InRange(Int(heard[0]![0]), Int(voices[0]![0]), Int(voices[1]![0]));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ABusAtMinus80AndAMutedBusAreNotHeardThoughTheirVoicesStart()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string rig = await AddRigAsync(
            BusLines("Quiet")
                + "AudioServer.set_bus_volume_db(AudioServer.get_bus_index(\"Quiet\"), -80.0)\n\t"
                + BusLines("Muted")
                + "AudioServer.set_bus_mute(AudioServer.get_bus_index(\"Muted\"), true)\n\t"
                + "soft.bus = &\"Quiet\"\n\tsilent.bus = &\"Muted\"\n\t",
            ("Soft", 500, 1),
            ("Silent", 500, 1)
        );

        await StartAsync(new WatchAudioTrack(new WatchAudioPlayers(rig), ["Quiet", "Muted"]), cancellation);
        await CallOnAsync($"{rig}/Soft", "play");
        await CallOnAsync($"{rig}/Silent", "play");
        await WaitUntilSilentAsync($"{rig}/Soft", cancellation);
        await WaitUntilSilentAsync($"{rig}/Silent", cancellation);
        await WaitFramesAsync(3, cancellation);
        JsonObject audio = await StopAsync(cancellation);

        Assert.Equal(2, Events(audio["voices"]!.AsArray()).Count(voiceEvent => voiceEvent == "start"));
        JsonObject quiet = Bus(audio, "Quiet");
        JsonObject muted = Bus(audio, "Muted");
        Assert.All([quiet, muted], bus => Assert.Equal((0, 0), (bus["heard"]!.AsArray().Count, Int(bus["heardFrames"]))));
        Assert.All([quiet, muted], bus => Assert.True(Int(bus["sampledFrames"]) > 0, bus.ToJsonString()));
        Assert.Equal(-80.0, quiet["volumeDb"]!["first"]!.GetValue<double>());
        Assert.True(muted["mute"]!["first"]!.GetValue<bool>() && muted["mute"]!["last"]!.GetValue<bool>(), muted.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MasterUnderTheBridgeMuteWarnsWhileItsChildBusIsStillHeard()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string rig = await AddRigAsync(BusLines("Effects") + "tone.bus = &\"Effects\"\n\t", ("Tone", 500, 1));
        string tone = $"{rig}/Tone";
        await SetBridgeMutedAsync(true);
        try
        {
            JsonObject started = await StartAsync(new WatchAudioTrack(new WatchAudioPlayers(rig), ["Master", "Effects"]), cancellation);
            await CallOnAsync(tone, "play");
            await WaitUntilSilentAsync(tone, cancellation);
            await WaitFramesAsync(3, cancellation);
            JsonObject timeline = await WatchAsync("stop", cancellation);

            Assert.True(started["audio"]!["bridgeMuted"]!.GetValue<bool>(), started.ToJsonString());
            Assert.Contains("Master read -200 dB while the bridge muted it", timeline["warning"]?.GetValue<string>() ?? "", StringComparison.Ordinal);
            JsonObject audio = timeline["audio"]!.AsObject();
            JsonObject master = Bus(audio, "Master");
            Assert.Empty(master["heard"]!.AsArray());
            Assert.True(master["peakMaxDb"]!.GetValue<double>() < -100, master.ToJsonString());
            Assert.True(Int(Bus(audio, "Effects")["heardFrames"]) > 0, audio.ToJsonString());
        }
        finally
        {
            await SetBridgeMutedAsync(false);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnUnknownBusIsListedMissingAndSkippedNotRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonObject started = await StartAsync(new WatchAudioTrack(Buses: ["Nowhere"]), cancellation);
        await WaitFramesAsync(3, cancellation);
        JsonObject audio = await StopAsync(cancellation);

        JsonObject listed = started["audio"]!["buses"]!.AsArray().Single()!.AsObject();
        Assert.Equal(("Nowhere", true, 2), (listed["name"]!.GetValue<string>(), listed["missing"]!.GetValue<bool>(), listed.Count));
        JsonObject nowhere = Bus(audio, "Nowhere");
        Assert.Equal((0, 0), (Int(nowhere["sampledFrames"]), nowhere["heard"]!.AsArray().Count));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ABusesOnlyWatchListsEveryBusAndNoVoices()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(BusLines("Effects") + "return true");

        JsonObject started = await StartAsync(new WatchAudioTrack(Buses: ["*"]), cancellation);
        await WaitFramesAsync(3, cancellation);
        JsonObject audio = await StopAsync(cancellation);

        JsonObject snapshot = started["audio"]!.AsObject();
        Assert.False(snapshot.ContainsKey("players"), snapshot.ToJsonString());
        JsonArray buses = snapshot["buses"]!.AsArray();
        Assert.Equal(["Master", "Effects"], buses.Select(bus => bus!["name"]!.GetValue<string>()));
        Assert.Equal((0, "Master"), (Int(buses[0]!["index"]), buses[1]!["send"]!.GetValue<string>()));
        Assert.False(audio.ContainsKey("voices"), audio.ToJsonString());
        Assert.True(Int(Bus(audio, "Master")["sampledFrames"]) >= 3, audio.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATimeScaleOtherThanOneAddsTheWarning()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync("Engine.time_scale = 2.0\n\treturn true");
        try
        {
            await StartAsync(new WatchAudioTrack(Buses: ["Master"]), cancellation);
            await WaitFramesAsync(3, cancellation);
            JsonObject timeline = await WatchAsync("stop", cancellation);

            Assert.Contains(
                "Engine.time_scale was not 1 in a sampled frame",
                timeline["warning"]?.GetValue<string>() ?? "",
                StringComparison.Ordinal
            );
        }
        finally
        {
            await RunAsync("Engine.time_scale = 1.0\n\treturn true");
        }
    }

    /// <summary>The {stream, bus, ...} of the player's first start in <paramref name="voices"/>.</summary>
    private static JsonNode StartOf(JsonArray voices, string player)
    {
        JsonNode? start = voices.FirstOrDefault(voice => voice![2]!.GetValue<string>() == "start" && voice[3]!.GetValue<string>() == player);
        Assert.NotNull(start);
        return start[4]!;
    }

    private async Task<JsonObject> StartAsync(string rig, CancellationToken cancellation) =>
        await StartAsync(new WatchAudioTrack(new WatchAudioPlayers(rig)), cancellation);

    private async Task<JsonObject> StartAsync(WatchAudioTrack audio, CancellationToken cancellation) =>
        await WatchAsync("start", cancellation, new WatchTracks(Audio: audio));

    /// <summary>The bus named <paramref name="name"/> in the timeline's audio.buses.</summary>
    private static JsonObject Bus(JsonObject audio, string name) =>
        audio["buses"]!.AsArray().Single(bus => bus!["name"]!.GetValue<string>() == name)!.AsObject();

    /// <summary>Waits until the player holds no playback: its tone has ended.</summary>
    private async Task WaitUntilSilentAsync(string player, CancellationToken cancellation) =>
        await _tools.WaitForAsync(new WaitCondition(Node: player, Expression: "not node.has_stream_playback()"), cancellationToken: cancellation);

    /// <summary>Turns the bridge's Master mute (its Audio child, which the mute launch option sets) on or off.</summary>
    private async Task SetBridgeMutedAsync(bool on) =>
        await RunAsync($"scene_tree.root.get_node(\"GodotMcpBridge/Audio\").set_muted({(on ? "true" : "false")})\n\treturn true");

    /// <summary>GDScript lines adding a bus named <paramref name="name"/> at the end of the layout, sending to Master.</summary>
    private static string BusLines(string name) =>
        $"AudioServer.add_bus()\n\tAudioServer.set_bus_name(AudioServer.get_bus_count() - 1, \"{name}\")\n\t"
        + "AudioServer.set_bus_send(AudioServer.get_bus_count() - 1, \"Master\")\n\t";

    /// <summary>Stops the watch and returns its audio.</summary>
    private async Task<JsonObject> StopAsync(CancellationToken cancellation) => (await WatchAsync("stop", cancellation))["audio"]!.AsObject();

    private async Task<JsonObject> WatchAsync(string action, CancellationToken cancellation, WatchTracks? tracks = null) =>
        JsonNode.Parse(await _tools.WatchAsync(action, tracks, null, null, cancellationToken: cancellation))!.AsObject();

    private async Task WaitFramesAsync(int frames, CancellationToken cancellation) =>
        await _tools.WaitForAsync(new WaitCondition(Frames: frames), cancellationToken: cancellation);

    private async Task CallOnAsync(string node, string method) => await RunAsync($"scene_tree.root.get_node(\"{node}\").{method}()\n\treturn true");

    /// <summary>
    /// Adds AudioRig under the root holding a pausable AudioStreamPlayer per (name, ms, polyphony), each with its own tone of ms
    /// milliseconds, a variable named as the player in lower case; then runs <paramref name="more"/>, which may use those
    /// variables; returns the rig's path.
    /// </summary>
    private async Task<string> AddRigAsync(string more, params (string Name, int Ms, int Polyphony)[] players)
    {
        StringBuilder body = new("var rig := Node.new()\n\trig.name = \"AudioRig\"\n\tscene_tree.root.add_child(rig)\n\t");
        foreach ((string name, int ms, int polyphony) in players)
        {
            body.Append(PlayerLines(name, ms, polyphony, "rig", "AudioStreamPlayer"));
        }

        body.Append(more).Append("return str(rig.get_path())");
        return (await RunAsync(body.ToString())).GetValue<string>();
    }

    /// <summary>GDScript lines adding a pausable <paramref name="playerClass"/> named <paramref name="name"/> under the node in
    /// the variable <paramref name="parent"/>, held in a variable named as it in lower case, with a 440 Hz 16-bit tone of
    /// <paramref name="ms"/> milliseconds on the Master bus.</summary>
    internal static string PlayerLines(string name, int ms, int polyphony, string parent, string playerClass)
    {
        string player = name.ToLowerInvariant();
        int samples = ToneMixRate * ms / 1000;
        return $"var {player}_data := PackedByteArray()\n\t{player}_data.resize({samples * 2})\n\t"
            + $"for i in {samples}:\n\t\t{player}_data.encode_s16(i * 2, int(sin(TAU * 440.0 * i / {ToneMixRate}.0) * 8000.0))\n\t"
            + $"var {player}_tone := AudioStreamWAV.new()\n\t{player}_tone.format = AudioStreamWAV.FORMAT_16_BITS\n\t"
            + $"{player}_tone.mix_rate = {ToneMixRate}\n\t{player}_tone.data = {player}_data\n\t"
            + $"var {player} := {playerClass}.new()\n\t{player}.name = \"{name}\"\n\t"
            + $"{player}.process_mode = Node.PROCESS_MODE_PAUSABLE\n\t{player}.max_polyphony = {polyphony}\n\t"
            + $"{player}.stream = {player}_tone\n\t{parent}.add_child({player})\n\t";
    }

    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }

    private static string[] Events(JsonArray voices) => [.. voices.Select(voice => voice![2]!.GetValue<string>())];

    /// <summary>The player's (starts, ends) in voiceCounts.</summary>
    private static (int Starts, int Ends) Counts(JsonObject audio, string player)
    {
        JsonNode counts = audio["voiceCounts"]![player]!;
        return (Int(counts["starts"]), Int(counts["ends"]));
    }

    // GDScript's JSON may write an integer as a float, so numbers are read as doubles.
    private static int Int(JsonNode? node) => (int)node!.GetValue<double>();
}
