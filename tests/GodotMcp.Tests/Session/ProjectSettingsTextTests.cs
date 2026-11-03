using System.Text.Json;
using GodotMcp.Server.Session;
using ModelContextProtocol;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The text a run's project settings are written as: the marker line, one section per setting name's first segment, each value
/// rendered as Godot's override.cfg parser reads it, and the refusals of a name or a value the file cannot carry.
/// </summary>
public sealed class ProjectSettingsTextTests
{
    private const string Call = "options.projectSettings";

    private const string FileSource = "\"projectSettings\"";

    private static readonly string[] Sources = [Call, FileSource];

    private const string ValueShape = "must be a string, a number or a boolean, or {\"raw\": \"<Godot value text>\"}.";

    private const string LineBreak = "holds a line break or control character; override.cfg takes one line per setting.";

    [Fact]
    public void AStringIsWrittenQuotedWithEveryEscape()
    {
        string block = Block(Call, ("x/value", Text("a\\b\"c\nd\re\tf")));

        Assert.Equal($"{ProjectSettingsText.BlockMarker}\n\n[x]\n\nvalue=\"a\\\\b\\\"c\\nd\\re\\tf\"\n", block);
    }

    [Fact]
    public void ABooleanAndANumberAreWrittenAsGodotReadsThem()
    {
        using var exponent = JsonDocument.Parse("1.5e3");

        string block = Block(Call, ("x/true", JsonSerializer.SerializeToElement(true)), ("x/false", JsonSerializer.SerializeToElement(false)));

        Assert.Equal($"{ProjectSettingsText.BlockMarker}\n\n[x]\n\nfalse=false\ntrue=true\n", block);
        Assert.Equal($"{ProjectSettingsText.BlockMarker}\n\n[x]\n\nvalue=30\n", Block(Call, ("x/value", JsonSerializer.SerializeToElement(30))));
        Assert.Equal($"{ProjectSettingsText.BlockMarker}\n\n[x]\n\nvalue=-3\n", Block(Call, ("x/value", JsonSerializer.SerializeToElement(-3))));
        Assert.Equal($"{ProjectSettingsText.BlockMarker}\n\n[x]\n\nvalue=1.5e3\n", Block(Call, ("x/value", exponent.RootElement)));
    }

    [Fact]
    public void ARawValueIsWrittenVerbatim()
    {
        string block = Block(Call, ("x/position", Raw("Vector2i(1280, 720)")), ("x/color", Raw("Color(1, 0, 0, 1)")));

        Assert.Equal($"{ProjectSettingsText.BlockMarker}\n\n[x]\n\ncolor=Color(1, 0, 0, 1)\nposition=Vector2i(1280, 720)\n", block);
    }

    [Fact]
    public void SettingsAreGroupedBySectionInOrdinalOrder()
    {
        string block = Block(Call, ("b/two", JsonSerializer.SerializeToElement(2)), ("a/z", JsonSerializer.SerializeToElement(1)));

        Assert.Equal($"{ProjectSettingsText.BlockMarker}\n\n[a]\n\nz=1\n\n[b]\n\ntwo=2\n", block);
        Assert.Equal(
            $"{ProjectSettingsText.BlockMarker}\n\n[a]\n\na=3\nz=1\n\n[b]\n\none=2\ntwo=2\n",
            Block(
                Call,
                ("b/two", JsonSerializer.SerializeToElement(2)),
                ("a/z", JsonSerializer.SerializeToElement(1)),
                ("b/one", JsonSerializer.SerializeToElement(2)),
                ("a/a", JsonSerializer.SerializeToElement(3))
            )
        );
    }

    [Fact]
    public void NoSettingsIsAnEmptyBlock()
    {
        Assert.Equal("", Block(Call));
        Assert.Equal("", ProjectSettingsText.Block([], FileSource, problem => new McpException(problem)));
    }

    [Fact]
    public void SummaryOfNoBlockIsNone() => Assert.Equal("none", ProjectSettingsText.Summary(""));

    [Fact]
    public void SummaryReadsBackEverySettingOfATwoSectionBlock()
    {
        string block = Block(Call, ("a/b", JsonSerializer.SerializeToElement(1)), ("c/d", Text("x")));

        Assert.Equal("a/b=1, c/d=\"x\"", ProjectSettingsText.Summary(block));
    }

