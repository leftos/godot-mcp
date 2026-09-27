using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// save_screenshot_baseline's and compare_screenshot's checks, which refuse before anything reaches a game, the baseline
/// folder's rules and the comparison's result; no Godot runs here.
/// </summary>
public sealed class BaselineValidationTests : IDisposable
{
    private static readonly ScreenshotCrop Square = new(400, 40, 120, 80);
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly RuntimeTools _tools;
    private readonly TempDirectory _temp = new();

    public BaselineValidationTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        _tools = new RuntimeTools(_sessions, TestCSharp.Unused());
    }

    public static TheoryData<string> MalformedNames => ["", new string('a', 65), "a/b", "..\\x", "a b", ".hidden"];

    public void Dispose()
    {
        _sessions.Dispose();
        _listener.Dispose();
        _temp.Dispose();
    }

    [Theory]
    [MemberData(nameof(MalformedNames))]
    public async Task AMalformedNameIsRefused(string name)
    {
        string expected = $"baseline name '{name}' is not valid: use 1-64 letters, digits, '.', '_' or '-', starting with a letter or digit.";

        McpException saved = await Assert.ThrowsAsync<McpException>(() => _tools.SaveScreenshotBaselineAsync(name, cancellationToken: Token));
        McpException compared = await Assert.ThrowsAsync<McpException>(() => _tools.CompareScreenshotAsync(name, cancellationToken: Token));

        Assert.Equal(expected, saved.Message);
        Assert.Equal(expected, compared.Message);
    }

    [Fact]
    public async Task ASixtyFourCharacterNameIsAccepted()
    {
        string name = new('a', 64);

        McpException saved = await Assert.ThrowsAsync<McpException>(() => _tools.SaveScreenshotBaselineAsync(name, cancellationToken: Token));
        McpException compared = await Assert.ThrowsAsync<McpException>(() => _tools.CompareScreenshotAsync(name, cancellationToken: Token));

        Assert.Contains("No Godot session", saved.Message, StringComparison.Ordinal);
        Assert.Contains("No Godot session", compared.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.png")]
    [InlineData("Com1")]
    [InlineData("lpt9.x")]
    public async Task AWindowsDeviceNameIsRefused(string name)
    {
        string expected = $"baseline name '{name}' is a reserved Windows device name.";

        McpException saved = await Assert.ThrowsAsync<McpException>(() => _tools.SaveScreenshotBaselineAsync(name, cancellationToken: Token));
        McpException compared = await Assert.ThrowsAsync<McpException>(() => _tools.CompareScreenshotAsync(name, cancellationToken: Token));

        Assert.Equal(expected, saved.Message);
        Assert.Equal(expected, compared.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public async Task AToleranceOutOfRangeIsRefused(int tolerance)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CompareScreenshotAsync("probe", tolerance: tolerance, cancellationToken: Token)
        );

        Assert.Equal($"tolerance takes values from 0 to 255; got {tolerance}.", refused.Message);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public async Task AMaxChangedRatioOutOfRangeIsRefused(double ratio)
    {
        CompareOptions options = new(MaxChangedRatio: ratio);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CompareScreenshotAsync("probe", options: options, cancellationToken: Token)
        );

        Assert.StartsWith("maxChangedRatio takes values from 0 to 1; got ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownResponseModeOrANarrowPreviewIsRefused()
    {
        McpException mode = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CompareScreenshotAsync("probe", options: new CompareOptions(ResponseMode: "thumbnail"), cancellationToken: Token)
        );
        McpException width = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CompareScreenshotAsync("probe", options: new CompareOptions(PreviewMaxWidth: 0), cancellationToken: Token)
        );

        Assert.StartsWith("responseMode 'thumbnail'", mode.Message, StringComparison.Ordinal);
        Assert.StartsWith("previewMaxWidth", width.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    public async Task ACropUnderOnePixelIsRefused(int width, int height)
    {
        ScreenshotCrop crop = new(0, 0, width, height);

        McpException saved = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SaveScreenshotBaselineAsync("probe", crop, cancellationToken: Token)
        );
        McpException compared = await Assert.ThrowsAsync<McpException>(() => _tools.CompareScreenshotAsync("probe", crop, cancellationToken: Token));

        Assert.StartsWith("A crop needs a width and a height of at least 1 pixel", saved.Message, StringComparison.Ordinal);
        Assert.StartsWith("A crop needs a width and a height of at least 1 pixel", compared.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidCallsWithoutASessionAreRefused()
    {
        CompareOptions options = new(MaxChangedRatio: 1, ResponseMode: "full", PreviewMaxWidth: 1);

        McpException saved = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SaveScreenshotBaselineAsync("probe_full", Square, new BaselineOptions(Overwrite: true), cancellationToken: Token)
        );
        McpException compared = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CompareScreenshotAsync("probe-1.a", Square, 255, options, cancellationToken: Token)
        );

        Assert.Contains("No Godot session", saved.Message, StringComparison.Ordinal);
        Assert.Contains("No Godot session", compared.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingBaselineIsRefusedListingTheBaselinesThere()
    {
        string folder = _temp.Combine("baselines");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "title.png"), "");
        File.WriteAllText(Path.Combine(folder, "menu.png"), "");
        File.WriteAllText(Path.Combine(folder, "menu.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "");

        McpException refused = Assert.Throws<McpException>(() => ScreenshotBaseline.RequireBaseline(folder, "hud"));

        Assert.Equal($"no baseline named 'hud' in {folder}; save one with save_screenshot_baseline. Baselines here: menu, title.", refused.Message);
    }

    [Fact]
    public void AMissingBaselineFolderListsNone()
    {
        string folder = _temp.Combine("absent");

        McpException refused = Assert.Throws<McpException>(() => ScreenshotBaseline.RequireBaseline(folder, "hud"));

        Assert.Equal($"no baseline named 'hud' in {folder}; save one with save_screenshot_baseline. Baselines here: none.", refused.Message);
    }

    [Fact]
    public void AnExistingBaselineIsFound()
    {
        string folder = _temp.Path;
        File.WriteAllText(Path.Combine(folder, "hud.png"), "");

        Assert.Equal(Path.Combine(folder, "hud.png"), ScreenshotBaseline.RequireBaseline(folder, "hud"));
    }

    [Fact]
    public void SavingOverAnExistingNameNeedsOverwrite()
    {
        string folder = _temp.Path;
        string existing = Path.Combine(folder, "hud.png");
        File.WriteAllText(existing, "");

        McpException refused = Assert.Throws<McpException>(() => ScreenshotBaseline.CheckCanSave(folder, "hud", overwrite: false));

        Assert.Equal($"a baseline named 'hud' already exists at {existing}; pass options.overwrite: true to replace it.", refused.Message);
        Assert.Equal(existing, ScreenshotBaseline.CheckCanSave(folder, "hud", overwrite: true));
        Assert.Equal(Path.Combine(folder, "new.png"), ScreenshotBaseline.CheckCanSave(folder, "new", overwrite: false));
    }

    [Fact]
    public void ACropDifferentFromTheStoredOneIsRefused()
    {
        McpException differs = Assert.Throws<McpException>(() => ScreenshotBaseline.ResolveCrop("hud", Square, Square with { X = 401 }));
        McpException none = Assert.Throws<McpException>(() => ScreenshotBaseline.ResolveCrop("hud", null, Square));

        Assert.Equal(
            "baseline 'hud' was saved with crop {x: 400, y: 40, width: 120, height: 80}; compare with the same crop or omit it.",
            differs.Message
        );
        Assert.Equal("baseline 'hud' was saved with crop none; compare with the same crop or omit it.", none.Message);
    }

    [Fact]
    public void TheStoredCropIsUsedWhenTheCallGivesNoneOrTheSame()
    {
        Assert.Equal(Square, ScreenshotBaseline.ResolveCrop("hud", Square, null));
        Assert.Equal(Square, ScreenshotBaseline.ResolveCrop("hud", Square, new ScreenshotCrop(400, 40, 120, 80)));
        Assert.Null(ScreenshotBaseline.ResolveCrop("hud", null, null));
    }

    [Theory]
    [InlineData(0, 0.0, true)]
    [InlineData(96, 0.0, false)]
    [InlineData(96, 0.01, true)]
    [InlineData(97, 0.01, false)]
    public void TheComparisonIsShapedFromTheBridgesReply(int changed, double maxChangedRatio, bool match)
    {
        JsonObject reply = CannedReply(changed, withPreview: changed > 0);

        JsonObject shaped = RuntimeTools.ShapeComparison("hud", @"C:\game\baselines\hud.png", 2, maxChangedRatio, reply);

        Assert.Equal("hud", shaped["name"]!.GetValue<string>());
        Assert.Equal(@"C:\game\baselines\hud.png", shaped["baselinePath"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath("C:/game/.godot/godot-mcp/screenshots/shot.png"), shaped["path"]!.GetValue<string>());
        Assert.Equal(120, shaped["width"]!.GetValue<int>());
        Assert.Equal(80, shaped["height"]!.GetValue<int>());
        Assert.Equal(2, shaped["tolerance"]!.GetValue<int>());
        Assert.Equal(changed, shaped["changedPixels"]!.GetValue<int>());
        Assert.Equal(9600, shaped["totalPixels"]!.GetValue<int>());
        Assert.Equal(changed / 9600.0, shaped["changedRatio"]!.GetValue<double>());
        Assert.Equal(match, shaped["match"]!.GetValue<bool>());
        if (changed == 0)
        {
            Assert.True(shaped.ContainsKey("bbox"));
            Assert.Null(shaped["bbox"]);
            Assert.False(shaped.ContainsKey("diffPath"));
            Assert.False(shaped.ContainsKey("diffPreviewPath"));
        }
        else
        {
            Assert.Equal("""{"x":400,"y":40,"width":12,"height":8}""", shaped["bbox"]!.ToJsonString());
            Assert.Equal(Path.GetFullPath(DiffPath), shaped["diffPath"]!.GetValue<string>());
            Assert.Equal(Path.GetFullPath(DiffPreviewPath), shaped["diffPreviewPath"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("""{"path": "C:/game/shot.png", "width": 1, "height": 1, "changedPixels": 0}""")]
    [InlineData("""{"path": "C:/game/shot.png", "width": 1, "height": 1, "changedPixels": 0, "totalPixels": 0}""")]
    [InlineData("""{"path": "C:/game/shot.png", "width": 1, "height": 1, "changedPixels": 0, "totalPixels": "many"}""")]
    [InlineData("""{"path": "C:/game/shot.png", "width": 1, "height": 1, "totalPixels": 9600}""")]
    public void AComparisonReplyWithoutPixelCountsIsRefused(string replyJson)
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.ShapeComparison("hud", @"C:\game\baselines\hud.png", 2, 1, JsonNode.Parse(replyJson))
        );

        Assert.StartsWith("The bridge's compare_screenshot reply has no pixel counts", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"changedPixels": 0, "totalPixels": 9600}""")]
    [InlineData("null")]
    [InlineData("[]")]
    public void AComparisonReplyWithoutAFileNamesTheCommand(string replyJson)
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.ShapeComparison("hud", @"C:\game\baselines\hud.png", 2, 1, JsonNode.Parse(replyJson))
        );

        Assert.StartsWith("The bridge's compare_screenshot reply names no file", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDiffImageFollowsTheResponseMode()
    {
        JsonObject withPreview = Shape(CannedReply(96, withPreview: true));
        JsonObject withoutPreview = Shape(CannedReply(96, withPreview: false));
        string diff = Path.GetFullPath(DiffPath);
        string preview = Path.GetFullPath(DiffPreviewPath);

        Assert.Null(RuntimeTools.DiffImagePath(withPreview, ScreenshotMode.PathOnly));
        Assert.Equal(preview, RuntimeTools.DiffImagePath(withPreview, ScreenshotMode.Preview));
        Assert.Equal(diff, RuntimeTools.DiffImagePath(withoutPreview, ScreenshotMode.Preview));
        Assert.Equal(diff, RuntimeTools.DiffImagePath(withPreview, ScreenshotMode.Full));
    }

    [Fact]
    public void NothingChangedReturnsNoDiffImage()
    {
        JsonObject unchanged = Shape(CannedReply(0, withPreview: false));
        JsonObject strayDiff = Shape(CannedReply(0, withPreview: true));

        Assert.Null(RuntimeTools.DiffImagePath(unchanged, ScreenshotMode.Preview));
        Assert.Null(RuntimeTools.DiffImagePath(unchanged, ScreenshotMode.Full));
        Assert.Null(RuntimeTools.DiffImagePath(strayDiff, ScreenshotMode.Preview));
        Assert.Null(RuntimeTools.DiffImagePath(strayDiff, ScreenshotMode.Full));
    }

    private const string DiffPath = "C:/game/.godot/godot-mcp/screenshots/shot_diff.png";
    private const string DiffPreviewPath = "C:/game/.godot/godot-mcp/screenshots/shot_diff_preview.png";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static JsonObject Shape(JsonObject reply) => RuntimeTools.ShapeComparison("hud", @"C:\game\baselines\hud.png", 2, 0, reply);

    // A bridge reply as GDScript's JSON writes it, every number a float; with withPreview, diff paths even when nothing changed.
    private static JsonObject CannedReply(int changed, bool withPreview)
    {
        JsonObject reply = new()
        {
            ["path"] = "C:/game/.godot/godot-mcp/screenshots/shot.png",
            ["width"] = 120.0,
            ["height"] = 80.0,
            ["changedPixels"] = (double)changed,
            ["totalPixels"] = 9600.0,
            ["bbox"] =
                changed == 0
                    ? null
                    : new JsonObject
                    {
                        ["x"] = 400.0,
                        ["y"] = 40.0,
                        ["width"] = 12.0,
                        ["height"] = 8.0,
                    },
        };
        if (changed > 0 || withPreview)
        {
            reply["diffPath"] = DiffPath;
        }

        if (withPreview)
        {
            reply["diffPreviewPath"] = DiffPreviewPath;
        }

        return reply;
    }
}
