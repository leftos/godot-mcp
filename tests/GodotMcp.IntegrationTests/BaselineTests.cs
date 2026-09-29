using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// save_screenshot_baseline and compare_screenshot against the InputProbe running in the real Godot: one shared run, reset
/// and cleared of baselines before each test, except for the test that relaunches at another size.
/// </summary>
public sealed class BaselineTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string Full = "probe_full";

    // main.tscn's RedSquare: a ColorRect of Color(1, 0, 0) from (400, 40) to (520, 120), 120 x 80 pixels in the 640 x 360 window.
    private const int SquarePixels = 120 * 80;
    private static readonly ScreenshotCrop Square = new(400, 40, 120, 80);
    private static readonly string[] Letterboxed = ["--resolution", "1000x900"];
    private readonly SharedProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());

    // Three tests save probe_full without overwrite, so each test starts with no baselines on the shared probe.
    public async ValueTask InitializeAsync()
    {
        await _shared.ResetAsync(TestContext.Current.CancellationToken);
        string baselines = BaselinesDirectory(_shared.ProbeDirectory);
        if (Directory.Exists(baselines))
        {
            Directory.Delete(baselines, recursive: true);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnUnchangedGameMatchesAndAChangedSquareIsFound()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonNode saved = JsonNode.Parse(await SaveAsync(Full, null, null, cancellation))!;
        IEnumerable<ContentBlock> same = await CompareAsync(Full, null, 2, cancellation);
        await SetSquareBlueAsync(cancellation);
        IEnumerable<ContentBlock> changed = await CompareAsync(Full, null, 2, cancellation);

        string baselinePath = Path.Combine(BaselinesDirectory(_shared.ProbeDirectory), "probe_full.png");
        Assert.Equal(Full, saved["name"]!.GetValue<string>());
        Assert.Equal(baselinePath, saved["baselinePath"]!.GetValue<string>());
        Assert.True(File.Exists(baselinePath));
        Assert.Null(saved["crop"]);
        int width = saved["width"]!.GetValue<int>();
        int height = saved["height"]!.GetValue<int>();
        JsonObject sidecar = ReadSidecar("probe_full");
        Assert.True(sidecar.ContainsKey("crop"));
        Assert.Null(sidecar["crop"]);
        Assert.Equal(width, sidecar["width"]!.GetValue<int>());
        Assert.Equal(height, sidecar["height"]!.GetValue<int>());

        JsonNode sameResult = Text(same);
        Assert.Equal(0, sameResult["changedPixels"]!.GetValue<int>());
        Assert.Equal(width * height, sameResult["totalPixels"]!.GetValue<int>());
        Assert.True(sameResult["match"]!.GetValue<bool>());
        Assert.Null(sameResult["bbox"]);
        Assert.Null(sameResult["diffPath"]);
        Assert.Empty(same.OfType<ImageContentBlock>());

        JsonNode changedResult = Text(changed);
        Assert.Equal(SquarePixels, changedResult["changedPixels"]!.GetValue<int>());
        Assert.False(changedResult["match"]!.GetValue<bool>());
        Assert.Equal("""{"x":400,"y":40,"width":120,"height":80}""", changedResult["bbox"]!.ToJsonString());
        string diffPath = changedResult["diffPath"]!.GetValue<string>();
        Assert.True(File.Exists(diffPath));
        Assert.Equal((width, height), PngSize(diffPath));
        Assert.True(File.Exists(changedResult["diffPreviewPath"]!.GetValue<string>()));
        Assert.Equal("image/png", Assert.Single(changed.OfType<ImageContentBlock>()).MimeType);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ACroppedBaselineComparesItsOwnCrop()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonNode saved = JsonNode.Parse(await SaveAsync("probe_square", Square, null, cancellation))!;
        JsonNode omitted = Text(await CompareAsync("probe_square", null, 2, cancellation));
        JsonNode repeated = Text(await CompareAsync("probe_square", Square, 2, cancellation));
        McpException other = await Assert.ThrowsAsync<McpException>(() => CompareAsync("probe_square", Square with { X = 0 }, 2, cancellation));
        await SetSquareBlueAsync(cancellation);
        JsonNode changed = Text(await CompareAsync("probe_square", null, 2, cancellation));

        Assert.Equal(120, saved["width"]!.GetValue<int>());
        Assert.Equal(80, saved["height"]!.GetValue<int>());
        Assert.Equal("""{"x":400,"y":40,"width":120,"height":80}""", saved["crop"]!.ToJsonString());
        JsonObject sidecar = ReadSidecar("probe_square");
        Assert.Equal("""{"x":400,"y":40,"width":120,"height":80}""", sidecar["crop"]!.ToJsonString());
        Assert.Equal(120, sidecar["width"]!.GetValue<int>());
        Assert.Equal(80, sidecar["height"]!.GetValue<int>());
        Assert.Equal(0, omitted["changedPixels"]!.GetValue<int>());
        Assert.Equal(SquarePixels, omitted["totalPixels"]!.GetValue<int>());
        Assert.Equal(0, repeated["changedPixels"]!.GetValue<int>());
        Assert.Contains("was saved with crop {x: 400, y: 40, width: 120, height: 80}", other.Message, StringComparison.Ordinal);
        Assert.Equal(1.0, changed["changedRatio"]!.GetValue<double>());
        Assert.False(changed["match"]!.GetValue<bool>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AOneLevelChangeIsWithinTheDefaultToleranceButNotZero()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await SaveAsync(Full, null, null, cancellation);
        await RunAsync("scene_tree.root.get_node(\"Main/RedSquare\").color = Color8(254, 0, 0)\n\treturn true", cancellation);
        JsonNode tolerant = Text(await CompareAsync(Full, null, 2, cancellation));
        JsonNode strict = Text(await CompareAsync(Full, null, 0, cancellation));

        Assert.Equal(0, tolerant["changedPixels"]!.GetValue<int>());
        Assert.True(tolerant["match"]!.GetValue<bool>());
        Assert.True(strict["changedPixels"]!.GetValue<int>() > 0, strict.ToJsonString());
        Assert.False(strict["match"]!.GetValue<bool>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AMissingBaselineAndAnExistingNameAreRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        McpException missing = await Assert.ThrowsAsync<McpException>(() => CompareAsync("absent", null, 2, cancellation));
        await SaveAsync(Full, null, null, cancellation);
        McpException existing = await Assert.ThrowsAsync<McpException>(() => SaveAsync(Full, null, null, cancellation));
        JsonNode replaced = JsonNode.Parse(await SaveAsync(Full, null, new BaselineOptions(Overwrite: true), cancellation))!;

        Assert.Contains("no baseline named 'absent'", missing.Message, StringComparison.Ordinal);
        Assert.Contains("save_screenshot_baseline", missing.Message, StringComparison.Ordinal);
        Assert.Contains("already exists", existing.Message, StringComparison.Ordinal);
        Assert.Equal(Full, replaced["name"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ABaselineOutlivesItsRunAndAnotherSizeFails()
    {
        // The test stops its game and launches another at another size, so it has runs of its own.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject probe = new();
        await using SessionHarness harness = new();
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        await harness.Sessions.LaunchAsync(new LaunchRequest(probe.Directory, null, [], [], true, false, Prepare: true), null, cancellation);
        JsonNode saved = JsonNode.Parse(await tools.SaveScreenshotBaselineAsync(Full, null, null, cancellationToken: cancellation))!;
        await StopAndCheckCleanAsync(harness, probe);

        await harness.Sessions.LaunchAsync(new LaunchRequest(probe.Directory, null, Letterboxed, [], true, false, Prepare: true), null, cancellation);
        McpException mismatch = await Assert.ThrowsAsync<McpException>(() =>
            tools.CompareScreenshotAsync(Full, null, 2, cancellationToken: cancellation)
        );
        await StopAndCheckCleanAsync(harness, probe);

        string baselineSize = $"{saved["width"]!.GetValue<int>()}x{saved["height"]!.GetValue<int>()}";
        // The 1000 x 900 window letterboxes the 640 x 360 content by 1.5625; the viewport's image is that content without the bars.
        Assert.True(
            mismatch.Message.Contains($"the screenshot is 1000x562 but baseline is {baselineSize}", StringComparison.Ordinal),
            mismatch.Message
        );
    }

    // Baselines, screenshots and diffs are written under the ignored .godot/, so the tree stays clean.
    private static async Task StopAndCheckCleanAsync(SessionHarness harness, ProbeProject probe)
    {
        await harness.Sessions.StopAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, Git.Status(probe.Directory));
    }

    private static string BaselinesDirectory(string probeDirectory) => Path.Combine(probeDirectory, ".godot", "godot-mcp", "baselines");

    private Task<string> SaveAsync(string name, ScreenshotCrop? crop, BaselineOptions? options, CancellationToken cancellation) =>
        _tools.SaveScreenshotBaselineAsync(name, crop, options, cancellationToken: cancellation);

    private Task<IEnumerable<ContentBlock>> CompareAsync(string name, ScreenshotCrop? crop, int tolerance, CancellationToken cancellation) =>
        _tools.CompareScreenshotAsync(name, crop, tolerance, cancellationToken: cancellation);

    private async Task SetSquareBlueAsync(CancellationToken cancellation) =>
        await _tools.SetPropertyAsync(
            "RedSquare",
            "color",
            JsonSerializer.Deserialize<JsonElement>("""{"r": 0, "g": 0, "b": 1}"""),
            cancellationToken: cancellation
        );

    private async Task RunAsync(string body, CancellationToken cancellation)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation);
    }

    // The sidecar save_screenshot_baseline writes beside <name>.png; its savedAt must be an ISO-8601 UTC time.
    private JsonObject ReadSidecar(string name)
    {
        string path = Path.Combine(BaselinesDirectory(_shared.ProbeDirectory), name + ".json");
        JsonObject sidecar = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        string savedAt = sidecar["savedAt"]!.GetValue<string>();
        var parsed = DateTimeOffset.Parse(savedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.True((DateTimeOffset.UtcNow - parsed).Duration() < TimeSpan.FromMinutes(5), savedAt);
        return sidecar;
    }

    private static JsonNode Text(IEnumerable<ContentBlock> blocks) =>
        JsonNode.Parse(string.Concat(blocks.OfType<TextContentBlock>().Select(block => block.Text)))!;

    // A PNG's IHDR chunk follows its 8-byte signature and 8-byte chunk header: width, then height, big-endian.
    private static (int Width, int Height) PngSize(string path)
    {
        byte[] header = new byte[24];
        using FileStream stream = File.OpenRead(path);
        stream.ReadExactly(header);
        return (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)));
    }
}
