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
        Assert.Null(parsed.Options.KeepGoing);
        Assert.Null(parsed.Options.UserArgs);
        Assert.Null(parsed.Options.EngineArgs);
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
    public void ParseRefusesANonRepeatableOptionGivenTwice(string line, string refusal)
    {
        Assert.Null(ScratchCommand.Parse(Words(line), out string? error));
        Assert.Equal(refusal, error);
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
