using System.Buffers.Binary;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// take_screenshot, get_ui_elements and run_script against the InputProbe running in the real Godot: one shared run, reset
/// before each test, except for the test that stops its game.
/// </summary>
public sealed class RuntimeReadTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;

    // The machine's real pads take devices 0-3 (see DEVELOPMENT's footguns), so the injected pad keeps clear of them.
    private const int PadDevice = 7;

    // Adds MotionRecorder under the root: its _input keeps [position.x, position.y, relative.x, relative.y] of every mouse
    // motion carrying the bridge's injected mark (0x6D6370), in seen. The reset frees it with the other root children.
    private const string MotionRecorderScript =
        "var script := GDScript.new()\n\t"
        + "script.source_code = \"extends Node\\n\\nvar seen: Array = []\\n\\n\\nfunc _input(event: InputEvent) -> void:\\n\\t"
        + "if event is InputEventMouseMotion and event.device == 0x6D6370:\\n\\t\\t"
        + "seen.append([event.position.x, event.position.y, event.relative.x, event.relative.y])\\n\"\n\t"
        + "script.reload()\n\t"
        + "var recorder := Node.new()\n\t"
        + "recorder.name = \"MotionRecorder\"\n\t"
        + "recorder.set_script(script)\n\t"
        + "scene_tree.root.add_child(recorder)\n\t"
        + "return true";

    // Opens a PopupPanel of solid magenta, 90 x 50 window pixels, over the game; returns whether it is embedded and visible,
    // and its place and size in viewport pixels (through the inverse of the root's screen transform).
    private const string MagentaPopupScript =
        "var root := scene_tree.root\n\t"
        + "var style := StyleBoxFlat.new()\n\t"
        + "style.bg_color = Color(1, 0, 1)\n\t"
        + "var popup := PopupPanel.new()\n\t"
        + "popup.add_theme_stylebox_override(\"panel\", style)\n\t"
        + "root.add_child(popup)\n\t"
        + "popup.popup(Rect2i(root.position + Vector2i(100, 60), Vector2i(90, 50)))\n\t"
        + "var to_viewport := root.get_screen_transform().affine_inverse()\n\t"
        + "var place: Vector2 = to_viewport * Vector2(100, 60)\n\t"
        + "var size: Vector2 = to_viewport.basis_xform(Vector2(popup.size))\n\t"
        + "return {\"embedded\": popup.is_embedded(), \"visible\": popup.visible, \"x\": place.x, \"y\": place.y, "
        + "\"width\": size.x, \"height\": size.y}";

    // Where the root window and the popup are on the screen, and their sizes, for a failure message.
    private const string WindowPlacesScript =
        "var root := scene_tree.root\n\t"
        + "var popup: Window = root.get_child(root.get_child_count() - 1)\n\t"
        + "return {\"root\": [root.position, root.size], \"popup\": [popup.position, popup.size], "
        + "\"screen\": str(root.get_screen_transform()), \"windowList\": DisplayServer.get_window_list()}";

    // main.tscn's RedSquare: a ColorRect of Color(1, 0, 0) at (400, 40), 120 x 80.
    private static readonly ScreenshotCrop RedSquare = new(400, 40, 120, 80);
    private readonly SharedProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());

    public async ValueTask InitializeAsync() => await _shared.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task FullScreenshotIsTheViewportSizeShowsTheRedSquareAndStaysOutOfGit()
    {
        // The test stops its game to check the tree after the run, so it has a run of its own.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject probe = new();
        await using SessionHarness harness = new();
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        await harness.Sessions.LaunchAsync(new LaunchRequest(probe.Directory, null, [], [], true, false, Prepare: true), null, cancellation);

        List<ContentBlock> blocks = [.. await tools.TakeScreenshotAsync("full", null, 960, cancellationToken: cancellation)];
        JsonNode reply = JsonNode.Parse(Text(blocks))!;
        string path = reply["path"]!.GetValue<string>();
        byte[] png = Image(blocks);
        JsonNode viewport = (await RunForResultAsync(tools, "return scene_tree.root.get_visible_rect().size", cancellation))["value"]!;
        JsonNode pixel = (
            await RunForResultAsync(
                tools,
                $"var c := Image.load_from_file(\"{path.Replace('\\', '/')}\").get_pixel(460, 80)\n\treturn [c.r8, c.g8, c.b8]",
                cancellation
            )
        )["value"]!;
        await harness.Sessions.StopAsync(null, cancellation);

        Assert.Equal((viewport["x"]!.GetValue<double>(), viewport["y"]!.GetValue<double>()), PngSize(png));
        Assert.Equal(PngSize(png), (reply["width"]!.GetValue<double>(), reply["height"]!.GetValue<double>()));
        Assert.Equal(File.ReadAllBytes(path), png);
        Assert.Equal([255, 0, 0], pixel.AsArray().Select(channel => channel!.GetValue<int>()));
        Assert.Equal(Path.Combine(probe.Directory, ".godot", "godot-mcp", "screenshots"), Path.GetDirectoryName(path));
        Assert.Equal(string.Empty, Git.Status(probe.Directory));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TakeScreenshotIncludesANonEmbeddedPopup()
    {
        // With embedded subwindows off the popup is an OS window of its own, outside the root viewport's texture. A quiet
        // run's window sits at (0, 0) on the hidden desktop, so the popup opens where it was asked, at its offset from it.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject probe = new();
        string settings = File.ReadAllText(probe.ProjectFile);
        File.WriteAllText(
            probe.ProjectFile,
            settings.Replace("[display]", "[display]\nwindow/subwindows/embed_subwindows=false", StringComparison.Ordinal)
        );
        await using SessionHarness harness = new();
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        await harness.Sessions.LaunchAsync(new LaunchRequest(probe.Directory, null, [], [], true, false, Prepare: true), null, cancellation);

        JsonNode popup = (await RunForResultAsync(tools, MagentaPopupScript, cancellation))["value"]!;
        List<ContentBlock> blocks = [.. await tools.TakeScreenshotAsync("full", null, 960, cancellationToken: cancellation)];
        string path = JsonNode.Parse(Text(blocks))!["path"]!.GetValue<string>();
        JsonNode found = (await RunForResultAsync(tools, MagentaBoundsScript(path), cancellation))["value"]!;
        JsonNode windows = (await RunForResultAsync(tools, WindowPlacesScript, cancellation))["value"]!;
        await harness.Sessions.StopAsync(null, cancellation);

        Assert.Equal((false, true), (popup["embedded"]!.GetValue<bool>(), popup["visible"]!.GetValue<bool>()));
        Assert.True(
            found["found"]!.GetValue<bool>(),
            $"no magenta pixel in the screenshot; the popup: {popup.ToJsonString()}, the windows: {windows.ToJsonString()}"
        );
        Assert.InRange(found["width"]!.GetValue<double>(), popup["width"]!.GetValue<double>() - 1, popup["width"]!.GetValue<double>() + 1);
        Assert.InRange(found["height"]!.GetValue<double>(), popup["height"]!.GetValue<double>() - 1, popup["height"]!.GetValue<double>() + 1);
        Assert.True(
            Math.Abs(found["x"]!.GetValue<double>() - popup["x"]!.GetValue<double>()) <= 1
                && Math.Abs(found["y"]!.GetValue<double>() - popup["y"]!.GetValue<double>()) <= 1,
            $"the popup landed at {found.ToJsonString()}, asked at {popup.ToJsonString()}; the windows: {windows.ToJsonString()}"
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AResetUndoesPauseTimeScaleHeldInputAndSceneChanges()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await _tools.FrameControlAsync("pause", cancellationToken: cancellation);
        await _tools.FrameControlAsync("time_scale", scale: 0.5, cancellationToken: cancellation);
        await _tools.KeyAsync("A", "press", cancellationToken: cancellation);
        await _tools.MouseButtonAsync(new InputTarget(X: 600, Y: 330), "left", "press", cancellationToken: cancellation);
        await _tools.GamepadButtonAsync("A", "press", PadDevice, cancellationToken: cancellation);
        await _tools.GamepadAxisAsync("LEFT_X", 0.8, PadDevice, cancellationToken: cancellation);
        await RunAsync(
            "scene_tree.root.add_child(Node.new())\n\tscene_tree.root.get_node(\"Main/ProbeLabel\").text = \"changed\"\n\t"
                + "push_error(\"raised before the reset\")\n\treturn true",
            cancellation
        );
        JsonNode before = await GameStateAsync(cancellation);

        await _shared.ResetAsync(cancellation);
        JsonNode after = await GameStateAsync(cancellation);
        JsonNode errors = JsonNode.Parse(_tools.GetErrors(_shared.ErrorCursor))!;

        Assert.Equal((true, 0.5, true, 1, true), StateFlags(before));
        Assert.True(before["axis"]!.GetValue<double>() > 0.7, before.ToJsonString());
        Assert.Equal((3, "changed"), (before["children"]!.GetValue<int>(), before["label"]!.GetValue<string>()));
        Assert.Equal((false, 1.0, false, 0, false), StateFlags(after));
        Assert.Equal(0.0, after["axis"]!.GetValue<double>());
        Assert.Equal((2, "Probe ready"), (after["children"]!.GetValue<int>(), after["label"]!.GetValue<string>()));
        Assert.Empty(errors["errors"]!.AsArray());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AResetRelaunchesAStoppedGame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync("push_error(\"raised before the stop\")\n\treturn true", cancellation);
        await _shared.Sessions.StopAsync(null, cancellation);
        bool liveAfterStop = _shared.Sessions.List(includeStopped: true).Any(session => session.Live);

        await _shared.ResetAsync(cancellation);
        JsonNode scene = await RunAsync("return scene_tree.current_scene.scene_file_path", cancellation);
        JsonNode errors = JsonNode.Parse(_tools.GetErrors(_shared.ErrorCursor))!;

        Assert.False(liveAfterStop);
        Assert.Contains(_shared.Sessions.List(includeStopped: true), session => session.Live);
        Assert.Equal("res://main.tscn", scene.GetValue<string>());
        Assert.Empty(errors["errors"]!.AsArray());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARawMouseMotionAfterAResetStartsFromTheOrigin()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await _tools.SimulateInputAsync([RawMotion(300, 200)], cancellationToken: cancellation);

        await _shared.ResetAsync(cancellation);
        await RunAsync(MotionRecorderScript, cancellation);
        await _tools.SimulateInputAsync([RawMotion(200, 120)], cancellationToken: cancellation);
        JsonNode seen = await RunAsync("return scene_tree.root.get_node(\"MotionRecorder\").seen", cancellation);

        double[] motion = [.. Assert.Single(seen.AsArray())!.AsArray().Select(value => value!.GetValue<double>())];
        Assert.Equal((200.0, 120.0), (motion[0], motion[1]));
        Assert.Equal((motion[0], motion[1]), (motion[2], motion[3]));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CroppedScreenshotIsTheCropsSize()
    {
        List<ContentBlock> blocks =
        [
            .. await _tools.TakeScreenshotAsync("full", RedSquare, 960, cancellationToken: TestContext.Current.CancellationToken),
        ];

        Assert.Equal((120.0, 80.0), PngSize(Image(blocks)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task PreviewIsNoWiderThanPreviewMaxWidth()
    {
        List<ContentBlock> blocks =
        [
            .. await _tools.TakeScreenshotAsync("preview", null, 320, cancellationToken: TestContext.Current.CancellationToken),
        ];
        JsonNode reply = JsonNode.Parse(Text(blocks))!;

        Assert.True(PngSize(Image(blocks)).Width <= 320);
        Assert.True(reply["width"]!.GetValue<int>() > 320);
        Assert.True(File.Exists(reply["previewPath"]!.GetValue<string>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DefaultPreviewIsAtMost480PixelsWide()
    {
        List<ContentBlock> blocks = [.. await _tools.TakeScreenshotAsync(cancellationToken: TestContext.Current.CancellationToken)];
        JsonNode reply = JsonNode.Parse(Text(blocks))!;

        Assert.True(PngSize(Image(blocks)).Width <= 480);
        Assert.True(reply["width"]!.GetValue<int>() > 480);
        Assert.True(File.Exists(reply["path"]!.GetValue<string>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task PathOnlyScreenshotReturnsNoImage()
    {
        List<ContentBlock> blocks =
        [
            .. await _tools.TakeScreenshotAsync("path_only", null, 960, cancellationToken: TestContext.Current.CancellationToken),
        ];

        Assert.IsType<TextContentBlock>(Assert.Single(blocks));
        Assert.True(File.Exists(JsonNode.Parse(Text(blocks))!["path"]!.GetValue<string>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task UiElementsListTheLabelWithItsTextAndViewportRect()
    {
        string json = await _tools.GetUiElementsAsync(true, "Label", cancellationToken: TestContext.Current.CancellationToken);

        JsonArray elements = JsonNode.Parse(json)!["elements"]!.AsArray();
        Assert.All(elements, element => Assert.Equal("Label", element!["class"]!.GetValue<string>()));
        JsonNode label = Assert.Single(elements, element => element!["name"]!.GetValue<string>() == "ProbeLabel")!;
        Assert.Equal("ProbeLabel", label["name"]!.GetValue<string>());
        Assert.Equal("Label", label["class"]!.GetValue<string>());
        Assert.Equal("Probe ready", label["text"]!.GetValue<string>());
        Assert.Equal("/root/Main/ProbeLabel", label["path"]!.GetValue<string>());
        Assert.Equal(20.0, label["rect"]!["x"]!.GetValue<double>());
        Assert.Equal(20.0, label["rect"]!["y"]!.GetValue<double>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RunScriptReturnsTheRootsChildCount()
    {
        JsonNode count = await RunAsync("return scene_tree.root.get_child_count()", TestContext.Current.CancellationToken);

        // The root holds the bridge autoload and the main scene.
        Assert.Equal(2, count.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RunScriptReportsAParseErrorWithGodotsMessage()
    {
        McpException failed = await Assert.ThrowsAsync<McpException>(() => RunAsync("return 1 +", TestContext.Current.CancellationToken));

        Assert.Contains("did not compile", failed.Message, StringComparison.Ordinal);
        Assert.Contains("Parse Error", failed.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RunScriptReportsARuntimeErrorWithItsLocation()
    {
        McpException failed = await Assert.ThrowsAsync<McpException>(() =>
            RunAsync("var nothing: Variant = null\n\treturn nothing.foo", TestContext.Current.CancellationToken)
        );

        Assert.Contains("execute returned null and Godot reported errors", failed.Message, StringComparison.Ordinal);
        Assert.Contains("foo", failed.Message, StringComparison.Ordinal);
        // The body's second line, `return nothing.foo`, is line 6 of the script RunAsync wraps it in.
        Assert.True(failed.Message.Contains(":6 in execute", StringComparison.Ordinal), failed.Message);
        // InputProbe is a GDScript project: an invalid access there says nothing of the C# tools.
        Assert.DoesNotContain("cs_get", failed.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RunScriptReturnsItsValueAndThePushedErrors()
    {
        JsonNode result = await RunForResultAsync("push_error(\"probe pushed an error\")\n\treturn 7", TestContext.Current.CancellationToken);

        Assert.Equal(7, result["value"]!.GetValue<int>());
        JsonNode error = Assert.Single(result["errors"]!.AsArray())!;
        Assert.Equal("probe pushed an error", error["message"]!.GetValue<string>());
        Assert.Equal(5, error["line"]!.GetValue<int>());
        Assert.Equal("execute", error["function"]!.GetValue<string>());
        Assert.DoesNotContain("variant_utility", error["file"]?.GetValue<string>() ?? string.Empty, StringComparison.Ordinal);
        Assert.EndsWith(":5 in execute", error["stack"]![0]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnEngineErrorRaisedByAScriptIsLocatedAtTheScript()
    {
        JsonNode result = await RunForResultAsync("scene_tree.root.get_node(\"Missing\")\n\treturn 1", TestContext.Current.CancellationToken);

        JsonNode error = result["errors"]![0]!;
        Assert.StartsWith("gdscript://", error["file"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(5, error["line"]!.GetValue<int>());
        Assert.Contains(".cpp", error["engine"]?.GetValue<string>() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ANullValueWithAnUnrelatedErrorStillSucceeds()
    {
        JsonNode result = await RunForResultAsync(
            "scene_tree.root.get_node(\"Main\").probe_push_error()\n\treturn null",
            TestContext.Current.CancellationToken
        );

        Assert.True(result.AsObject().ContainsKey("value"));
        Assert.Null(result["value"]);
        JsonNode error = Assert.Single(result["errors"]!.AsArray())!;
        Assert.Equal("probe fixture error", error["message"]!.GetValue<string>());
        Assert.Equal("res://main.gd", error["file"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnErrorOnAWorkerThreadReachesTheFeed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        await RunAsync(
            "var id := WorkerThreadPool.add_task(func() -> void: push_error(\"from a thread\"))\n\t"
                + "WorkerThreadPool.wait_for_task_completion(id)\n\treturn 1",
            cancellation
        );

        // A worker's error may land after the bridge flushed for the reply, so it is looked for in the feed.
        Assert.True(
            await Poll.UntilAsync(
                () => _tools.GetErrors(_shared.ErrorCursor).Contains("from a thread", StringComparison.Ordinal),
                TimeSpan.FromSeconds(1),
                cancellation
            ),
            _tools.GetErrors(_shared.ErrorCursor)
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetErrorsReturnsErrorsAndWarningsFromACursor()
    {
        await RunAsync("push_warning(\"probe warning\")\n\tpush_error(\"probe error\")\n\treturn true", TestContext.Current.CancellationToken);
        JsonNode all = JsonNode.Parse(_tools.GetErrors(_shared.ErrorCursor))!;
        long next = all["next"]!.GetValue<long>();
        JsonNode later = JsonNode.Parse(_tools.GetErrors(next))!;

        JsonNode[] entries = [.. all["errors"]!.AsArray().Select(entry => entry!)];
        Assert.True(entries.Length >= 2, all.ToJsonString());
        Assert.Equal(("warning", "probe warning"), (entries[^2]["type"]!.GetValue<string>(), entries[^2]["message"]!.GetValue<string>()));
        Assert.Equal(("error", "probe error"), (entries[^1]["type"]!.GetValue<string>(), entries[^1]["message"]!.GetValue<string>()));
        Assert.Equal(entries[^1]["seq"]!.GetValue<long>(), next);
        Assert.Equal(entries[^2]["seq"]!.GetValue<long>() + 1, next);
        Assert.Empty(later["errors"]!.AsArray());
        Assert.Equal(next, later["next"]!.GetValue<long>());
        Assert.Equal(0, all["dropped"]!.GetValue<long>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RunScriptTimeoutStopsTheScript()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string counting =
            "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\tEngine.time_scale = 2.0\n\t"
            + "scene_tree.root.set_meta(\"stop_probe\", 0)\n\twhile true:\n\t\tawait scene_tree.process_frame\n\t\t"
            + "scene_tree.root.set_meta(\"stop_probe\", scene_tree.root.get_meta(\"stop_probe\") + 1)\n\treturn null\n";

        McpException stopped = await Assert.ThrowsAsync<McpException>(() => _tools.RunScriptAsync(counting, 1000, cancellationToken: cancellation));
        JsonNode after = await RunAsync(
            "var first: int = scene_tree.root.get_meta(\"stop_probe\")\n\tfor frame in 5:\n\t\tawait scene_tree.process_frame\n\t"
                + "var last: int = scene_tree.root.get_meta(\"stop_probe\")\n\tscene_tree.root.remove_meta(\"stop_probe\")\n\t"
                + "return [first, last, Engine.time_scale]",
            cancellation
        );

        Assert.StartsWith(
            "run_script timed out after 1 s and was stopped: its coroutine will not resume; restored Engine.time_scale to 1",
            stopped.Message,
            StringComparison.Ordinal
        );
        Assert.EndsWith("; a script that needs longer can raise timeoutMs.", stopped.Message, StringComparison.Ordinal);
        Assert.True(after[0]!.GetValue<int>() > 0, after.ToJsonString());
        Assert.Equal(after[0]!.GetValue<int>(), after[1]!.GetValue<int>());
        Assert.Equal(1.0, after[2]!.GetValue<double>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallMethodTimeoutSaysTheMethodKeepsRunning()
    {
        // Adds Holder under the root: its hold() speeds time up and then waits an hour. The reset frees it with the other
        // root children.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            "var script := GDScript.new()\n\t"
                + "script.source_code = \"extends Node\\n\\n\\nfunc hold() -> bool:\\n\\tEngine.time_scale = 2.0\\n\\t"
                + "await get_tree().create_timer(3600.0).timeout\\n\\treturn true\\n\"\n\t"
                + "script.reload()\n\tvar holder := Node.new()\n\tholder.name = \"Holder\"\n\tholder.set_script(script)\n\t"
                + "scene_tree.root.add_child(holder)\n\treturn true",
            cancellation
        );

        McpException forgotten = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CallMethodAsync("/root/Holder", "hold", options: new CallOptions(1000), cancellationToken: cancellation)
        );
        JsonNode timeScale = await RunAsync("return Engine.time_scale", cancellation);

        Assert.StartsWith(
            "call_method timed out after 1 s; it was no longer awaited: the method keeps running on its node; restored Engine.time_scale to 1",
            forgotten.Message,
            StringComparison.Ordinal
        );
        Assert.EndsWith(
            "; restart_project stops it; a method that needs longer can raise options.timeoutMs.",
            forgotten.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(1.0, timeScale.GetValue<double>());
    }

    private async Task<JsonNode> RunAsync(string body, CancellationToken cancellation) =>
        (await RunForResultAsync(_tools, body, cancellation))["value"]!;

    private Task<JsonNode> RunForResultAsync(string body, CancellationToken cancellation) => RunForResultAsync(_tools, body, cancellation);

    private static async Task<JsonNode> RunForResultAsync(RuntimeTools tools, string body, CancellationToken cancellation)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation);
        return JsonNode.Parse(json)!;
    }

    // What the reset test sets and the reset undoes; the pad is PadDevice.
    private Task<JsonNode> GameStateAsync(CancellationToken cancellation) =>
        RunAsync(
            "var root := scene_tree.root\n\treturn {\"paused\": scene_tree.paused, \"timeScale\": Engine.time_scale, "
                + "\"key\": Input.is_key_pressed(KEY_A), \"mouse\": Input.get_mouse_button_mask(), "
                + $"\"pad\": Input.is_joy_button_pressed({PadDevice}, JOY_BUTTON_A), \"axis\": Input.get_joy_axis({PadDevice}, JOY_AXIS_LEFT_X), "
                + "\"children\": root.get_child_count(), \"label\": root.get_node(\"Main/ProbeLabel\").text}",
            cancellation
        );

    private static (bool Paused, double TimeScale, bool Key, int Mouse, bool Pad) StateFlags(JsonNode state) =>
        (
            state["paused"]!.GetValue<bool>(),
            state["timeScale"]!.GetValue<double>(),
            state["key"]!.GetValue<bool>(),
            state["mouse"]!.GetValue<int>(),
            state["pad"]!.GetValue<bool>()
        );

    private static JsonObject RawMotion(double x, double y) =>
        new()
        {
            ["type"] = "mouse_motion",
            ["x"] = x,
            ["y"] = y,
        };

    // The bounds of the pure magenta pixels in the PNG at path: {found, width, height}.
    private static string MagentaBoundsScript(string path) =>
        $"var image := Image.load_from_file(\"{path.Replace('\\', '/')}\")\n\t"
        + "var low := Vector2i(image.get_width(), image.get_height())\n\t"
        + "var high := Vector2i(-1, -1)\n\t"
        + "for y in image.get_height():\n\t\t"
        + "for x in image.get_width():\n\t\t\t"
        + "var c := image.get_pixel(x, y)\n\t\t\t"
        + "if c.r8 == 255 and c.g8 == 0 and c.b8 == 255:\n\t\t\t\t"
        + "low = low.min(Vector2i(x, y))\n\t\t\t\t"
        + "high = high.max(Vector2i(x, y))\n\t"
        + "return {\"found\": high.x >= 0, \"x\": low.x, \"y\": low.y, \"width\": high.x - low.x + 1, \"height\": high.y - low.y + 1}";

    private static string Text(IEnumerable<ContentBlock> blocks) => string.Concat(blocks.OfType<TextContentBlock>().Select(block => block.Text));

    private static byte[] Image(IEnumerable<ContentBlock> blocks)
    {
        ImageContentBlock image = Assert.Single(blocks.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        return image.DecodedData.ToArray();
    }

    // A PNG's IHDR chunk follows the 8-byte signature and the chunk's length and type: width, then height, big-endian.
    private static (double Width, double Height) PngSize(byte[] png) =>
        (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
}
