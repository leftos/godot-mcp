using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

public sealed class ProjectProfileTests : IDisposable
{
    private const string TopLevelKeys = "scene, userArgs, engineArgs, resolution, quiet, audible, env, presets, prepWrapper, scratch, processes";
    private const string PrepWrapperShape =
        "(top level): \"prepWrapper\" must be a non-empty array of non-empty strings, the program and then its arguments";
    private const string PresetKeys = "scene, userArgs, engineArgs, resolution, quiet, audible, env, session";

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
        const string refusal =
            "options.mute: false cannot unmute a quiet run, which is always silent; pass options.audible: true to hear it off-screen, "
            + "or options.quiet: false to hear it on screen.";

        string byDefault = Refused(() => ProjectProfile.Empty(_temp.Path).Merge(null, [], [], new RunOptions(Mute: false)));
        string byPreset = Refused(() => Merge(new RunOptions(Mute: false, Preset: "client")));
        string byOption = Refused(() => Merge(new RunOptions(Quiet: true, Mute: false)));
        ProfileLaunch audible = Merge(new RunOptions(Quiet: true, Mute: false, Audible: true));

        Assert.Equal((refusal, refusal, refusal), (byDefault, byPreset, byOption));
        Assert.Equal((true, false), (audible.Request.Quiet, audible.Request.Mute));
    }

    [Fact]
    public void AudibleIsTakenFromOptionPresetAndTopLevelAndRefusedOffAQuietRun()
    {
        Write(
            """
            {
              "audible": true,
              "presets": {
                "silent": { "quiet": true, "audible": false },
                "bare": {}
              }
            }
            """
        );
        const string notQuiet =
            "options.audible: true applies to a quiet run, and this run is not quiet, so it already plays sound (options.mute: true "
            + "silences it); drop options.audible.";
        const string contradicts = "options.audible: true and options.mute: true contradict; drop one.";
        const string recording =
            "options.audible has no effect on a recording run: Movie Maker mixes on the Dummy driver and writes the sound into the "
            + "clip; drop options.audible.";

        ProfileLaunch byTop = Merge(new RunOptions());
        ProfileLaunch byPreset = Merge(new RunOptions(Preset: "silent"));
        ProfileLaunch byOption = Merge(new RunOptions(Preset: "silent", Audible: true));
        ProfileLaunch leftOut = ProjectProfile.Empty(_temp.Path).Merge(null, [], [], new RunOptions());

        Assert.Equal((true, true), (byTop.Request.Quiet, byTop.Request.Audible));
        Assert.Equal((true, false), (byPreset.Request.Quiet, byPreset.Request.Audible));
        Assert.Equal((true, true), (byOption.Request.Quiet, byOption.Request.Audible));
        Assert.False(leftOut.Request.Audible);
        Assert.Equal(notQuiet, Refused(() => Merge(new RunOptions(Quiet: false, Audible: true))));
        Assert.Equal(contradicts, Refused(() => Merge(new RunOptions(Mute: true, Audible: true))));
        Assert.Equal(recording, Refused(() => Merge(new RunOptions(Record: true, Audible: true))));
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
        Write("""{ "prepWrapper": ["gate", "run", "--log", "{log}", "--timeout", "{ceiling}", "--"] }""");

        IReadOnlyList<string>? wrapper = ProjectProfile.Load(_temp.Path).PrepWrapper;

        Assert.Equal(["gate", "run", "--log", "{log}", "--timeout", "{ceiling}", "--"], wrapper);
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
    public void ScratchEngineArgsAreRead()
    {
        Write("""{ "scratch": { "engineArgs": ["--fixed-fps", "30"] } }""");
        ScratchProfile set = ProjectProfile.Load(_temp.Path).Scratch!;
        Write("""{ "scratch": { "folder": "res://Scratch" } }""");
        ScratchProfile unset = ProjectProfile.Load(_temp.Path).Scratch!;

        Assert.Equal(["--fixed-fps", "30"], set.EngineArgs);
        Assert.Null(unset.EngineArgs);
    }

    [Fact]
    public void ScratchCeilingsAreReadPerScene()
    {
        Write("""{ "scratch": { "ceilings": { "Combat": 12.5, "Tray": 3600 } } }""");
        ScratchProfile set = ProjectProfile.Load(_temp.Path).Scratch!;
        Write("""{ "scratch": { "folder": "res://Scratch" } }""");
        ScratchProfile unset = ProjectProfile.Load(_temp.Path).Scratch!;

        Assert.Equal(2, set.Ceilings.Count);
        Assert.Equal(12.5, set.Ceilings["Combat"]);
        Assert.Equal(3600, set.Ceilings["Tray"]);
        Assert.Empty(unset.Ceilings);
    }

    [Theory]
    [InlineData("""{ "scratch": { "ceilings": { "Combat": 0 } } }""", "0")]
    [InlineData("""{ "scratch": { "ceilings": { "Combat": -1 } } }""", "-1")]
    [InlineData("""{ "scratch": { "ceilings": { "Combat": 3601 } } }""", "3601")]
    [InlineData("""{ "scratch": { "ceilings": { "Combat": "12" } } }""", "a string")]
    public void AScratchCeilingOutsideItsRangeIsRefused(string json, string shown)
    {
        Write(json);

        Assert.Equal(
            $"{FilePath} (scratch): scratch.ceilings.Combat must be a number of seconds above 0 and at most 3600; got {shown}.",
            Refused(() => ProjectProfile.Load(_temp.Path))
        );
    }

    [Fact]
    public void ScratchSetupIsReadAsStepKeysPerScene()
    {
        Write("""{ "scratch": { "setup": { "Combat": [0, "build", "2"], "Tray": ["open"] } } }""");
        ScratchProfile set = ProjectProfile.Load(_temp.Path).Scratch!;
        Write("""{ "scratch": { "folder": "res://Scratch" } }""");
        ScratchProfile unset = ProjectProfile.Load(_temp.Path).Scratch!;

        Assert.Equal(["0", "build", "2"], set.Setup["Combat"].Always);
        Assert.Empty(set.Setup["Combat"].Steps);
        Assert.Equal(["open"], set.Setup["Tray"].Always);
        Assert.Empty(unset.Setup);
        Assert.Empty(ScratchProfile.None.Setup);
    }

    [Fact]
    public void ScratchSetupReadsAnObjectOfTheSceneWideListAndEachStepsOwn()
    {
        Write(
            """
            {
              "scratch": {
                "setup": {
                  "Always": { "always": ["build", 1] },
                  "Steps": { "steps": { "3": ["open"], "Fight": [0, "build"] } },
                  "Both": { "always": ["build"], "steps": { "Fight": ["open"] } }
                }
              }
            }
            """
        );

        ScratchProfile scratch = ProjectProfile.Load(_temp.Path).Scratch!;

        Assert.Equal(["build", "1"], scratch.Setup["Always"].Always);
        Assert.Empty(scratch.Setup["Always"].Steps);
        Assert.Empty(scratch.Setup["Steps"].Always);
        Assert.Equal(["open"], scratch.Setup["Steps"].Steps["3"]);
        Assert.Equal(["0", "build"], scratch.Setup["Steps"].Steps["Fight"]);
        Assert.Equal(["build"], scratch.Setup["Both"].Always);
        Assert.Equal(["open"], scratch.Setup["Both"].Steps["Fight"]);
    }

    [Theory]
    [InlineData(
        """{ "scratch": { "setup": { "Combat": "build" } } }""",
        "(scratch): scratch.setup.Combat must be an array of setup steps or an object {always?, steps?}, not a string; "
            + "a setup step is a whole number from 0 up, or a non-empty string naming it."
    )]
    [InlineData(
        """{ "scratch": { "setup": { "Combat": { "always": "build" } } } }""",
        "(scratch): scratch.setup.Combat.always must be an array of setup steps, not a string; a setup step is a whole number "
            + "from 0 up, or a non-empty string naming it."
    )]
    [InlineData(
        """{ "scratch": { "setup": { "Combat": { "steps": { "fight": "build" } } } } }""",
        "(scratch): scratch.setup.Combat.steps.fight must be an array of setup steps, not a string; a setup step is a whole "
            + "number from 0 up, or a non-empty string naming it."
    )]
    [InlineData(
        """{ "scratch": { "setup": { "Combat": { "steps": [] } } } }""",
        "(scratch): scratch.setup.Combat.steps must be an object of step keys to setup steps, not an array."
    )]
    [InlineData("""{ "scratch": { "setup": { "Combat": {} } } }""", "(scratch): scratch.setup.Combat must name always or steps; it names neither.")]
    [InlineData(
        """{ "scratch": { "setup": { "Combat": { "allways": ["build"] } } } }""",
        "(scratch, setup of \"Combat\"): unknown key \"allways\"; the allowed keys are always, steps. Remove or rename it."
    )]
    [InlineData(
        """{ "scratch": { "setup": { "Combat": { "steps": { "fight": ["a"], "fight": ["b"] } } } } }""",
        "(scratch): scratch.setup.Combat.steps names the step key \"fight\" twice; keep one."
    )]
    [InlineData(
        """{ "scratch": { "setup": { "Combat": { "always": [-1] } } } }""",
        "(scratch): scratch.setup.Combat.always item 1 is -1; a setup step is a whole number from 0 up, or a non-empty string " + "naming it."
    )]
    [InlineData(
        """{ "scratch": { "setup": { "Combat": { "always": [] } } } }""",
        "(scratch): scratch.setup.Combat.always must name at least one step; it is empty."
    )]
    [InlineData(
        """{ "scratch": { "setup": { "Combat": { "steps": {} } } } }""",
        "(scratch): scratch.setup.Combat.steps must name at least one step; it is empty."
    )]
    [InlineData(
        """{ "scratch": { "setup": { "Combat": { "steps": { "fight": [] } } } } }""",
        "(scratch): scratch.setup.Combat.steps.fight must name at least one step; it is empty."
    )]
    [InlineData("""{ "scratch": { "setup": { "Combat": [] } } }""", "(scratch): scratch.setup.Combat must name at least one step; it is empty.")]
    [InlineData(
        """{ "scratch": { "setup": { "Combat": [-1] } } }""",
        "(scratch): scratch.setup.Combat item 1 is -1; a setup step is a whole number from 0 up, or a non-empty string naming it."
    )]
    [InlineData(
        """{ "scratch": { "setup": { "Combat": [1.5] } } }""",
        "(scratch): scratch.setup.Combat item 1 is 1.5; a setup step is a whole number from 0 up, or a non-empty string naming it."
    )]
    [InlineData(
        """{ "scratch": { "setup": { "Combat": [""] } } }""",
        "(scratch): scratch.setup.Combat item 1 is an empty string; a setup step is a whole number from 0 up, or a non-empty " + "string naming it."
    )]
    [InlineData(
        """{ "scratch": { "setup": { "Combat": [true] } } }""",
        "(scratch): scratch.setup.Combat item 1 is true or false; a setup step is a whole number from 0 up, or a non-empty " + "string naming it."
    )]
    public void AMalformedScratchSetupIsRefused(string json, string problem)
    {
        Write(json);

        Assert.Equal($"{FilePath} {problem}", Refused(() => ProjectProfile.Load(_temp.Path)));
    }

    [Fact]
    public void AnObjectPaceReadsItsKeysInEitherOrder()
    {
        Write("""{ "scratch": { "pace": { "A": { "reason": "why", "seconds": 3 } } } }""");

        Assert.Equal(new ScratchPace(3, "why"), ProjectProfile.Load(_temp.Path).Scratch!.Pace["A"]);
    }

    [Fact]
    public void AnObjectPaceReadsItsStepPacesKeyedByIndexOrNameAsNumbersOrObjects()
    {
        Write(
            """
            {
              "scratch": {
                "pace": {
                  "A": {
                    "seconds": 0.5,
                    "reason": "most steps settle at once",
                    "steps": { "3": 30, "FightBatch": { "reason": "the 25-line batch plays 22.7 s", "seconds": 25 } }
                  }
                }
              }
            }
            """
        );

        ScratchPace pace = ProjectProfile.Load(_temp.Path).Scratch!.Pace["A"];

        Assert.Equal((0.5, "most steps settle at once"), (pace.Seconds, pace.Reason));
        Assert.Equal(["3", "FightBatch"], pace.Steps.Keys);
        Assert.Equal(new ScratchPace(30, null), pace.Steps["3"]);
        Assert.Equal(new ScratchPace(25, "the 25-line batch plays 22.7 s"), pace.Steps["FightBatch"]);
        Assert.Empty(pace.Steps["3"].Steps);
    }

    [Fact]
    public void AnObjectPacesTimeScaleOfOneKeepsTheSceneAtNormalSpeed()
    {
        Write(
            """
            { "scratch": { "pace": { "A": { "seconds": 2, "reason": "physics", "timeScale": 1 }, "B": { "seconds": 2, "reason": "why" }, "C": 2 } } }
            """
        );

        ScratchProfile scratch = ProjectProfile.Load(_temp.Path).Scratch!;

        Assert.Equal((2.0, "physics", true), (scratch.Pace["A"].Seconds, scratch.Pace["A"].Reason, scratch.Pace["A"].NormalSpeed));
        Assert.False(scratch.Pace["B"].NormalSpeed);
        Assert.False(scratch.Pace["C"].NormalSpeed);
    }

    [Fact]
    public void AnObjectPacesExitHoldIsReadAndAPaceWithoutOneHasNone()
    {
        Write(
            """
            { "scratch": { "pace": { "A": { "seconds": 10, "reason": "until", "exitHold": 2 }, "B": { "seconds": 2, "reason": "why" }, "C": 2 } } }
            """
        );

        ScratchProfile scratch = ProjectProfile.Load(_temp.Path).Scratch!;

        Assert.Equal((10.0, 2.0), (scratch.Pace["A"].Seconds, scratch.Pace["A"].ExitHold));
        Assert.Null(scratch.Pace["B"].ExitHold);
        Assert.Null(scratch.Pace["C"].ExitHold);
    }

    [Fact]
    public void APaceWithoutStepsHasNone()
    {
        Write("""{ "scratch": { "pace": { "A": { "seconds": 3, "reason": "why" }, "B": 2 } } }""");

        ScratchProfile scratch = ProjectProfile.Load(_temp.Path).Scratch!;

        Assert.Empty(scratch.Pace["A"].Steps);
        Assert.Empty(scratch.Pace["B"].Steps);
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
        "(scratch): unknown key \"threads\"; the allowed keys are folder, userArgs, engineArgs, pace, ceilings, setup, known, "
            + "patterns, expect, expectFor, parallel. Remove or rename it."
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
            + "write the pace as a bare number of seconds, which cannot carry a timeScale or exitHold."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "reason": "x" } } } }""",
        "(scratch, pace of \"A\"): \"seconds\" is missing; an object pace needs both \"seconds\" and \"reason\". Add it, or "
            + "write the pace as a bare number of seconds, which cannot carry a timeScale or exitHold."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": {} } } }""",
        "(scratch, pace of \"A\"): \"seconds\" is missing; an object pace needs both \"seconds\" and \"reason\". Add it, or "
            + "write the pace as a bare number of seconds, which cannot carry a timeScale or exitHold."
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
        "(scratch, pace of \"A\"): unknown key \"why\"; the allowed keys are seconds, reason, steps, timeScale, exitHold. Remove or rename it."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 3, "reason": "x", "exitHold": 0 } } } }""",
        "(scratch, pace of \"A\"): \"exitHold\" must be a number of seconds above 0 and at most 120, not 0."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 3, "reason": "x", "exitHold": -1 } } } }""",
        "(scratch, pace of \"A\"): \"exitHold\" must be a number of seconds above 0 and at most 120, not -1."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 3, "reason": "x", "exitHold": 121 } } } }""",
        "(scratch, pace of \"A\"): \"exitHold\" must be a number of seconds above 0 and at most 120, not 121."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 3, "reason": "x", "exitHold": "2" } } } }""",
        "(scratch, pace of \"A\"): \"exitHold\" must be a number of seconds above 0 and at most 120, not a string."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 1, "reason": "x", "steps": { "2": { "seconds": 3, "reason": "y", "exitHold": 1 } } } } } }""",
        "(scratch, pace of \"A\", step \"2\"): unknown key \"exitHold\"; the allowed keys are seconds, reason. Remove or rename it."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 3, "reason": "x", "timeScale": 2 } } } }""",
        "(scratch, pace of \"A\"): \"timeScale\" is 2; a pace entry's \"timeScale\" can only be 1, which keeps the scene at normal speed. "
            + "Remove it, or set it to 1."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 3, "reason": "x", "timeScale": "1" } } } }""",
        "(scratch, pace of \"A\"): \"timeScale\" is a string; a pace entry's \"timeScale\" can only be 1, which keeps the scene at normal "
            + "speed. Remove it, or set it to 1."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 1, "reason": "x", "steps": { "2": { "seconds": 3, "reason": "y", "timeScale": 1 } } } } } }""",
        "(scratch, pace of \"A\", step \"2\"): unknown key \"timeScale\"; the allowed keys are seconds, reason. Remove or rename it."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 1, "reason": "x", "steps": { "2": { "seconds": 3, "reason": "y", "steps": {} } } } } } }""",
        "(scratch, pace of \"A\", step \"2\"): unknown key \"steps\"; the allowed keys are seconds, reason. Remove or rename it."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 1, "reason": "x", "steps": { "2": 0 } } } } }""",
        "(scratch, pace of \"A\"): \"steps\" entry \"2\" must be a number of seconds above 0 and at most 120, not 0."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 1, "reason": "x", "steps": { "Fight": { "seconds": 121, "reason": "y" } } } } } }""",
        "(scratch, pace of \"A\", step \"Fight\"): \"seconds\" must be a number of seconds above 0 and at most 120, not 121."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 1, "reason": "x", "steps": { "Fight": { "seconds": 3 } } } } } }""",
        "(scratch, pace of \"A\", step \"Fight\"): \"reason\" is missing; an object pace needs both \"seconds\" and \"reason\". Add it, or "
            + "write the pace as a bare number of seconds, which cannot carry a timeScale or exitHold."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 1, "reason": "x", "steps": { "2": "3" } } } } }""",
        "(scratch, pace of \"A\"): \"steps\" entry \"2\" must be a number of seconds above 0 and at most 120, or an object "
            + "{\"seconds\", \"reason\"}, not a string."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 1, "reason": "x", "steps": [3] } } } }""",
        "(scratch, pace of \"A\"): \"steps\" must be an object, not an array."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "seconds": 1, "reason": "x", "steps": { "2": 1, "2": 3 } } } } }""",
        "(scratch, pace of \"A\"): the step key \"2\" appears twice in \"steps\"; keep one."
    )]
    [InlineData(
        """{ "scratch": { "pace": { "A": { "reason": "x", "steps": { "2": 1 } } } } }""",
        "(scratch, pace of \"A\"): \"seconds\" is missing; an object pace needs both \"seconds\" and \"reason\". Add it, or "
            + "write the pace as a bare number of seconds, which cannot carry a timeScale or exitHold."
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
    [InlineData("""{ "scratch": { "expect": "^A" } }""", "(scratch): \"expect\" must be an array, not a string.")]
    [InlineData(
        """{ "scratch": { "expect": ["^A", 3] } }""",
        "(scratch): \"expect\" must be an array of regular expressions, each a string or an object {\"pattern\", \"reason\"}; "
            + "item 2 is a number."
    )]
    [InlineData(
        """{ "scratch": { "expect": [{"pattern": "^A"}] } }""",
        "(scratch, expect pattern 1): \"reason\" is missing; an object pattern needs both \"pattern\" and \"reason\". Add it, or "
            + "write the pattern as a bare string."
    )]
    [InlineData(
        """{ "scratch": { "expect": [{"pattern": "^A", "reason": ""}] } }""",
        "(scratch, expect pattern 1): \"reason\" must be a non-empty string, why the scene must print a line matching it."
    )]
    [InlineData("""{ "scratch": { "expectFor": ["^A"] } }""", "(scratch): \"expectFor\" must be an object, not an array.")]
    [InlineData("""{ "scratch": { "expectFor": { "A": "^A" } } }""", "(scratch): \"expectFor\" of \"A\" must be an array, not a string.")]
    [InlineData(
        """{ "scratch": { "expectFor": { "A": [{"pattern": "^A", "reason": "x", "why": "y"}] } } }""",
        "(scratch, expectFor \"A\" pattern 1): unknown key \"why\"; the allowed keys are pattern, reason. Remove or rename it."
    )]
    [InlineData(
        """{ "scratch": { "expectFor": { "A": [], "A": ["^B"] } } }""",
        "(scratch): the scene \"A\" appears twice in \"expectFor\"; keep one."
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

    [Fact]
    public void ExpectTakesStringsAndObjectsAndExpectForTakesThemPerScene()
    {
        Write(
            """
            {
              "scratch": {
                "expect": ["^A", {"reason": "the store is removed", "pattern": "^B"}],
                "expectFor": { "Duel": [{"pattern": "^C", "reason": "the duel ends"}, "^D"], "Tray": [] }
              }
            }
            """
        );

        ScratchProfile scratch = ProjectProfile.Load(_temp.Path).Scratch!;

        Assert.Equal(["^A", "^B"], scratch.Expect.Select(pattern => pattern.Regex.ToString()));
        Assert.Equal([null, "the store is removed"], scratch.Expect.Select(pattern => pattern.Reason));
        Assert.Equal(["Duel", "Tray"], scratch.ExpectFor.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["^C", "^D"], scratch.ExpectFor["Duel"].Select(pattern => pattern.Regex.ToString()));
        Assert.Equal(["the duel ends", null], scratch.ExpectFor["Duel"].Select(pattern => pattern.Reason));
        Assert.Empty(scratch.ExpectFor["Tray"]);
    }

    [Fact]
    public void ASectionWithoutExpectExpectsNothing()
    {
        Write("""{ "scratch": { "folder": "scratch" } }""");

        ScratchProfile scratch = ProjectProfile.Load(_temp.Path).Scratch!;

        Assert.Empty(scratch.Expect);
        Assert.Empty(scratch.ExpectFor);
        Assert.Empty(ScratchProfile.None.Expect);
        Assert.Empty(ScratchProfile.None.ExpectFor);
    }

    [Theory]
    [InlineData("""{ "scratch": { "expect": ["^ok", "(unclosed"] } }""", "\"expect\"")]
    [InlineData("""{ "scratch": { "expect": [{"pattern": "(unclosed", "reason": "x"}] } }""", "\"expect\"")]
    [InlineData("""{ "scratch": { "expectFor": { "Duel": ["(unclosed"] } } }""", "\"expectFor\" of \"Duel\"")]
    public void AnInvalidExpectPatternIsRefusedAtLoadNamingItsKey(string json, string key)
    {
        Write(json);

        string message = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.StartsWith($"{FilePath} (scratch): the pattern \"(unclosed\" in {key} is not a valid .NET regular expression: ", message);
        Assert.EndsWith(" Fix or remove it.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessesParseEveryFieldAndTheTimeoutDefault()
    {
        Write(
            """
            {
              "processes": {
                "server": {
                  "command": ["dotnet", "run", "--project", "../Delve.Server", "--", "--dev-cheats"],
                  "cwd": ".",
                  "env": {"Delve__DataDir": "../../.tmp/drive-runs"},
                  "ready": {"output": "Now listening on", "timeoutMs": 120000},
                  "description": "the game server; pass --port <n> as args"
                },
                "ticker": {
                  "command": ["pwsh", "-NoProfile", "-File", "ticker.ps1"],
                  "ready": {"output": "ticker \\d+"}
                }
              }
            }
            """
        );

        IReadOnlyDictionary<string, ProcessProfile> processes = ProjectProfile.Load(_temp.Path).Processes;

        Assert.Equal(["server", "ticker"], processes.Keys);
        ProcessProfile server = processes["server"];
        Assert.Equal(["dotnet", "run", "--project", "../Delve.Server", "--", "--dev-cheats"], server.Command);
        Assert.Equal(Path.GetFullPath(Path.Combine(_temp.Path, ".")), server.Cwd);
        Assert.Equal("../../.tmp/drive-runs", server.Env["Delve__DataDir"]);
        Assert.Equal("Now listening on", server.Ready!.Output);
        Assert.Equal(120000, server.Ready.TimeoutMs);
        Assert.Matches(server.Ready.Regex, "Now listening on http://localhost:5000");
        Assert.Equal("the game server; pass --port <n> as args", server.Description);

        ProcessProfile ticker = processes["ticker"];
        Assert.Null(ticker.Cwd);
        Assert.Empty(ticker.Env);
        Assert.Equal(60000, ticker.Ready!.TimeoutMs);
        Assert.Null(ticker.Description);
    }

    [Fact]
    public void AProcessPrepIsReadAsACommand()
    {
        Write(
            """
            {
              "processes": {
                "server": {
                  "command": ["dotnet", "run", "--no-build"],
                  "prep": ["dotnet", "build", "--no-restore"]
                },
                "ticker": { "command": ["pwsh", "-File", "ticker.ps1"] }
              }
            }
            """
        );

        IReadOnlyDictionary<string, ProcessProfile> processes = ProjectProfile.Load(_temp.Path).Processes;

        Assert.NotNull(processes["server"].Prep);
        Assert.Equal(["dotnet", "build", "--no-restore"], processes["server"].Prep!);
        Assert.Equal(["dotnet", "run", "--no-build"], processes["server"].Command);
        Assert.Null(processes["ticker"].Prep);
    }

    [Theory]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "prep": "dotnet build" } } }""",
        "(processes.server.prep): \"prep\" must be a non-empty array of non-empty strings, the program and then its arguments; it is a string."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "prep": [] } } }""",
        "(processes.server.prep): \"prep\" must be a non-empty array of non-empty strings, the program and then its arguments; it is empty."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "prep": ["dotnet", ""] } } }""",
        "(processes.server.prep): \"prep\" must be a non-empty array of non-empty strings, the program and then its arguments; "
            + "its item 2 is an empty string."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "prep": ["dotnet", 1] } } }""",
        "(processes.server.prep): \"prep\" must be a non-empty array of non-empty strings, the program and then its arguments; it holds a number."
    )]
    public void AMalformedProcessPrepIsRefused(string json, string problem)
    {
        Write(json);

        string message = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.Equal($"{FilePath} {problem}", message);
    }

    [Theory]
    [InlineData("""{ "processes": [] }""", "(top level): \"processes\" must be an object, not an array.")]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "redo": 1 } } }""",
        "(processes.server): unknown key \"redo\"; the allowed keys are command, cwd, env, ready, description, prep. Remove or rename it."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"] }, "server": { "command": ["b"] } } }""",
        "(top level): the process \"server\" appears twice in \"processes\"; keep one."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": [] } } }""",
        "(processes.server): \"command\" must be a non-empty array of non-empty strings, the program and then its arguments; it is empty."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": "dotnet" } } }""",
        "(processes.server): \"command\" must be a non-empty array of non-empty strings, the program and then its arguments; it is a string."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["dotnet", 1] } } }""",
        "(processes.server): \"command\" must be a non-empty array of non-empty strings, the program and then its arguments; it holds a number."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "ready": {"output": "x", "why": 1} } } }""",
        "(processes.server.ready): unknown key \"why\"; the allowed keys are output, timeoutMs. Remove or rename it."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "ready": {"timeoutMs": 1000} } } }""",
        "(processes.server.ready): \"output\" is missing; \"ready\" needs the regular expression a printed line is matched against."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "ready": {"output": "x", "timeoutMs": 0} } } }""",
        "(processes.server.ready): \"timeoutMs\" must be a whole number from 1 to 600000; got 0."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "ready": {"output": "x", "timeoutMs": 600001} } } }""",
        "(processes.server.ready): \"timeoutMs\" must be a whole number from 1 to 600000; got 600001."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "ready": {"output": "x", "timeoutMs": 1.5} } } }""",
        "(processes.server.ready): \"timeoutMs\" must be a whole number from 1 to 600000; got 1.5."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "env": {"Delve__DataDir": 1} } } }""",
        "(processes.server): \"env\" of \"server\" must hold strings; \"Delve__DataDir\" is a number."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "cwd": "" } } }""",
        "(processes.server): \"cwd\" must be a non-empty string, the folder the process runs in, relative to the project folder."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "cwd": "C:\\work" } } }""",
        "(processes.server): cwd \"C:\\work\" leaves the project folder; it must be the folder or a folder inside it."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "cwd": "..\\..\\x" } } }""",
        "(processes.server): cwd \"..\\..\\x\" leaves the project folder; it must be the folder or a folder inside it."
    )]
    [InlineData(
        """{ "processes": { "server": { "command": ["a"], "description": "" } } }""",
        "(processes.server): \"description\" must be a non-empty string, what the process is."
    )]
    public void AMalformedProcessesSectionIsRefusedNamingTheFile(string json, string problem)
    {
        Write(json);

        string message = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.Equal($"{FilePath} {problem}", message);
    }

    [Theory]
    [InlineData(@"C:\", @"C:\sub", true)]
    [InlineData(@"C:\", @"C:\x", true)]
    [InlineData(@"C:\", @"C:\other\..\..\x", true)]
    [InlineData(@"C:\folder", @"C:\folder\sub", true)]
    [InlineData(@"C:\folder", @"C:\folder\..\x", false)]
    [InlineData(@"C:\folder", @"D:\folder\sub", false)]
    public void AFolderIsInsideWhenItIsTheFolderOrBelowIt(string folder, string path, bool inside) =>
        Assert.Equal(inside, ProjectProfile.IsInsideFolder(folder, path));

    [Fact]
    public void AnInvalidReadyPatternIsRefusedAtLoadNamingIt()
    {
        Write("""{ "processes": { "server": { "command": ["a"], "ready": {"output": "("} } } }""");

        string message = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.StartsWith(
            $"{FilePath} (processes.server.ready.output): the pattern \"(\" in \"ready.output\" is not a valid .NET regular expression: ",
            message
        );
        Assert.EndsWith(" Fix or remove it.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvLayersTopLevelPresetAndOptionPerVariable()
    {
        Write(
            """
            {
              "env": {"TOP_ONLY": "top", "UNDER_PRESET": "top", "UNDER_CALL": "top"},
              "presets": { "client": { "env": {"UNDER_PRESET": "preset", "UNDER_CALL": "preset", "PRESET_ONLY": "preset"} } }
            }
            """
        );

        IReadOnlyDictionary<string, string> top = Merge(new RunOptions()).Request.Env;
        IReadOnlyDictionary<string, string> preset = Merge(new RunOptions(Preset: "client")).Request.Env;
        IReadOnlyDictionary<string, string> call = Merge(
            new RunOptions(Preset: "client", Env: new() { ["UNDER_CALL"] = "call", ["CALL_ONLY"] = "call" })
        ).Request.Env;

        Assert.Equal(3, top.Count);
        Assert.Equal("top", top["UNDER_PRESET"]);
        Assert.Equal(4, preset.Count);
        Assert.Equal("preset", preset["UNDER_PRESET"]);
        Assert.Equal("top", preset["TOP_ONLY"]);
        Assert.Equal("preset", preset["PRESET_ONLY"]);
        Assert.Equal(5, call.Count);
        Assert.Equal("call", call["UNDER_CALL"]);
        Assert.Equal("call", call["CALL_ONLY"]);
        Assert.Equal("preset", call["UNDER_PRESET"]);
        Assert.Equal("top", call["TOP_ONLY"]);
    }

    [Fact]
    public void EnvKeyIsAllowedAtTopLevelAndInAPreset()
    {
        Write("""{ "env": {"A": "top"}, "presets": { "client": { "env": {"B": "preset"} } } }""");

        ProfileLaunch top = Merge(new RunOptions());
        ProfileLaunch preset = Merge(new RunOptions(Preset: "client"));

        Assert.Equal("top", top.Request.Env["A"]);
        Assert.Equal("top", preset.Request.Env["A"]);
        Assert.Equal("preset", preset.Request.Env["B"]);
    }

    [Fact]
    public void RefusesAnEnvNameTheBridgeOwns()
    {
        Write("""{ "env": {"GODOT_MCP_PORT": "secret"} }""");
        string top = Refused(() => ProjectProfile.Load(_temp.Path));
        Write("""{ "presets": { "client": { "env": {"GODOT_MCP_TOKEN": "secret"} } } }""");
        string preset = Refused(() => ProjectProfile.Load(_temp.Path));
        Write("""{ }""");
        string call = Refused(() => Merge(new RunOptions(Env: new() { ["GODOT_MCP_QUIET"] = "secret" })));

        Assert.Equal(
            $"{FilePath} (top level): \"env\" cannot set \"GODOT_MCP_PORT\": GODOT_MCP_* variables belong to the bridge, which the server "
                + "sets from the run's own options.",
            top
        );
        Assert.Equal(
            $"{FilePath} (preset \"client\"): \"env\" cannot set \"GODOT_MCP_TOKEN\": GODOT_MCP_* variables belong to the bridge, which the "
                + "server sets from the run's own options.",
            preset
        );
        Assert.Equal(
            "options.env cannot set \"GODOT_MCP_QUIET\": GODOT_MCP_* variables belong to the bridge, which the server sets from the run's "
                + "own options.",
            call
        );
        Assert.DoesNotContain("secret", top, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", call, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesANonStringEnvValue()
    {
        Write("""{ "env": {"Delve__DataDir": 1} }""");
        string top = Refused(() => ProjectProfile.Load(_temp.Path));
        Write("""{ "presets": { "client": { "env": {"Delve__DataDir": true} } } }""");
        string preset = Refused(() => ProjectProfile.Load(_temp.Path));

        Assert.Equal($"{FilePath} (top level): \"env\" of top level must hold strings; \"Delve__DataDir\" is a number.", top);
        Assert.Equal($"{FilePath} (preset \"client\"): \"env\" of preset \"client\" must hold strings; \"Delve__DataDir\" is true or false.", preset);
    }

    [Fact]
    public void RefusesAnEnvNameThatIsEmptyOrHoldsAnEquals()
    {
        Write("""{ "env": {"A=B": "x"} }""");
        string inFile = Refused(() => ProjectProfile.Load(_temp.Path));
        Write("""{ "presets": { "client": { "env": {"": "x"} } } }""");
        string emptyInPreset = Refused(() => ProjectProfile.Load(_temp.Path));
        Write("""{ }""");
        string emptyInCall = Refused(() => Merge(new RunOptions(Env: new() { [""] = "x" })));
        string equalsInCall = Refused(() => Merge(new RunOptions(Env: new() { ["A=B"] = "x" })));

        Assert.Equal($"{FilePath} (top level): \"env\" has the name \"A=B\", which is empty or holds '='; name a variable.", inFile);
        Assert.Equal($"{FilePath} (preset \"client\"): \"env\" has the name \"\", which is empty or holds '='; name a variable.", emptyInPreset);
        Assert.Equal("options.env has the name \"\", which is empty or holds '='; name a variable.", emptyInCall);
        Assert.Equal("options.env has the name \"A=B\", which is empty or holds '='; name a variable.", equalsInCall);
    }

    [Fact]
    public void ALowercaseBridgeVariableIsRefused()
    {
        Write("""{ "env": {"godot_mcp_off": "1"} }""");
        string inFile = Refused(() => ProjectProfile.Load(_temp.Path));
        Write("""{ }""");
        string inCall = Refused(() => Merge(new RunOptions(Env: new() { ["godot_mcp_dotnet_error"] = "1" })));

        Assert.Equal(
            $"{FilePath} (top level): \"env\" cannot set \"godot_mcp_off\": GODOT_MCP_* variables belong to the bridge, which the server "
                + "sets from the run's own options.",
            inFile
        );
        Assert.Equal(
            "options.env cannot set \"godot_mcp_dotnet_error\": GODOT_MCP_* variables belong to the bridge, which the server sets from "
                + "the run's own options.",
            inCall
        );
    }

    [Fact]
    public void AValueHoldingANulIsRefused()
    {
        Write("""{ "env": {"A": "x\u0000y"} }""");
        string inFile = Refused(() => ProjectProfile.Load(_temp.Path));
        Write("""{ }""");
        string inCall = Refused(() => Merge(new RunOptions(Env: new() { ["A"] = "x\0y" })));

        Assert.Equal($"{FilePath} (top level): \"env\" gives \"A\" a value holding a NUL character; remove it.", inFile);
        Assert.Equal("options.env gives \"A\" a value holding a NUL character; remove it.", inCall);
    }

    [Fact]
    public void ANullValueInTheCallIsRefused()
    {
        string refusal = Refused(() => Merge(new RunOptions(Env: new() { ["A"] = null! })));

        Assert.Equal("options.env gives \"A\" no value; give it a string.", refusal);
    }

    [Fact]
    public void ACaseDifferingNameInTheCallWinsOverThePreset()
    {
        Write("""{ "env": {"Shared": "top", "TopOnly": "top"}, "presets": { "client": { "env": {"SHARED": "preset"} } } }""");

        IReadOnlyDictionary<string, string> merged = Merge(
            new RunOptions(Preset: "client", Env: new() { ["shared"] = "call", ["CallOnly"] = "call" })
        ).Request.Env;

        Assert.Equal(3, merged.Count);
        Assert.Equal("call", merged["SHARED"]);
        Assert.Equal("call", merged["shared"]);
        Assert.Equal("top", merged["TOPONLY"]);
        Assert.Equal("call", merged["CALLONLY"]);
        Assert.Contains("shared", merged.Keys);
        Assert.DoesNotContain("Shared", merged.Keys);
    }

    [Fact]
    public void TwoNamesDifferingOnlyInCaseAreRefused()
    {
        Write("""{ "env": {"Name": "a", "NAME": "b"} }""");
        string inFile = Refused(() => ProjectProfile.Load(_temp.Path));
        Write("""{ }""");
        string inCall = Refused(() => Merge(new RunOptions(Env: new() { ["Name"] = "a", ["NAME"] = "b" })));

        Assert.Equal($"{FilePath} (top level): \"env\" names \"Name\" and \"NAME\", which are one variable; keep one.", inFile);
        Assert.Equal("options.env names \"Name\" and \"NAME\", which are one variable; keep one.", inCall);
    }

    private static string Refused(Func<object> action) => Assert.Throws<McpException>(action).Message;

    private ProfileLaunch Merge(RunOptions options, string? scene = null, string[]? userArgs = null, string[]? engineArgs = null) =>
        ProjectProfile.Load(_temp.Path).Merge(scene, userArgs ?? [], engineArgs ?? [], options);

    private void Write(string json) => File.WriteAllText(FilePath, json);
}
