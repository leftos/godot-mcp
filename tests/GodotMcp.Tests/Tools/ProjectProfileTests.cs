using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

public sealed class ProjectProfileTests : IDisposable
{
    private const string TopLevelKeys = "scene, userArgs, engineArgs, resolution, quiet, presets";
    private const string PresetKeys = "scene, userArgs, engineArgs, resolution, quiet, session";

    private const string Layered = """
        {
          "scene": "res://top.tscn",
          "userArgs": ["--top-user"],
          "engineArgs": ["--top-engine"],
          "resolution": "1280x720",
          "quiet": false,
          "presets": {
            "client": {
              "scene": "res://client.tscn",
              "userArgs": ["--preset-user"],
              "engineArgs": ["--preset-engine"],
              "resolution": "640x360",
              "quiet": true,
              "session": "client1"
            },
            "bare": {}
          }
        }
        """;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string FilePath => _temp.Combine(ProjectProfile.FileName);

    [Fact]
    public void AMissingFileIsAnEmptyProfile()
    {
        ProfileLaunch launch = Merge(new RunOptions());

        Assert.Null(launch.Request.Scene);
        Assert.Empty(launch.Request.UserArgs);
        Assert.Empty(launch.Request.EngineArgs);
        Assert.True(launch.Request.Quiet);
        Assert.Null(launch.Session);
        Assert.Equal(_temp.Path, launch.Request.ProjectPath);
    }

