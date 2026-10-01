using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

public sealed class ProjectProfileTests : IDisposable
{
    private const string TopLevelKeys = "scene, userArgs, engineArgs, resolution, quiet, presets, prepWrapper, scratch";
    private const string PrepWrapperShape =
        "(top level): \"prepWrapper\" must be a non-empty array of non-empty strings, the program and then its arguments";
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

    [Fact]
    public void APrepWrapperIsReadAsWritten()
    {
        Write("""{ "prepWrapper": ["pwsh", "tools/gate.ps1", "-Log", "{log}", "-TimeoutSeconds", "{ceiling}", "--"] }""");

        IReadOnlyList<string>? wrapper = ProjectProfile.Load(_temp.Path).PrepWrapper;

        Assert.Equal(["pwsh", "tools/gate.ps1", "-Log", "{log}", "-TimeoutSeconds", "{ceiling}", "--"], wrapper);
    }

    [Fact]
    public void NoFileOrNoKeySetsNoPrepWrapper()
    {
        IReadOnlyList<string>? noFile = ProjectProfile.Load(_temp.Path).PrepWrapper;
        Write("""{ "scene": "res://main.tscn" }""");
        IReadOnlyList<string>? noKey = ProjectProfile.Load(_temp.Path).PrepWrapper;

        Assert.Null(noFile);
        Assert.Null(noKey);
    }

    [Theory]
    [InlineData("""{ "prepWrapper": "pwsh" }""", "; it is a string.")]
    [InlineData("""{ "prepWrapper": { "program": "pwsh" } }""", "; it is an object.")]
    [InlineData("""{ "prepWrapper": [] }""", "; it is empty.")]
    [InlineData("""{ "prepWrapper": ["pwsh", 5] }""", "; it holds a number.")]
    [InlineData("""{ "prepWrapper": ["pwsh", null] }""", "; it holds null.")]
    [InlineData("""{ "prepWrapper": ["pwsh", ""] }""", "; its item 2 is an empty string.")]
    public void AMalformedPrepWrapperIsRefusedWithItsShape(string json, string problem)
    {
        Write(json);

        string message = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.Contains(FilePath, message, StringComparison.Ordinal);
        Assert.Contains(PrepWrapperShape, message, StringComparison.Ordinal);
        Assert.EndsWith(problem, message, StringComparison.Ordinal);
    }

    [Fact]
    public void APresetDescriptionLoadsAndChangesNoLaunchValue()
    {
        Write("""{ "scene": "res://top.tscn", "presets": { "host": { "description": "the server half, started first" } } }""");
        ProfileLaunch described = Merge(new RunOptions(Preset: "host"));
        Write("""{ "scene": "res://top.tscn", "presets": { "host": {} } }""");
        ProfileLaunch bare = Merge(new RunOptions(Preset: "host"));

        Assert.Equal(bare.Request.Scene, described.Request.Scene);
        Assert.Equal(bare.Request.UserArgs, described.Request.UserArgs);
        Assert.Equal(bare.Request.EngineArgs, described.Request.EngineArgs);
        Assert.Equal(bare.Request.Quiet, described.Request.Quiet);
        Assert.Equal(bare.Session, described.Session);
    }

    [Theory]
    [InlineData("""{ "presets": { "host": { "description": 5 } } }""", "(preset \"host\"): \"description\" must be a string, not a number.")]
    [InlineData(
        """{ "presets": { "host": { "description": "" } } }""",
        "(preset \"host\"): \"description\" must be a non-empty string, what the preset is for."
    )]
    public void ABadPresetDescriptionIsRefused(string json, string problem)
    {
        Write(json);

        Assert.Equal($"{FilePath} {problem}", Refused(() => ProjectProfile.Load(_temp.Path)));
    }

