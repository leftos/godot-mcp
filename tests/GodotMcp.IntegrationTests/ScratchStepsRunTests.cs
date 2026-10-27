using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using static GodotMcp.IntegrationTests.ScratchRunnerTests;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches against the InputProbe fixture's scratch scenes in the real Godot, for which steps a scene plays and what the
/// result says of them: keep-going and only-red, a step that leaves the game unable to answer, folded errors, a long note under
/// detailChars, details of a green scene, a partial run's steps and its setup steps, and a hidden step's mark. It shares
/// <see cref="ScratchRunnerTests"/>' helpers.
/// </summary>
public sealed class ScratchStepsRunTests : ScratchRunBase
{
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task KeepGoingReportsEveryRedStepAndWithoutItTheSceneStopsAtTheFirst()
    {
        JsonObject kept = Single(
            await RunAsync(_probe.Directory, ["ScratchTwoRed"], new ScratchOptions(KeepGoing: true), TestContext.Current.CancellationToken)
        );
        JsonObject stopped = Single(await RunAsync(_probe.Directory, ["ScratchTwoRed"], options: null, TestContext.Current.CancellationToken));

        Assert.Equal(("red", "red"), (kept["verdict"]!.GetValue<string>(), stopped["verdict"]!.GetValue<string>()));
        Assert.Equal((1, "first"), (kept["failedAt"]!["index"]!.GetValue<int>(), kept["failedAt"]!["name"]!.GetValue<string>()));
        JsonArray failures = kept["failures"]!.AsArray();
        Assert.Equal([1, 2], failures.Select(failure => failure!["index"]!.GetValue<int>()));
        Assert.Equal(["first", "second"], failures.Select(failure => failure!["name"]!.GetValue<string>()));
        Assert.Contains("first independent failure", failures[0]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("second independent failure", failures[1]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal((4, 4), (kept["steps"]!["played"]!.GetValue<int>(), kept["steps"]!["total"]!.GetValue<int>()));
        JsonArray details = kept["details"]!.AsArray();
        Assert.Equal([true, false, false, true], details.Select(step => step!["ok"]!.GetValue<bool>()));
        Assert.Contains("second independent failure", details[2]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(details[3]!["error"]);

        JsonNode only = Assert.Single(stopped["failures"]!.AsArray())!;
        Assert.Equal((1, "first"), (only["index"]!.GetValue<int>(), only["name"]!.GetValue<string>()));
        Assert.Equal(2, stopped["steps"]!["played"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task OnlyRedListsARedScenesRedStepsAloneWithTheirReadback()
    {
        JsonObject scene = Single(
            await RunAsync(
                _probe.Directory,
                ["ScratchTwoRed"],
                new ScratchOptions(KeepGoing: true, Only: "red"),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal((4, 4), (scene["steps"]!["played"]!.GetValue<int>(), scene["steps"]!["total"]!.GetValue<int>()));
        JsonArray details = scene["details"]!.AsArray();
        Assert.Equal([1, 2], details.Select(step => step!["index"]!.GetValue<int>()));
        Assert.All(details, step => Assert.False(step!["ok"]!.GetValue<bool>()));
        Assert.Contains("first independent failure", details[0]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("second independent failure", details[1]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal([1, 2], scene["failures"]!.AsArray().Select(failure => failure!["index"]!.GetValue<int>()));
        Assert.Equal(1, scene["failedAt"]!["index"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task KeepGoingStillStopsAtAStepThatLeftTheGameUnableToAnswer()
    {
        JsonObject scene = Single(
            await RunAsync(_probe.Directory, ["ScratchQuit"], new ScratchOptions(KeepGoing: true), TestContext.Current.CancellationToken)
        );

        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal((1, "quit"), (scene["failedAt"]!["index"]!.GetValue<int>(), scene["failedAt"]!["name"]!.GetValue<string>()));
        Assert.Equal((2, 4), (scene["steps"]!["played"]!.GetValue<int>(), scene["steps"]!["total"]!.GetValue<int>()));
        JsonNode failure = Assert.Single(scene["failures"]!.AsArray())!;
        Assert.Equal(1, failure["index"]!.GetValue<int>());
        Assert.Equal(["calm", "quit"], scene["details"]!.AsArray().Select(step => step!["name"]!.GetValue<string>()));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AStepThatErrorsEveryFrameListsItOnceWithItsCount()
    {
        string raw = await _scratch.RunScratchesAsync(
            _probe.Directory,
            ["ScratchFlood"],
            new ScratchOptions(Details: FullDetails),
            TestContext.Current.CancellationToken
        );

        JsonObject scene = Single(JsonNode.Parse(raw)!.AsObject());
        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal((1, "floods"), (scene["failedAt"]!["index"]!.GetValue<int>(), scene["failedAt"]!["name"]!.GetValue<string>()));
        JsonNode error = Assert.Single(scene["details"]![1]!["errors"]!.AsArray())!;
        Assert.StartsWith("the same error every frame", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(error["count"]!.GetValue<int>() > 1, error.ToJsonString());
        Assert.True(raw.Length < 60_000, $"{raw.Length} characters");
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ALongNoteIsReturnedWholeUnderDetailChars()
    {
        string note = new('n', 1500);

        JsonObject scene = Single(
            await RunAsync(
                _probe.Directory,
                ["ScratchLongNote"],
                new ScratchOptions(Details: FullDetails, DetailChars: 2000),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        JsonNode step = scene["details"]![0]!;
        Assert.EndsWith(note, Status(step), StringComparison.Ordinal);
        List<string> lines = [.. step["lines"]!.AsArray().Select(line => line!.GetValue<string>())];
        Assert.Contains(lines, line => line.Contains(note, StringComparison.Ordinal) && !line.Contains("… (+", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task DetailsListAGreenScenesStepsAndAKnownGreenSceneCountsAsRed()
    {
        string profile = """{ "scratch": { "folder": "scratch", "known": { "ScratchGreen": "was flaky" } } }""";
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ProjectProfile.FileName), profile, TestContext.Current.CancellationToken);

        JsonObject result = await RunAsync(
            _probe.Directory,
            ["ScratchGreen"],
            new ScratchOptions(Details: FullDetails, Pace: 0.25),
            TestContext.Current.CancellationToken
        );

        JsonObject scene = Single(result);
        Assert.Equal("known-now-green", scene["verdict"]!.GetValue<string>());
        Assert.False(result["passed"]!.GetValue<bool>());
        Assert.Equal(1, result["red"]!.GetValue<int>());
        Assert.Equal(0.25, scene["pace"]!.GetValue<double>());
        JsonArray details = scene["details"]!.AsArray();
        Assert.Equal(["open", "move", "close"], details.Select(step => step!["name"]!.GetValue<string>()));
        Assert.Equal(["opened", "moved", "closed"], details.Select(step => Status(step!)));
        Assert.All(details, step => Assert.Equal(250, step!["gameMs"]!.GetValue<int>()));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task CompactDetailsListAGreenScenesStepsAsOneLineEach()
    {
        string profile = """{ "scratch": { "folder": "scratch" } }""";
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ProjectProfile.FileName), profile, TestContext.Current.CancellationToken);

        JsonObject scene = Single(
            await RunAsync(
                _probe.Directory,
                ["ScratchGreen"],
                new ScratchOptions(Details: Tokens("\"compact\""), Pace: 0.25),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        List<string> lines = [.. scene["details"]!.AsArray().Select(step => step!.GetValue<string>())];
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("0 ok open", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("1 ok move", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("2 ok close", lines[2], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task APartialRunPlaysTheScenesSetupStepFirst()
    {
        CopyFolder(Path.Combine(RepoPaths.InputProbe, "scratch_setup"), Path.Combine(_probe.Directory, "scratch"));
        await File.WriteAllTextAsync(
            Path.Combine(_probe.Directory, ProjectProfile.FileName),
            """{ "scratch": { "folder": "scratch", "setup": { "ScratchSetupNeeded": ["build"] } } }""",
            TestContext.Current.CancellationToken
        );
        ScratchOptions options = new(Details: FullDetails, Steps: Tokens("""["use"]"""));

        JsonObject green = await RunAsync(_probe.Directory, ["ScratchSetupNeeded"], options, TestContext.Current.CancellationToken);
        JsonObject scene = Single(green);

        Assert.True(scene["verdict"]!.GetValue<string>() == "green", scene.ToJsonString());
        Assert.Equal([1], scene["selected"]!.AsArray().Select(index => index!.GetValue<int>()));
        Assert.Equal([0], scene["setup"]!.AsArray().Select(index => index!.GetValue<int>()));
        Assert.Equal(["build", "use"], scene["details"]!.AsArray().Select(step => step!["name"]!.GetValue<string>()));
        Assert.Equal(["built", "used"], scene["details"]!.AsArray().Select(step => Status(step!)));

        await File.WriteAllTextAsync(
            Path.Combine(_probe.Directory, ProjectProfile.FileName),
            """{ "scratch": { "folder": "scratch" } }""",
            TestContext.Current.CancellationToken
        );

        JsonObject red = Single(await RunAsync(_probe.Directory, ["ScratchSetupNeeded"], options, TestContext.Current.CancellationToken));

        Assert.True(red["verdict"]!.GetValue<string>() == "red", red.ToJsonString());
        Assert.True(red["failedAt"]?["index"]?.GetValue<int>() == 1, red.ToJsonString());
        Assert.Equal((1, "use"), (red["failedAt"]!["index"]!.GetValue<int>(), red["failedAt"]!["name"]!.GetValue<string>()));
        Assert.Contains("use ran before build", red["failedAt"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal([1], red["selected"]!.AsArray().Select(index => index!.GetValue<int>()));
        Assert.False(red.ContainsKey("setup"), red.ToJsonString());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task StepsPlaysOnlyTheSelectedStepsAndTheOthersNeverRun()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject last = Single(
            await RunAsync(_probe.Directory, ["ScratchTwoRed"], new ScratchOptions(Steps: Tokens("""["last"]""")), cancellation)
        );
        JsonObject middle = Single(
            await RunAsync(_probe.Directory, ["ScratchTwoRed"], new ScratchOptions(KeepGoing: true, Steps: Tokens("""["1-2"]""")), cancellation)
        );
        JsonObject unknown = Single(
            await RunAsync(_probe.Directory, ["ScratchTwoRed"], new ScratchOptions(Steps: Tokens("""["nope"]""")), cancellation)
        );

        Assert.Equal("green", last["verdict"]!.GetValue<string>());
        Assert.Equal((1, 4), (last["steps"]!["played"]!.GetValue<int>(), last["steps"]!["total"]!.GetValue<int>()));
        Assert.Equal([3], Indices(last["selected"]!));

        Assert.Equal("red", middle["verdict"]!.GetValue<string>());
        Assert.Equal((2, 4), (middle["steps"]!["played"]!.GetValue<int>(), middle["steps"]!["total"]!.GetValue<int>()));
        Assert.Equal([1, 2], Indices(middle["selected"]!));
        Assert.Equal([1, 2], middle["failures"]!.AsArray().Select(failure => failure!["index"]!.GetValue<int>()));
        Assert.Equal(["first", "second"], middle["details"]!.AsArray().Select(step => step!["name"]!.GetValue<string>()));

        JsonNode failedAt = unknown["failedAt"]!;
        Assert.Equal("red", unknown["verdict"]!.GetValue<string>());
        Assert.Equal(-1, failedAt["index"]!.GetValue<int>());
        Assert.Equal(
            "options.steps token \"nope\" of ScratchTwoRed matches no step; its steps are 0 calm, 1 first, 2 second, 3 last",
            failedAt["error"]!.GetValue<string>()
        );
        Assert.Equal((0, 4), (unknown["steps"]!["played"]!.GetValue<int>(), unknown["steps"]!["total"]!.GetValue<int>()));
        Assert.Null(unknown["selected"]);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AStepsMapPlaysEachScenesOwnSteps()
    {
        ScratchOptions options = new(Details: FullDetails, Steps: Tokens("""{ "ScratchTwoRed": ["last"], "ScratchGreen": [0, "close"] }"""));

        JsonObject result = await RunAsync(_probe.Directory, ["ScratchTwoRed", "ScratchGreen"], options, TestContext.Current.CancellationToken);
        JsonArray scenes = result["scenes"]!.AsArray();

        Assert.True(result["passed"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal([3], Indices(scenes[0]!["selected"]!));
        Assert.Equal(["last"], scenes[0]!["details"]!.AsArray().Select(step => step!["name"]!.GetValue<string>()));
        Assert.Equal([0, 2], Indices(scenes[1]!["selected"]!));
        Assert.Equal(["open", "close"], scenes[1]!["details"]!.AsArray().Select(step => step!["name"]!.GetValue<string>()));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task APartialRunOfASceneWithAHiddenStepListsItsStepsWithTheMark()
    {
        ScratchOptions options = new(Steps: Tokens("[1]"));

        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchHidden"], options, TestContext.Current.CancellationToken));

        Assert.True(scene["verdict"]!.GetValue<string>() == "green", scene.ToJsonString());
        Assert.Equal([1], Indices(scene["selected"]!));
        Assert.Equal(
            ["0 (hidden) fight setup", "1 first view", "2 second view"],
            scene["stepList"]!.AsArray().Select(line => line!.GetValue<string>())
        );
    }

    private static JsonElement Tokens(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static IEnumerable<int> Indices(JsonNode indices) => indices.AsArray().Select(index => index!.GetValue<int>());
}