    [Theory]
    [InlineData("")]
    [InlineData("quieten")]
    [InlineData("/value")]
    [InlineData("section/")]
    [InlineData("section/val=ue")]
    [InlineData("section/val[ue")]
    [InlineData("section/val]ue")]
    [InlineData("section/val;ue")]
    [InlineData("section/val\"ue")]
    [InlineData("section/val\u0001ue")]
    [InlineData("gmcp/my value")]
    [InlineData("gmcp/café")]
    [InlineData("a\\/b")]
    [InlineData(" /window/size/no_focus")]
    [InlineData("display/window/size/no_focus ")]
    public void ANameThatIsNotASectionAndAKeyIsRefused(string name)
    {
        foreach (string source in Sources)
        {
            Assert.Equal(
                $"{source} \"{name}\" must be a \"section/key\" project setting name, e.g. \"rendering/renderer/rendering_method\".",
                Refused(source, (name, JsonSerializer.SerializeToElement(1)))
            );
        }
    }

    [Fact]
    public void EverySettingTheBridgeOwnsIsRefused()
    {
        string[] owned =
        [
            "autoload/bridge",
            $"input_devices/{OverrideFile.IgnoreJoypadOnUnfocusedSetting}",
            $"display/{OverrideFile.NoFocusSetting}",
            $"display/{OverrideFile.InitialPositionTypeSetting}",
            $"display/{OverrideFile.InitialPositionSetting}",
            $"display/{OverrideFile.WindowModeSetting}",
            $"display/{OverrideFile.NoFocusSetting}.windows",
            $"input_devices/{OverrideFile.IgnoreJoypadOnUnfocusedSetting}.linux",
        ];
        const string listed =
            "autoload/*, input_devices/joypads/ignore_joypad_on_unfocused_application, display/window/size/no_focus, "
            + "display/window/size/initial_position_type, display/window/size/initial_position, display/window/size/mode";

        foreach (string name in owned)
        {
            foreach (string source in Sources)
            {
                Assert.Equal(
                    $"{source} cannot set \"{name}\": godot-mcp's override.cfg sets it for the bridge ({listed}).",
                    Refused(source, (name, JsonSerializer.SerializeToElement(1)))
                );
            }
        }
    }

    [Fact]
    public void ASettingNamedLikeABridgeSettingWithoutAFeatureTagIsWritten()
    {
        string block = Block(Call, ($"display/{OverrideFile.NoFocusSetting}_extra", JsonSerializer.SerializeToElement(1)));

        Assert.Equal($"{ProjectSettingsText.BlockMarker}\n\n[display]\n\nwindow/size/no_focus_extra=1\n", block);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[1, 2]")]
    [InlineData("{\"raw\": 5}")]
    [InlineData("{\"raw\": \"x\", \"other\": true}")]
    public void AValueThatIsNotAStringANumberABooleanOrARawStringIsRefused(string json)
    {
        using var document = JsonDocument.Parse(json);

        foreach (string source in Sources)
        {
            Assert.Equal($"{source} \"section/value\" {ValueShape}", Refused(source, ("section/value", document.RootElement)));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\u0001b")]
    public void ARawValueThatIsEmptyOrHoldsALineBreakIsRefused(string raw)
    {
        foreach (string source in Sources)
        {
            Assert.Equal($"{source} \"section/value\" {LineBreak}", Refused(source, ("section/value", Raw(raw))));
        }
    }

    [Fact]
    public void AStringValueHoldingAControlCharacterIsRefused()
    {
        foreach (string source in Sources)
        {
            Assert.Equal($"{source} \"section/value\" {LineBreak}", Refused(source, ("section/value", Text("a\u0001b"))));
            Assert.Equal($"{source} \"section/value\" {LineBreak}", Refused(source, ("section/value", Text("a\0b"))));
        }
    }

    /// <summary>The block these settings render to, of which a name or a value holds a refusal.</summary>
    private static string Block(string source, params (string Name, JsonElement Value)[] settings) =>
        ProjectSettingsText.Block(Entries(settings), source, problem => new McpException(problem));

    /// <summary>The refusal text these settings are refused with.</summary>
    private static string Refused(string source, params (string Name, JsonElement Value)[] settings) =>
        Assert.Throws<McpException>(() => Block(source, settings)).Message;

    private static IEnumerable<KeyValuePair<string, JsonElement>> Entries((string Name, JsonElement Value)[] settings) =>
        settings.Select(setting => new KeyValuePair<string, JsonElement>(setting.Name, setting.Value));

    private static JsonElement Text(string text) => JsonSerializer.SerializeToElement(text);

    /// <summary>A value Godot's own text reader takes, written as <c>{"raw": "..."}</c>.</summary>
    private static JsonElement Raw(string text) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { ["raw"] = JsonSerializer.SerializeToElement(text) });
}
