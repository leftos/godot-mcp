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

    // main.tscn's RedSquare: a ColorRect of Color(1, 0, 0) at (400, 40), 120 x 80.
    private static readonly ScreenshotCrop RedSquare = new(400, 40, 120, 80);
    private readonly SharedProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions);

    public async ValueTask InitializeAsync() => await _shared.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task FullScreenshotIsTheViewportSizeShowsTheRedSquareAndStaysOutOfGit()
    {
        // The test stops its game to check the tree after the run, so it has a run of its own.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject probe = new();
        await using SessionHarness harness = new();
        RuntimeTools tools = new(harness.Sessions);
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