    [Fact]
    public void APresetWithNoFileIsRefused()
    {
        string message = Refused(() => Merge(new RunOptions(Preset: "server")));

        Assert.Contains(FilePath, message, StringComparison.Ordinal);
        Assert.Contains("\"server\"", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownPresetIsRefusedWithTheNames()
    {
        Write("""{ "presets": { "server": {}, "client": {} } }""");
        string unknown = Refused(() => Merge(new RunOptions(Preset: "host")));
        Write("""{ "scene": "res://main.tscn" }""");
        string none = Refused(() => Merge(new RunOptions(Preset: "host")));

        Assert.Contains(FilePath, unknown, StringComparison.Ordinal);
        Assert.Contains("\"host\"", unknown, StringComparison.Ordinal);
        Assert.Contains("client, server", unknown, StringComparison.Ordinal);
        Assert.Contains(FilePath, none, StringComparison.Ordinal);
        Assert.Contains("no presets", none, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "sceen": "res://main.tscn" }""", "sceen", TopLevelKeys, "")]
    [InlineData("""{ "session": "server" }""", "session", TopLevelKeys, "; session belongs inside a preset")]
    [InlineData("""{ "presets": { "server": { "presets": {} } } }""", "presets", PresetKeys, "")]
    [InlineData("""{ "presets": { "server": { "prepare": "never" } } }""", "prepare", PresetKeys, "")]
    public void AnUnknownKeyIsRefusedWithTheAllowedKeys(string json, string key, string allowed, string hint)
    {
        Write(json);

        string message = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.Contains(FilePath, message, StringComparison.Ordinal);
        Assert.Contains($"\"{key}\"", message, StringComparison.Ordinal);
        Assert.Contains(allowed + hint, message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidJsonIsRefusedWithThePath()
    {
        Write("{\n  \"scene\":\n}");

        string message = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.Contains(FilePath, message, StringComparison.Ordinal);
        Assert.Contains("(line 3, column 1)", message, StringComparison.Ordinal);
        Assert.Contains("is an invalid start of a value", message, StringComparison.Ordinal);
        Assert.DoesNotContain("LineNumber:", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "quiet": "yes" }""", "(top level): \"quiet\" must be true or false, not a string.")]
    [InlineData("""{ "userArgs": "x" }""", "(top level): \"userArgs\" must be an array, not a string.")]
    [InlineData("""{ "userArgs": [1] }""", "(top level): \"userArgs\" must be an array of strings; it holds a number.")]
    [InlineData("""{ "scene": 5 }""", "(top level): \"scene\" must be a string, not a number.")]
    [InlineData("""{ "presets": [] }""", "(top level): \"presets\" must be an object, not an array.")]
    [InlineData("""{ "presets": { "a": 1 } }""", "(preset \"a\"): expected a JSON object ({ ... }), not a number.")]
    [InlineData("[]", "(top level): expected a JSON object ({ ... }), not an array.")]
    [InlineData("""{ "scene": "res://a.tscn", "scene": "res://b.tscn" }""", "(top level): the key \"scene\" appears twice; keep one.")]
    [InlineData("", "is not valid JSON (line 1, column 1): The input does not contain any JSON tokens.")]
    public void AWrongTypedValueIsRefused(string json, string expectedFragment)
    {
        Write(json);

        string message = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.Contains(FilePath, message, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1280x720", true)]
    [InlineData("1x1", true)]
    [InlineData("1280X720", false)]
    [InlineData("1280 x 720", false)]
    [InlineData("0x720", false)]
    [InlineData("1280x", false)]
    [InlineData("1280x720\n", false)]
    public void AResolutionMustBeWidthByHeight(string resolution, bool valid)
    {
        Write($$"""{ "presets": { "small": { "resolution": {{System.Text.Json.JsonSerializer.Serialize(resolution)}} } } }""");

        if (valid)
        {
            ProfileLaunch launch = Merge(new RunOptions(Preset: "small"));
            Assert.Equal(["--resolution", resolution], launch.Request.EngineArgs);
        }
        else
        {
            string message = Refused(() => ProjectProfile.Load(_temp.Path));
            Assert.Contains(FilePath, message, StringComparison.Ordinal);
            Assert.Contains("\"resolution\"", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ABadPresetSessionNameIsRefused()
    {
        Write("""{ "presets": { "server": { "session": "my server" } } }""");

        string message = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.Contains(FilePath, message, StringComparison.Ordinal);
        Assert.Contains(SessionRegistry.NameRule, message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTopLevelAppliesWithoutAPreset()
    {
        Write(Layered);

        ProfileLaunch launch = Merge(new RunOptions());

        Assert.Equal("res://top.tscn", launch.Request.Scene);
        Assert.Equal(["--top-user"], launch.Request.UserArgs);
        Assert.Equal(["--resolution", "1280x720", "--top-engine"], launch.Request.EngineArgs);
        Assert.False(launch.Request.Quiet);
        Assert.Null(launch.Session);
    }

    [Fact]
    public void APresetReplacesSceneResolutionAndQuiet()
    {
        Write(Layered);

        ProfileLaunch launch = Merge(new RunOptions(Preset: "client"));
        ProfileLaunch bare = Merge(new RunOptions(Preset: "bare"));

        Assert.Equal("res://client.tscn", launch.Request.Scene);
        Assert.Equal(["--resolution", "640x360"], launch.Request.EngineArgs.Take(2));
        Assert.True(launch.Request.Quiet);
        Assert.Equal("client1", launch.Session);
        Assert.Equal("res://top.tscn", bare.Request.Scene);
        Assert.Equal(["--resolution", "1280x720"], bare.Request.EngineArgs.Take(2));
        Assert.False(bare.Request.Quiet);
        Assert.Null(bare.Session);
    }

    [Fact]
    public void ArgumentsAppendTopLevelThenPresetThenExplicit()
    {
        Write(Layered);

        ProfileLaunch launch = Merge(new RunOptions(Preset: "client"), userArgs: ["--explicit-user"], engineArgs: ["--explicit-engine"]);

        Assert.Equal(["--top-user", "--preset-user", "--explicit-user"], launch.Request.UserArgs);
        Assert.Equal(["--resolution", "640x360", "--top-engine", "--preset-engine", "--explicit-engine"], launch.Request.EngineArgs);
    }

    [Fact]
    public void TheResolutionComesBeforeEveryEngineArgument()
    {
        Write("""{ "resolution": "1280x720", "engineArgs": ["--verbose"] }""");

        ProfileLaunch launch = Merge(new RunOptions(), engineArgs: ["--resolution", "800x600"]);

        Assert.Equal(["--resolution", "1280x720", "--verbose", "--resolution", "800x600"], launch.Request.EngineArgs);
    }

    [Fact]
    public void ExplicitArgumentsWin()
    {
        Write(Layered);

        ProfileLaunch scene = Merge(new RunOptions(Preset: "client"), scene: "res://explicit.tscn");
        ProfileLaunch loudOverQuiet = Merge(new RunOptions(Quiet: false, Preset: "client"));
        ProfileLaunch quietOverLoud = Merge(new RunOptions(Quiet: true));
        ProfileLaunch session = Merge(new RunOptions(Session: "explicit", Preset: "client"));

        Assert.Equal("res://explicit.tscn", scene.Request.Scene);
        Assert.False(loudOverQuiet.Request.Quiet);
        Assert.True(quietOverLoud.Request.Quiet);
        Assert.Equal("explicit", session.Session);
    }

    [Fact]
    public void QuietDefaultsToTrueWhenNothingSetsIt()
    {
        Write("""{ "scene": "res://main.tscn", "presets": { "server": { "userArgs": ["--server"] } } }""");

        ProfileLaunch top = Merge(new RunOptions());
        ProfileLaunch preset = Merge(new RunOptions(Preset: "server"));

        Assert.True(top.Request.Quiet);
        Assert.True(preset.Request.Quiet);
    }

    [Fact]
    public void AnExplicitMuteFalseOnAQuietRunIsRefused()
    {
        Write(Layered);
        const string refusal = "options.mute: false cannot unmute a quiet run, which is always silent; pass options.quiet: false to hear the game.";

        string byDefault = Refused(() => ProjectProfile.Empty(_temp.Path).Merge(null, [], [], new RunOptions(Mute: false)));
        string byPreset = Refused(() => Merge(new RunOptions(Mute: false, Preset: "client")));
        string byOption = Refused(() => Merge(new RunOptions(Quiet: true, Mute: false)));

        Assert.Equal((refusal, refusal, refusal), (byDefault, byPreset, byOption));
    }

    [Fact]
    public void MuteIsTakenOnAWatchedRunAndDefaultsToFalse()
    {
        Write(Layered);

        ProfileLaunch watchedByProfile = Merge(new RunOptions(Mute: false));
        ProfileLaunch muted = Merge(new RunOptions(Mute: true));
        ProfileLaunch quietMuted = Merge(new RunOptions(Quiet: true, Mute: true));
        ProfileLaunch leftOut = Merge(new RunOptions(Preset: "client"));

        Assert.Equal((false, false), (watchedByProfile.Request.Quiet, watchedByProfile.Request.Mute));
        Assert.Equal((false, true), (muted.Request.Quiet, muted.Request.Mute));
        Assert.Equal((true, true), (quietMuted.Request.Quiet, quietMuted.Request.Mute));
        Assert.Equal((true, false), (leftOut.Request.Quiet, leftOut.Request.Mute));
    }

    private static string Refused(Func<object> action) => Assert.Throws<McpException>(action).Message;

    private ProfileLaunch Merge(RunOptions options, string? scene = null, string[]? userArgs = null, string[]? engineArgs = null) =>
        ProjectProfile.Load(_temp.Path).Merge(scene, userArgs ?? [], engineArgs ?? [], options);

    private void Write(string json) => File.WriteAllText(FilePath, json);
}
