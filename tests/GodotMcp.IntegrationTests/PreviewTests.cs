using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// preview_scene against the InputProbe fixture in the real Godot: a 2D scene's pixels, a camera framed for a 3D scene that has
/// none and not for one that has, a preview beside a live run, nothing left behind, and a non-scene refused before launch.
/// </summary>
public sealed class PreviewTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 90_000;

    // preview_3d.tscn's box: an unshaded BoxMesh of Color(1, 0, 1), beside an OmniLight3D of range 200 whose 400 m box,
    // framed too, would shrink the 1 m box to nothing.
    private static readonly (int R, int G, int B) Magenta = (255, 0, 255);

    // A 3D scene with its own current camera, 4 m in front of the same magenta box.
    private const string SceneWithCamera = """
        [gd_scene format=3]

        [sub_resource type="StandardMaterial3D" id="StandardMaterial3D_box"]
        shading_mode = 0
        albedo_color = Color(1, 0, 1, 1)

        [sub_resource type="BoxMesh" id="BoxMesh_box"]
        material = SubResource("StandardMaterial3D_box")

        [node name="WithCamera" type="Node3D"]

        [node name="Box" type="MeshInstance3D" parent="."]
        mesh = SubResource("BoxMesh_box")

        [node name="Camera" type="Camera3D" parent="."]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 4)
        current = true

        """;

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly PreviewTools _preview;
    private readonly RuntimeTools _tools;

    public PreviewTests()
    {
        _preview = new PreviewTools(_harness.Sessions);
        _tools = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A2DSceneShowsItsSwatchWhereTheSceneDrawsIt()
    {
        List<ContentBlock> blocks = await PreviewAsync("res://preview_2d.tscn", "full", TestContext.Current.CancellationToken);

        JsonNode reply = JsonNode.Parse(Text(blocks))!;
        var png = Png.Decode(Image(blocks));
        // preview_2d.tscn's Swatch: a ColorRect of Color(0, 1, 1) from (100, 80) to (300, 200) in the 640 x 360 viewport.
        Assert.Equal("res://preview_2d.tscn", reply["scene"]!.GetValue<string>());
        Assert.False(reply["cameraAdded"]!.GetValue<bool>());
        Assert.Equal((640, 360), (png.Width, png.Height));
        Assert.Equal((0, 255, 255), png.At(200, 140));
        Assert.NotEqual((0, 255, 255), png.At(20, 20));
        Assert.Equal("no-csproj", reply["prep"]!["build"]!.GetValue<string>());
        Assert.Equal("not-needed", reply["prep"]!["import"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A3DSceneWithoutACameraGetsOneThatShowsTheBox()
    {
        List<ContentBlock> blocks = await PreviewAsync("res://preview_3d.tscn", "full", TestContext.Current.CancellationToken);

        JsonNode reply = JsonNode.Parse(Text(blocks))!;
        var png = Png.Decode(Image(blocks));
        Assert.Equal("res://preview_3d.tscn", reply["scene"]!.GetValue<string>());
        Assert.True(reply["cameraAdded"]!.GetValue<bool>());
        int boxPixels = png.Count(Magenta, tolerance: 8);
        Assert.True(boxPixels > 2000, $"{boxPixels} magenta pixels of {png.Width * png.Height}");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A3DSceneWithItsOwnCameraIsShownThroughIt()
    {
        File.WriteAllText(Path.Combine(_probe.Directory, "preview_camera.tscn"), SceneWithCamera.ReplaceLineEndings("\n"));

        List<ContentBlock> blocks = await PreviewAsync("preview_camera.tscn", "full", TestContext.Current.CancellationToken);

        JsonNode reply = JsonNode.Parse(Text(blocks))!;
        int boxPixels = Png.Decode(Image(blocks)).Count(Magenta, tolerance: 8);
        Assert.Equal("res://preview_camera.tscn", reply["scene"]!.GetValue<string>());
        Assert.False(reply["cameraAdded"]!.GetValue<bool>());
        Assert.True(boxPixels > 500, $"{boxPixels} magenta pixels");
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task APreviewBesideALiveRunSucceedsAndLeavesTheRunRunning()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // Not quiet, so the preview (always quiet) differs from the live session on the folder.
        await _harness.Sessions.LaunchAsync(new LaunchRequest(_probe.Directory, null, [], [], false, false, Prepare: true), null, cancellation);

        List<ContentBlock> blocks = await PreviewAsync("res://preview_2d.tscn", "path_only", cancellation);

        Assert.True(File.Exists(JsonNode.Parse(Text(blocks))!["path"]!.GetValue<string>()));
        SessionInfo session = Assert.Single(_harness.Sessions.List(includeStopped: true));
        Assert.Equal("InputProbe", session.Name);
        Assert.True(session.Live);
        Assert.True(File.Exists(_probe.OverrideFile));
        string script = "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\treturn scene_tree.current_scene.name\n";
        string value = await _tools.RunScriptAsync(script, 10_000, cancellationToken: cancellation);
        Assert.Equal("""{"value":"Main"}""", value);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task APreviewLeavesNoSessionAndNoFilesAndAHeadlessToolRunsRightAfter()
    {
        await PreviewAsync("res://preview_2d.tscn", "preview", TestContext.Current.CancellationToken);

        Assert.Empty(_harness.Sessions.List(includeStopped: true));
        Assert.False(File.Exists(_probe.OverrideFile));
        Assert.Equal(string.Empty, Git.Status(_probe.Directory));
        string tree = await new HeadlessTools(_harness.Sessions).GetSceneFileTreeAsync(
            _probe.Directory,
            "res://preview_2d.tscn",
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Contains("Swatch", tree);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AScriptIsRefusedBeforeAnythingLaunches()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            PreviewAsync("res://main.gd", "preview", TestContext.Current.CancellationToken)
        );

        Assert.Contains("is not a scene file", refused.Message);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
        Assert.False(File.Exists(_probe.OverrideFile));
        Assert.False(Directory.Exists(Path.Combine(_probe.Directory, ".godot", "godot-mcp", "screenshots")));
    }

    private async Task<List<ContentBlock>> PreviewAsync(string scene, string responseMode, CancellationToken cancellation) =>
        [.. await _preview.PreviewSceneAsync(_probe.Directory, scene, null, responseMode, cancellationToken: cancellation)];

    private static string Text(IEnumerable<ContentBlock> blocks) => string.Concat(blocks.OfType<TextContentBlock>().Select(block => block.Text));

    private static byte[] Image(IEnumerable<ContentBlock> blocks)
    {
        ImageContentBlock image = Assert.Single(blocks.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        return image.DecodedData.ToArray();
    }

    /// <summary>An 8-bit, non-interlaced RGB or RGBA PNG, as Godot's save_png writes a viewport image, decoded to its pixels.</summary>
    private sealed record Png(int Width, int Height, int Channels, byte[] Pixels)
    {
        public (int R, int G, int B) At(int x, int y)
        {
            int start = ((y * Width) + x) * Channels;
            return (Pixels[start], Pixels[start + 1], Pixels[start + 2]);
        }

        /// <summary>How many pixels have every channel within <paramref name="tolerance"/> of the colour.</summary>
        public int Count((int R, int G, int B) colour, int tolerance)
        {
            int count = 0;
            for (int start = 0; start < Pixels.Length; start += Channels)
            {
                bool near =
                    Math.Abs(Pixels[start] - colour.R) <= tolerance
                    && Math.Abs(Pixels[start + 1] - colour.G) <= tolerance
                    && Math.Abs(Pixels[start + 2] - colour.B) <= tolerance;
                count += near ? 1 : 0;
            }

            return count;
        }

        public static Png Decode(byte[] data)
        {
            using MemoryStream compressed = new();
            ReadOnlySpan<byte> header = default;
            // A chunk: its length, its type, its body, then a CRC; the first follows the 8-byte signature.
            for (int offset = 8; offset < data.Length; offset += 12 + BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4)))
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
                string type = Encoding.ASCII.GetString(data, offset + 4, 4);
                header = type == "IHDR" ? data.AsSpan(offset + 8, length) : header;
                if (type == "IDAT")
                {
                    compressed.Write(data, offset + 8, length);
                }
            }

            int width = BinaryPrimitives.ReadInt32BigEndian(header[..4]);
            int height = BinaryPrimitives.ReadInt32BigEndian(header[4..8]);
            Assert.True(header[8] == 8 && header[12] == 0, "an 8-bit, non-interlaced PNG");
            int channels = header[9] switch
            {
                2 => 3,
                6 => 4,
                _ => throw new InvalidDataException($"PNG colour type {header[9]} is neither RGB nor RGBA."),
            };
            compressed.Position = 0;
            using ZLibStream inflate = new(compressed, CompressionMode.Decompress);
            using MemoryStream raw = new();
            inflate.CopyTo(raw);
            return new Png(width, height, channels, Unfilter(raw.ToArray(), width * channels, height, channels));
        }

        /// <summary>The pixel rows, each row's filter undone against the rows already restored.</summary>
        private static byte[] Unfilter(byte[] raw, int stride, int height, int channels)
        {
            byte[] pixels = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                byte filter = raw[y * (stride + 1)];
                for (int x = 0; x < stride; x++)
                {
                    int left = x >= channels ? pixels[(y * stride) + x - channels] : 0;
                    int up = y > 0 ? pixels[((y - 1) * stride) + x] : 0;
                    int upLeft = x >= channels && y > 0 ? pixels[((y - 1) * stride) + x - channels] : 0;
                    pixels[(y * stride) + x] = (byte)(raw[(y * (stride + 1)) + 1 + x] + Predict(filter, left, up, upLeft));
                }
            }

            return pixels;
        }

        private static int Predict(byte filter, int left, int up, int upLeft) =>
            filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => throw new InvalidDataException($"PNG filter {filter} is unknown."),
            };

        private static int Paeth(int left, int up, int upLeft)
        {
            int estimate = left + up - upLeft;
            int toLeft = Math.Abs(estimate - left);
            int toUp = Math.Abs(estimate - up);
            int toUpLeft = Math.Abs(estimate - upLeft);
            if (toLeft <= toUp && toLeft <= toUpLeft)
            {
                return left;
            }

            return toUp <= toUpLeft ? up : upLeft;
        }
    }
}
