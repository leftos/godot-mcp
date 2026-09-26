using System.Buffers.Binary;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>take_screenshot, get_ui_elements and run_script against the InputProbe running in the real Godot.</summary>
public sealed class RuntimeReadTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;

    // main.tscn's RedSquare: a ColorRect of Color(1, 0, 0) at (400, 40), 120 x 80.
    private static readonly ScreenshotCrop RedSquare = new(400, 40, 120, 80);
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly RuntimeTools _tools;

    public RuntimeReadTests() => _tools = new RuntimeTools(_harness.Session);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task FullScreenshotIsTheViewportSizeShowsTheRedSquareAndStaysOutOfGit()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync(TestContext.Current.CancellationToken);

        List<ContentBlock> blocks = [.. await _tools.TakeScreenshotAsync("full", null, 960, cancellation)];
        JsonNode reply = JsonNode.Parse(Text(blocks))!;
        string path = reply["path"]!.GetValue<string>();
        byte[] png = Image(blocks);
        JsonNode viewport = await RunAsync("return scene_tree.root.get_visible_rect().size");
        JsonNode pixel = await RunAsync(
            $"var c := Image.load_from_file(\"{path.Replace('\\', '/')}\").get_pixel(460, 80)\n\treturn [c.r8, c.g8, c.b8]"
        );
        await _harness.Session.StopAsync(cancellation);

        Assert.Equal((viewport["x"]!.GetValue<double>(), viewport["y"]!.GetValue<double>()), PngSize(png));
        Assert.Equal(PngSize(png), (reply["width"]!.GetValue<double>(), reply["height"]!.GetValue<double>()));
        Assert.Equal(File.ReadAllBytes(path), png);
        Assert.Equal([255, 0, 0], pixel.AsArray().Select(channel => channel!.GetValue<int>()));
        Assert.Equal(Path.Combine(_probe.Directory, ".godot", "godot-mcp", "screenshots"), Path.GetDirectoryName(path));
        Assert.Equal(string.Empty, Git.Status(_probe.Directory));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CroppedScreenshotIsTheCropsSize()
    {
        await LaunchAsync(TestContext.Current.CancellationToken);

        List<ContentBlock> blocks = [.. await _tools.TakeScreenshotAsync("full", RedSquare, 960, TestContext.Current.CancellationToken)];

        Assert.Equal((120.0, 80.0), PngSize(Image(blocks)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task PreviewIsNoWiderThanPreviewMaxWidth()
    {
        await LaunchAsync(TestContext.Current.CancellationToken);

        List<ContentBlock> blocks = [.. await _tools.TakeScreenshotAsync("preview", null, 320, TestContext.Current.CancellationToken)];
        JsonNode reply = JsonNode.Parse(Text(blocks))!;

        Assert.True(PngSize(Image(blocks)).Width <= 320);
        Assert.True(reply["width"]!.GetValue<int>() > 320);
        Assert.True(File.Exists(reply["previewPath"]!.GetValue<string>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task PathOnlyScreenshotReturnsNoImage()
    {
        await LaunchAsync(TestContext.Current.CancellationToken);

        List<ContentBlock> blocks = [.. await _tools.TakeScreenshotAsync("path_only", null, 960, TestContext.Current.CancellationToken)];

        Assert.IsType<TextContentBlock>(Assert.Single(blocks));
        Assert.True(File.Exists(JsonNode.Parse(Text(blocks))!["path"]!.GetValue<string>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task UiElementsListTheLabelWithItsTextAndViewportRect()
    {
        await LaunchAsync(TestContext.Current.CancellationToken);

        string json = await _tools.GetUiElementsAsync(true, "Label", TestContext.Current.CancellationToken);

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
        await LaunchAsync(TestContext.Current.CancellationToken);

        JsonNode count = await RunAsync("return scene_tree.root.get_child_count()");

        // The root holds the bridge autoload and the main scene.
        Assert.Equal(2, count.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RunScriptReportsAParseErrorWithGodotsMessage()
    {
        await LaunchAsync(TestContext.Current.CancellationToken);

        McpException failed = await Assert.ThrowsAsync<McpException>(() => RunAsync("return 1 +"));

        Assert.Contains("did not compile", failed.Message, StringComparison.Ordinal);
        Assert.Contains("Parse Error", failed.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RunScriptReportsARuntimeErrorWithItsScriptErrorLine()
    {
        await LaunchAsync(TestContext.Current.CancellationToken);

        McpException failed = await Assert.ThrowsAsync<McpException>(() => RunAsync("var nothing: Variant = null\n\treturn nothing.foo"));

        Assert.Contains("SCRIPT ERROR", failed.Message, StringComparison.Ordinal);
        Assert.Contains("foo", failed.Message, StringComparison.Ordinal);
    }

    private Task<LaunchResult> LaunchAsync(CancellationToken cancellation) =>
        _harness.Session.LaunchAsync(new LaunchRequest(_probe.Directory, null, [], [], false), cancellation);

    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!;
    }

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
