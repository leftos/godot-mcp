using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Agents;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Agents;

/// <summary>
/// --run-scratches' arguments, its verdict lines and its exit status, with no Godot: the one run here is refused at the plan, so
/// nothing launches. The result JSON is the tool's own shape, which the itest reads from a real run.
/// </summary>
public sealed class ScratchCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;

    public ScratchCommandTests() => _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);

    public void Dispose()
    {
        _sessions.Dispose();
        _listener.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void ParseReadsTheProjectTheScenesAndEveryOption()
    {
        ScratchArguments? parsed = ScratchCommand.Parse(
            Words(
                "--run-scratches C:/game --scene A --scene res://X.tscn --pace 0.2 --time-scale 4 --keep-going --parallel 2 "
                    + "--prepare auto --details compact --only red --json"
            ),
            out string? error
        );

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal("C:/game", parsed.ProjectPath);
        Assert.Equal(["A", "res://X.tscn"], parsed.Scenes!);
        Assert.Equal(0.2, parsed.Options.Pace);
        Assert.Equal(4, parsed.Options.TimeScale);
        Assert.True(parsed.Options.KeepGoing);
        Assert.Equal(2, parsed.Options.Parallel);
        Assert.Equal("auto", parsed.Options.Prepare);
        Assert.Equal("compact", parsed.Options.Details?.GetString());
        Assert.Equal("red", parsed.Options.Only);
        Assert.True(parsed.Json);
    }

    [Fact]
    public void ParseLeavesTheOptionsItWasNotGivenAtTheToolsDefaults()
    {
        ScratchArguments parsed = ScratchCommand.Parse(Words("--run-scratches C:/game"), out string? error)!;

        Assert.Null(error);
        Assert.Null(parsed.Scenes);
        Assert.Null(parsed.Options.Pace);
        Assert.Null(parsed.Options.TimeScale);
        Assert.Null(parsed.Options.Parallel);
        Assert.Null(parsed.Options.Prepare);
        Assert.Null(parsed.Options.Details);
        Assert.Null(parsed.Options.Only);
        Assert.Null(parsed.Options.UserData);
        Assert.Null(parsed.Options.KeepGoing);
        Assert.Null(parsed.Options.Steps);
        Assert.Null(parsed.Options.Ceilings);
        Assert.Null(parsed.Options.MemoryCeilings);
        Assert.Null(parsed.Options.UserArgs);
        Assert.Null(parsed.Options.EngineArgs);
        Assert.Null(parsed.Options.Windowed);
        Assert.Null(parsed.Options.Audible);
        Assert.Null(parsed.Options.Capture);
        Assert.False(parsed.Json);
    }

    [Fact]
    public void ParseCollectsEachUserArgAndEngineArgInOrder()
    {
        ScratchArguments? parsed = ScratchCommand.Parse(
            [
                "--run-scratches",
                "C:/game",
                "--user-arg",
                "--shadow-quality=Low",
                "--engine-arg",
                "--resolution",
                "--engine-arg",
                "1280x720",
                "--user-arg",
                "plain",
            ],
            out string? error
        );

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(["--shadow-quality=Low", "plain"], parsed.Options.UserArgs!);
        Assert.Equal(["--resolution", "1280x720"], parsed.Options.EngineArgs!);
    }

    [Fact]
    public void ParseKeepsReadingOptionsAfterAUserArgValueThatLooksLikeOne()
    {
        ScratchArguments? parsed = ScratchCommand.Parse(["--run-scratches", "C:/game", "--user-arg", "--x", "--json"], out string? error);

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.True(parsed.Json);
        Assert.Equal(["--x"], parsed.Options.UserArgs!);
    }

    [Theory]
    [InlineData("real", "\"real\"")]
    [InlineData("scratch", "\"scratch\"")]
    [InlineData("seed=Saves/base", "{\"seed\":\"Saves/base\"}")]
    [InlineData("seed=a=b", "{\"seed\":\"a=b\"}")]
    public void ParseReadsEachUserDataForm(string value, string json)
    {
        ScratchArguments? parsed = ScratchCommand.Parse(["--run-scratches", "C:/game", "--user-data", value], out string? error);

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(json, JsonNode.Parse(parsed.Options.UserData!.Value.GetRawText())!.ToJsonString());
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("Real")]
    [InlineData("seed=")]
    [InlineData("seed")]
    public void ParseRefusesAUserDataOfNoForm(string value)
    {
        Assert.Null(ScratchCommand.Parse(["--run-scratches", "game", "--user-data", value], out string? error));
        Assert.Equal($"--user-data takes real, scratch or seed=<folder>, not '{value}'.", error);
    }

    [Theory]
    [InlineData("--run-scratches", "--run-scratches needs a project folder.")]
    [InlineData("--run-scratches game other", "--run-scratches takes one project folder, not 'other'.")]
    [InlineData("--run-scratches game --bogus value", "--run-scratches does not take '--bogus'.")]
    [InlineData("--run-scratches game --scene", "--scene needs a value.")]
    [InlineData("--run-scratches game --scene --json", "--scene needs a value.")]
    [InlineData("--run-scratches game --user-arg", "--user-arg needs a value.")]
    [InlineData("--run-scratches game --engine-arg", "--engine-arg needs a value.")]
    [InlineData("--run-scratches game --pace abc", "--pace takes a number, not 'abc'.")]
    [InlineData("--run-scratches game --time-scale soon", "--time-scale takes a number, not 'soon'.")]
    [InlineData("--run-scratches game --parallel two", "--parallel takes a number, not 'two'.")]
    [InlineData("--run-scratches game --user-data", "--user-data needs a value.")]
    public void ParseRefusesTheArgumentsItCannotRead(string line, string refusal)
    {
        Assert.Null(ScratchCommand.Parse(Words(line), out string? error));
        Assert.Equal(refusal, error);
    }

    [Theory]
    [InlineData("--run-scratches game --background", "--run-scratches does not take '--background'.")]
    [InlineData("--run-scratches game --background true", "--run-scratches does not take '--background'.")]
    [InlineData("--run-scratches game --session cli", "--run-scratches does not take '--session'.")]
    public void ParseNamesAnUnknownOptionAsSuchBeforeItAsksForItsValue(string line, string refusal)
    {
        Assert.Null(ScratchCommand.Parse(Words(line), out string? error));
        Assert.Equal(refusal, error);
    }

    [Theory]
    [InlineData("--run-scratches game --pace 1 --pace 2", "--pace is given twice; give it once.")]
    [InlineData("--run-scratches game --details full --details compact", "--details is given twice; give it once.")]
    [InlineData("--run-scratches game --only red --only red", "--only is given twice; give it once.")]
    [InlineData("--run-scratches game --user-data real --user-data scratch", "--user-data is given twice; give it once.")]
    public void ParseRefusesANonRepeatableOptionGivenTwice(string line, string refusal)
    {
        Assert.Null(ScratchCommand.Parse(Words(line), out string? error));
        Assert.Equal(refusal, error);
    }

    [Fact]
    public void ParseReadsTheWindowedAudibleStepsAndCaptureSwitches()
    {
        ScratchArguments? parsed = ScratchCommand.Parse(
            Words("--run-scratches C:/game --windowed --audible --steps 0 --steps calm --capture --capture-step 1 --capture-step close"),
            out string? error
        );

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.True(parsed.Options.Windowed);
        Assert.True(parsed.Options.Audible);
        Assert.Equal(["0", "calm"], parsed.Options.Steps!.Value.EnumerateArray().Select(token => token.GetString()!));
        Assert.True(parsed.Options.Capture!.Screenshot);
        Assert.Equal(["1", "close"], parsed.Options.Capture.Steps!.Value.EnumerateArray().Select(token => token.GetString()!));
    }

    [Fact]
    public void ParseReadsCeilingsMemoryCeilingsAndBatches()
    {
        ScratchArguments? parsed = ScratchCommand.Parse(
            Words("--run-scratches C:/game --ceiling A=30 --ceiling B=1.5 --memory-ceiling A=2048 --batch A=0,2-4 --batch A=6-9 --batch B=calm"),
            out string? error
        );

        Assert.Null(error);
        Assert.NotNull(parsed);
        JsonObject ceilings = JsonNode.Parse(parsed.Options.Ceilings!.Value.GetRawText())!.AsObject();
        Assert.Equal(2, ceilings.Count);
        Assert.Equal(30d, ceilings["A"]!.GetValue<double>());
        Assert.Equal(1.5d, ceilings["B"]!.GetValue<double>());
        Assert.Single(parsed.Options.MemoryCeilings!);
        Assert.Equal(2048d, parsed.Options.MemoryCeilings!["A"]);
        Assert.Equal("""{"A":[["0","2-4"],["6-9"]],"B":[["calm"]]}""", JsonNode.Parse(parsed.Options.Steps!.Value.GetRawText())!.ToJsonString());
    }

    [Fact]
    public void ParseSplitsACeilingAtItsLastEqualsAndABatchAtItsFirst()
    {
        ScratchArguments? parsed = ScratchCommand.Parse(Words("--run-scratches C:/game --ceiling A=B=5 --batch S=x=y"), out string? error);

        Assert.Null(error);
        Assert.NotNull(parsed);
        JsonObject ceilings = JsonNode.Parse(parsed.Options.Ceilings!.Value.GetRawText())!.AsObject();
        KeyValuePair<string, JsonNode?> ceiling = Assert.Single(ceilings);
        Assert.Equal("A=B", ceiling.Key);
        Assert.Equal(5d, ceiling.Value!.GetValue<double>());
        Assert.Equal("""{"S":[["x=y"]]}""", JsonNode.Parse(parsed.Options.Steps!.Value.GetRawText())!.ToJsonString());
    }

    [Fact]
    public void ParseReadsADerivedCeilingAndOneValuePerBatch()
    {
        ScratchArguments? parsed = ScratchCommand.Parse(
            Words("--run-scratches C:/game --ceiling A=derived --ceiling B#0=10 --ceiling B#1=derived --ceiling B#2=2.5"),
            out string? error
        );

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal("""{"A":null,"B":[10,null,2.5]}""", JsonNode.Parse(parsed.Options.Ceilings!.Value.GetRawText())!.ToJsonString());
    }

    [Fact]
    public void ParseSplitsACeilingsSceneFromItsBatchAtTheLastHash()
    {
        ScratchArguments? parsed = ScratchCommand.Parse(
            Words("--run-scratches C:/game --ceiling Foo#2#0=5 --ceiling Foo#0=1 --ceiling Foo#1=2 --ceiling Foo#2=3"),
            out string? error
        );

        Assert.Null(error);
        Assert.NotNull(parsed);
        JsonObject ceilings = JsonNode.Parse(parsed.Options.Ceilings!.Value.GetRawText())!.AsObject();
        Assert.Equal(2, ceilings.Count);
        Assert.Equal("[5]", ceilings["Foo#2"]!.ToJsonString());
        Assert.Equal("[1,2,3]", ceilings["Foo"]!.ToJsonString());
    }

    [Theory]
    [InlineData("A=NaN")]
    [InlineData("A#0=Infinity")]
    [InlineData("A=1e999")]
    public void ANonFiniteCeilingIsRefusedWithExitTwo(string value)
    {
        StringWriter output = new();
        StringWriter error = new();

        int status = ScratchCommand.Run(["--run-scratches", "game", "--ceiling", value], output, error);

        Assert.Equal(2, status);
        Assert.Equal("", output.ToString());
        Assert.StartsWith(
            $"godot-mcp: --ceiling takes <scene>=<seconds|derived> or <scene>#<batch>=<seconds|derived>, not '{value}'. ",
            error.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void AUserDataOfNoFormIsRefusedWithExitTwo()
    {
        StringWriter output = new();
        StringWriter error = new();

        int status = ScratchCommand.Run(["--run-scratches", "game", "--user-data", "bogus"], output, error);

        Assert.Equal(2, status);
        Assert.Equal("", output.ToString());
        Assert.StartsWith("godot-mcp: --user-data takes real, scratch or seed=<folder>, not 'bogus'. ", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--ceiling A=1 --ceiling A#0=2", "--ceiling gives 'A' twice; give it once.")]
    [InlineData("--ceiling A#1=5", "--ceiling gives 'A' batch 1 but not batch 0; give every batch from 0, or give the scene one ceiling.")]
    [InlineData(
        "--ceiling A#0=1 --ceiling A#2=3",
        "--ceiling gives 'A' batch 2 but not batch 1; give every batch from 0, or give the scene one ceiling."
    )]
    [InlineData("--ceiling A#0=1 --ceiling A#0=2", "--ceiling gives 'A#0' twice; give it once.")]
    public void ParseRefusesACeilingGivenTwiceOrWithABatchMissing(string line, string refusal)
    {
        Assert.Null(ScratchCommand.Parse(Words($"--run-scratches game {line}"), out string? error));
        Assert.Equal(refusal, error);
    }

    [Theory]
    [InlineData("--ceiling A", "--ceiling takes <scene>=<seconds|derived> or <scene>#<batch>=<seconds|derived>, not 'A'.")]
    [InlineData("--ceiling =5", "--ceiling takes <scene>=<seconds|derived> or <scene>#<batch>=<seconds|derived>, not '=5'.")]
    [InlineData("--ceiling A=soon", "--ceiling takes <scene>=<seconds|derived> or <scene>#<batch>=<seconds|derived>, not 'A=soon'.")]
    [InlineData("--ceiling A#0=soon", "--ceiling takes <scene>=<seconds|derived> or <scene>#<batch>=<seconds|derived>, not 'A#0=soon'.")]
    [InlineData("--memory-ceiling A=", "--memory-ceiling takes <scene>=<megabytes>, not 'A='.")]
    [InlineData("--batch A", "--batch takes <scene>=<token>[,<token>]..., not 'A'.")]
    [InlineData("--batch =0", "--batch takes <scene>=<token>[,<token>]..., not '=0'.")]
    [InlineData("--batch A=", "--batch takes <scene>=<token>[,<token>]..., not 'A='.")]
    [InlineData("--batch A=0,,1", "--batch takes <scene>=<token>[,<token>]..., not 'A=0,,1'.")]
    [InlineData("--batch A=0,", "--batch takes <scene>=<token>[,<token>]..., not 'A=0,'.")]
    [InlineData("--ceiling A=1 --ceiling A=2", "--ceiling gives 'A' twice; give it once.")]
    [InlineData("--memory-ceiling A=100 --memory-ceiling A=200", "--memory-ceiling gives 'A' twice; give it once.")]
    [InlineData(
        "--steps 0 --batch A=1",
        "--steps and --batch cannot be combined: --steps gives every scene one list, --batch gives a scene its own batches."
    )]
    [InlineData("--ceiling", "--ceiling needs a value.")]
    public void ParseRefusesAMalformedSceneKeyedValue(string line, string refusal)
    {
        Assert.Null(ScratchCommand.Parse(Words($"--run-scratches game {line}"), out string? error));
        Assert.Equal(refusal, error);
    }

    [Fact]
    public void ParseShootsEveryPlayedStepWhenCaptureIsGivenNoStepOfItsOwn()
    {
        ScratchArguments parsed = ScratchCommand.Parse(Words("--run-scratches C:/game --capture"), out string? error)!;

        Assert.Null(error);
        Assert.True(parsed.Options.Capture!.Screenshot);
        Assert.Null(parsed.Options.Capture.Steps);
    }

    [Fact]
    public void ParseRefusesACaptureStepWithoutACapture()
    {
        Assert.Null(ScratchCommand.Parse(Words("--run-scratches game --capture-step 1"), out string? error));
        Assert.Equal("--capture-step needs --capture", error);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void ExitIsZeroOnlyWhenTheRunPassed(bool passed, int expected) => Assert.Equal(expected, ScratchCommand.Exit(Result(passed)));

    [Fact]
    public void DescribePrintsABlockASceneAndTheFailedSummary()
    {
        JsonObject result = JsonNode
            .Parse(
                """
                {
                  "passed": false, "green": 1, "red": 2, "killed": 1, "known": 0, "noSteps": 0,
                  "scenes": [
                    { "scene": "ScratchGreen", "verdict": "green", "steps": { "played": 3, "total": 3 }, "seconds": 2,
                      "exit": { "code": 0 } },
                    { "scene": "ScratchRed", "verdict": "red", "steps": { "played": 1, "total": 2 }, "seconds": 0.5,
                      "failedAt": { "index": 1, "name": "two", "error": "boom" }, "exit": { "code": 0 } },
                    { "scene": "ScratchKilled", "verdict": "killed", "steps": { "played": 0, "total": 2 }, "seconds": 30,
                      "exit": { "code": null, "error": "the game stopped answering", "killed": true, "killReason": "the ceiling" } },
                    { "scene": "ScratchExpect", "verdict": "red", "steps": { "played": 2, "total": 2 }, "seconds": 1,
                      "exit": { "code": 0, "missing": ["scene painted (it never prints its colour)", "a bell"] } }
                  ]
                }
                """
            )!
            .AsObject();
        string expected = """
            ScratchGreen: green (3/3 steps, 2.0s)
            ScratchRed: red (1/2 steps, 0.5s)
              failed at step 1 two: boom
            ScratchKilled: killed (0/2 steps, 30.0s)
              exit: the game stopped answering
              exit: killed (the ceiling)
            ScratchExpect: red (2/2 steps, 1.0s)
              exit: scene painted (it never prints its colour)
              exit: a bell
            scratch: failed (1 green, 2 red, 1 killed, 0 known, 0 no steps)
            """;

        AssertLines(expected, ScratchCommand.Describe(result));
    }

    [Fact]
    public void DescribeSummarisesAPassedRun()
    {
        JsonObject result = JsonNode
            .Parse(
                """
                {
                  "passed": true, "green": 2, "red": 0, "killed": 0, "known": 1, "noSteps": 0,
                  "scenes": [
                    { "scene": "ScratchGreen", "verdict": "green", "steps": { "played": 3, "total": 3 }, "seconds": 2,
                      "exit": { "code": 0 } },
                    { "scene": "ScratchKnown", "verdict": "known-now-green", "steps": { "played": 2, "total": 2 },
                      "seconds": 0.4, "exit": { "code": 0 } }
                  ]
                }
                """
            )!
            .AsObject();
        string expected = """
            ScratchGreen: green (3/3 steps, 2.0s)
            ScratchKnown: known-now-green (2/2 steps, 0.4s)
            scratch: passed (2 green, 1 known, 0 no steps)
            """;

        AssertLines(expected, ScratchCommand.Describe(result));
    }

    [Fact]
    public void DescribeNamesTheFolderOfAScenesShotsAndNoLineForASceneWithoutOne()
    {
        string shot = "C:/game/.godot/godot-mcp/scratch-1/ScratchGreen/0-open.png";
        JsonObject result = JsonNode
            .Parse(
                $$"""
                {
                  "passed": true, "green": 2, "red": 0, "killed": 0, "known": 0, "noSteps": 0,
                  "scenes": [
                    { "scene": "ScratchGreen", "verdict": "green", "steps": { "played": 1, "total": 1 }, "seconds": 1,
                      "details": [ { "index": 0, "name": "open", "ok": true, "status": "opened", "screenshot": "{{shot}}" } ],
                      "exit": { "code": 0 } },
                    { "scene": "ScratchPlain", "verdict": "green", "steps": { "played": 1, "total": 1 }, "seconds": 1,
                      "exit": { "code": 0 } }
                  ]
                }
                """
            )!
            .AsObject();
        string expected = $"""
            ScratchGreen: green (1/1 steps, 1.0s)
              shots: {Path.GetDirectoryName(shot)}
            ScratchPlain: green (1/1 steps, 1.0s)
            scratch: passed (2 green, 0 known, 0 no steps)
            """;

        AssertLines(expected, ScratchCommand.Describe(result));
    }

    [Fact]
    public void DescribeMarksEachBatchOfASceneInItsLine()
    {
        JsonObject result = JsonNode
            .Parse(
                """
                {
                  "passed": false, "green": 1, "red": 1, "killed": 0, "known": 0, "noSteps": 0,
                  "scenes": [
                    { "scene": "ScratchTwoRed", "verdict": "green", "steps": { "played": 1, "total": 4 }, "seconds": 1,
                      "batch": 0, "batches": 2, "exit": { "code": 0 } },
                    { "scene": "ScratchTwoRed", "verdict": "red", "steps": { "played": 1, "total": 4 }, "seconds": 0.5,
                      "batch": 1, "batches": 2, "failedAt": { "index": 1, "name": "first", "error": "boom" }, "exit": { "code": 0 } }
                  ]
                }
                """
            )!
            .AsObject();
        string expected = """
            ScratchTwoRed [batch 1/2]: green (1/4 steps, 1.0s)
            ScratchTwoRed [batch 2/2]: red (1/4 steps, 0.5s)
              failed at step 1 first: boom
            scratch: failed (1 green, 1 red, 0 killed, 0 known, 0 no steps)
            """;

        AssertLines(expected, ScratchCommand.Describe(result));
    }

    [Fact]
    public async Task ARunOfARefusedPlanExitsTwoWithTheMessageOnStderr()
    {
        string project = _temp.Combine("game");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "project.godot"), "config_version=5\n", TestContext.Current.CancellationToken);
        ScratchArguments arguments = ScratchCommand.Parse(["--run-scratches", project], out _)!;
        StringWriter output = new();
        StringWriter error = new();

        int status = await ScratchCommand.RunAsync(
            new ScratchTools(_sessions, TestCSharp.Unused()),
            arguments,
            output,
            error,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, status);
        Assert.Equal("", output.ToString());
        Assert.StartsWith("godot-mcp: ", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("has no scratch section", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARunThatThrowsAnotherFailureExitsTwoWithItsMessage()
    {
        // A path the runtime itself refuses is no McpException, so it reaches the command's catch of every other failure.
        StringWriter output = new();
        StringWriter error = new();

        int status = ScratchCommand.Run(["--run-scratches", "not\0a path"], output, error);

        Assert.Equal(2, status);
        Assert.Equal("", output.ToString());
        Assert.StartsWith("godot-mcp: ", error.ToString(), StringComparison.Ordinal);
    }

    private static JsonObject Result(bool passed) =>
        new()
        {
            ["passed"] = passed,
            ["green"] = 0,
            ["red"] = 0,
            ["killed"] = 0,
            ["known"] = 0,
            ["noSteps"] = 0,
            ["scenes"] = new JsonArray(),
        };

    /// <summary>The lines of both, the platform's line ends and the text's own last one dropped.</summary>
    private static void AssertLines(string expected, string actual) => Assert.Equal(Lines(expected), Lines(actual));

    private static string[] Lines(string text)
    {
        string[] lines = [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static string[] Words(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