    [Fact]
    public void ADescriptionAtTheTopLevelIsAnUnknownKey()
    {
        Write("""{ "description": "nope" }""");

        string message = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.Contains("\"description\"", message, StringComparison.Ordinal);
        Assert.Contains(TopLevelKeys, message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownPresetListsDescribedAndBarePresets()
    {
        Write("""{ "presets": { "quick": {}, "host": { "description": "the server half, started first" }, "client": {} } }""");

        string message = Refused(() => Merge(new RunOptions(Preset: "x")));

        Assert.Equal(
            $"options.preset \"x\" is not in {FilePath}; its presets are client, host (the server half, started first), quick. "
                + "Pass one of those.",
            message
        );
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAllowed()
    {
        Write(
            """
            {
              // the top-level defaults
              "resolution": "1280x720", /* the window's size */
              "presets": {
                "server": {
                  "userArgs": ["--server",],
                },
              },
              "scratch": {
                "pace": { "TrayScratch": { "seconds": 3, /* the tray lies at rest */ "reason": "the tray lies at rest" } }
              }
            }
            """
        );

        var profile = ProjectProfile.Load(_temp.Path);
        ProfileLaunch launch = profile.Merge(null, [], [], new RunOptions(Preset: "server"));

        Assert.Equal(["--resolution", "1280x720"], launch.Request.EngineArgs.Take(2));
        Assert.Equal(["--server"], launch.Request.UserArgs);
        Assert.Equal(new ScratchPace(3, "the tray lies at rest"), profile.Scratch!.Pace["TrayScratch"]);
    }

    [Fact]
    public void AnUnknownKeyIsRefusedBesideAComment()
    {
        Write("""{ /* the top level */ "sceen": "res://main.tscn" }""");

        string message = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.Contains("\"sceen\"", message, StringComparison.Ordinal);
        Assert.Contains(TopLevelKeys, message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoFileOrNoKeySetsNoScratchSection()
    {
        ScratchProfile? noFile = ProjectProfile.Load(_temp.Path).Scratch;
        Write("""{ "scene": "res://main.tscn" }""");
        ScratchProfile? noKey = ProjectProfile.Load(_temp.Path).Scratch;

        Assert.Null(noFile);
        Assert.Null(noKey);
    }

    [Fact]
    public void TheScratchSectionReadsEveryKey()
    {
        Write(
            """
            {
              "scratch": {
                "folder": "res://Scratch",
                "userArgs": ["--scratch-profile"],
                "pace": { "TrayScratch": { "seconds": 3, "reason": "the tray lies at rest" }, "Fast": 0.25 },
                "known": { "TrayScratch": "the tray drops a card under load" },
                "patterns": ["^FAIL", "boom$"]
              }
            }
            """
        );

        ScratchProfile scratch = ProjectProfile.Load(_temp.Path).Scratch!;

        Assert.Equal("res://Scratch", scratch.Folder);
        Assert.Equal(["--scratch-profile"], scratch.UserArgs);
        Assert.Equal(new ScratchPace(3, "the tray lies at rest"), scratch.Pace["TrayScratch"]);
        Assert.Equal(new ScratchPace(0.25, null), scratch.Pace["Fast"]);
        Assert.Equal("the tray drops a card under load", scratch.Known["TrayScratch"]);
        Assert.Equal(["^FAIL", "boom$"], scratch.Patterns.Select(pattern => pattern.Regex.ToString()));
        Assert.Matches(scratch.Patterns[0].Regex, "FAIL: x");
        Assert.DoesNotMatch(scratch.Patterns[0].Regex, "fail: x");
    }

    [Fact]
    public void AnObjectPaceReadsItsKeysInEitherOrder()
    {
        Write("""{ "scratch": { "pace": { "A": { "reason": "why", "seconds": 3 } } } }""");

        Assert.Equal(new ScratchPace(3, "why"), ProjectProfile.Load(_temp.Path).Scratch!.Pace["A"]);
    }

    [Fact]
    public void AnEmptyScratchSectionTakesTheDefaults()
    {
        Write("""{ "userArgs": ["--top"], "scratch": {} }""");

        var profile = ProjectProfile.Load(_temp.Path);
        ScratchProfile scratch = profile.Scratch!;

        Assert.Null(scratch.Folder);
        Assert.Null(scratch.UserArgs);
        Assert.Equal(["--top"], profile.UserArgs);
        Assert.Empty(scratch.Pace);
        Assert.Empty(scratch.Known);
        ScratchPattern pattern = Assert.Single(scratch.Patterns);
        Assert.Equal(ScratchProfile.DefaultPatterns[0].Regex.ToString(), pattern.Regex.ToString());
        Assert.Null(pattern.Reason);
        Assert.Matches(pattern.Regex, "SCRIPT ERROR: Invalid call.");
        Assert.Matches(pattern.Regex, "ERROR: late failure");
        Assert.Matches(pattern.Regex, "WARNING: 1 ObjectDB instance was leaked at exit");
        Assert.Matches(pattern.Regex, "WARNING: 2 ObjectDB instances were leaked at exit");
        Assert.DoesNotMatch(pattern.Regex, "[scratch] note: ERROR: in the middle");
    }

    [Fact]
    public void AnEmptyPatternListTurnsLineMatchingOff()
    {
        Write("""{ "scratch": { "patterns": [] } }""");

        Assert.Empty(ProjectProfile.Load(_temp.Path).Scratch!.Patterns);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ScratchParallelFromOneToFourIsRead(int parallel)
    {
        Write($$"""{ "scratch": { "parallel": {{parallel}} } }""");

        Assert.Equal(parallel, ProjectProfile.Load(_temp.Path).Scratch!.Parallel);
    }

    [Fact]
    public void ScratchParallelDefaultsToOne()
    {
        Write("""{ "scratch": {} }""");

        Assert.Equal(1, ProjectProfile.Load(_temp.Path).Scratch!.Parallel);
        Assert.Equal(1, ScratchProfile.None.Parallel);
    }

    [Theory]
    [InlineData(
        """{ "scratch": { "threads": 2 } }""",
        "(scratch): unknown key \"threads\"; the allowed keys are folder, userArgs, pace, known, patterns, parallel. Remove or rename it."
    )]
    [InlineData("""{ "scratch": { "parallel": 0 } }""", "(scratch): \"parallel\" must be a whole number from 1 to 4, not 0.")]
    [InlineData("""{ "scratch": { "parallel": 5 } }""", "(scratch): \"parallel\" must be a whole number from 1 to 4, not 5.")]
    [InlineData("""{ "scratch": { "parallel": 2.5 } }""", "(scratch): \"parallel\" must be a whole number from 1 to 4, not 2.5.")]
    [InlineData("""{ "scratch": { "parallel": "2" } }""", "(scratch): \"parallel\" must be a whole number from 1 to 4, not a string.")]
    [InlineData("""{ "scratch": { "folder": "A", "folder": "B" } }""", "(scratch): the key \"folder\" appears twice; keep one.")]
    [InlineData("""{ "scratch": [] }""", "(scratch): expected a JSON object ({ ... }), not an array.")]
    [InlineData("""{ "scratch": { "folder": 3 } }""", "(scratch): \"folder\" must be a string, not a number.")]
    [InlineData(
        """{ "scratch": { "pace": { "A": 0 } } }""",
        "(scratch): \"pace\" of \"A\" must be a number of seconds above 0 and at most 120, not 0."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": 121 } } }""",
        "(scratch): \"pace\" of \"A\" must be a number of seconds above 0 and at most 120, not 121."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": "1" } } }""",
        "(scratch): \"pace\" of \"A\" must be a number of seconds above 0 and at most 120, or an object {\"seconds\", \"reason\"}, not a string."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": [] } } }""",
        "(scratch): \"pace\" of \"A\" must be a number of seconds above 0 and at most 120, or an object {\"seconds\", \"reason\"}, not an array."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 3 } } } }""",
        "(scratch, pace of \"A\"): \"reason\" is missing; an object pace needs both \"seconds\" and \"reason\". Add it, or "
            + "write the pace as a bare number of seconds."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "reason": "x" } } } }""",
        "(scratch, pace of \"A\"): \"seconds\" is missing; an object pace needs both \"seconds\" and \"reason\". Add it, or "
            + "write the pace as a bare number of seconds."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": {} } } }""",
        "(scratch, pace of \"A\"): \"seconds\" is missing; an object pace needs both \"seconds\" and \"reason\". Add it, or "
            + "write the pace as a bare number of seconds."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 0, "reason": "x" } } } }""",
        "(scratch, pace of \"A\"): \"seconds\" must be a number of seconds above 0 and at most 120, not 0."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": "3", "reason": "x" } } } }""",
        "(scratch, pace of \"A\"): \"seconds\" must be a number of seconds above 0 and at most 120, not a string."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 3, "reason": "" } } } }""",
        "(scratch, pace of \"A\"): \"reason\" must be a non-empty string, why the scene needs this pace."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 3, "reason": 5 } } } }""",
        "(scratch, pace of \"A\"): \"reason\" must be a non-empty string, why the scene needs this pace."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 3, "reason": "x", "why": "y" } } } }""",
        "(scratch, pace of \"A\"): unknown key \"why\"; the allowed keys are seconds, reason. Remove or rename it."
    )]
    [InlineData("""{ "scratch": { "pace": { "A": 1, "A": 2 } } }""", "(scratch): the scene \"A\" appears twice in \"pace\"; keep one.")]
    [InlineData(
        """{ "scratch": { "known": { "A": "" } } }""",
        "(scratch): \"known\" of \"A\" must be a non-empty string, the reason the scene is known to fail."
    )]
    [InlineData("""{ "scratch": { "patterns": "ERROR" } }""", "(scratch): \"patterns\" must be an array, not a string.")]
    [InlineData(
        """{ "scratch": { "patterns": ["^A", 3] } }""",
        "(scratch): \"patterns\" must be an array of regular expressions, each a string or an object {\"pattern\", \"reason\"}; "
            + "item 2 is a number."
    )]
    [InlineData(
        """{ "scratch": { "patterns": [{"pattern": "^A"}] } }""",
        "(scratch, pattern 1): \"reason\" is missing; an object pattern needs both \"pattern\" and \"reason\". Add it, or "
            + "write the pattern as a bare string."
    )]
    [InlineData(
        """{ "scratch": { "patterns": [{"reason": "x"}] } }""",
        "(scratch, pattern 1): \"pattern\" is missing; an object pattern needs both \"pattern\" and \"reason\". Add it, or "
            + "write the pattern as a bare string."
    )]
    [InlineData(
        """{ "scratch": { "patterns": [{}] } }""",
        "(scratch, pattern 1): \"pattern\" is missing; an object pattern needs both \"pattern\" and \"reason\". Add it, or "
            + "write the pattern as a bare string."
    )]
    [InlineData(
        """{ "scratch": { "patterns": [{"pattern": 3, "reason": "x"}] } }""",
        "(scratch, pattern 1): \"pattern\" must be a string, not a number."
    )]
    [InlineData(
        """{ "scratch": { "patterns": [{"pattern": "^A", "reason": ""}] } }""",
        "(scratch, pattern 1): \"reason\" must be a non-empty string, why a line matching it fails a step."
    )]
    [InlineData(
        """{ "scratch": { "patterns": [{"pattern": "^A", "reason": 5}] } }""",
        "(scratch, pattern 1): \"reason\" must be a non-empty string, why a line matching it fails a step."
    )]
    [InlineData(
        """{ "scratch": { "patterns": [{"pattern": "^A", "reason": "x", "why": "y"}] } }""",
        "(scratch, pattern 1): unknown key \"why\"; the allowed keys are pattern, reason. Remove or rename it."
    )]
    public void AMalformedScratchSectionIsRefusedNamingTheFile(string json, string problem)
    {
        Write(json);

        string message = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.Equal($"{FilePath} {problem}", message);
    }

    [Fact]
    public void APatternItemIsAStringOrAnObjectInEitherKeyOrder()
    {
        Write("""{ "scratch": { "patterns": ["^A", {"reason": "why", "pattern": "^B"}] } }""");

        IReadOnlyList<ScratchPattern> patterns = ProjectProfile.Load(_temp.Path).Scratch!.Patterns;

        Assert.Equal(["^A", "^B"], patterns.Select(pattern => pattern.Regex.ToString()));
        Assert.Equal([null, "why"], patterns.Select(pattern => pattern.Reason));
    }

    [Fact]
    public void AnInvalidObjectPatternIsRefusedAtLoadNamingIt()
    {
        Write("""{ "scratch": { "patterns": [{"pattern": "(", "reason": "x"}] } }""");

        string message = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.StartsWith($"{FilePath} (scratch): the pattern \"(\" in \"patterns\" is not a valid .NET regular expression: ", message);
        Assert.EndsWith(" Fix or remove it.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidPatternIsRefusedAtLoadNamingIt()
    {
        Write("""{ "scratch": { "patterns": ["^ok", "(unclosed"] } }""");

        string message = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.StartsWith($"{FilePath} (scratch): the pattern \"(unclosed\" in \"patterns\" is not a valid .NET regular expression: ", message);
        Assert.EndsWith(" Fix or remove it.", message, StringComparison.Ordinal);
    }

    private static string Refused(Func<object> action) => Assert.Throws<McpException>(action).Message;

    private ProfileLaunch Merge(RunOptions options, string? scene = null, string[]? userArgs = null, string[]? engineArgs = null) =>
        ProjectProfile.Load(_temp.Path).Merge(scene, userArgs ?? [], engineArgs ?? [], options);

    private void Write(string json) => File.WriteAllText(FilePath, json);
}
