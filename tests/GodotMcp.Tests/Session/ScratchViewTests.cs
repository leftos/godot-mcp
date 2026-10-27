using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>The cap on a scratch result's size: which scenes' steps it leaves out, what it names, and the spill file it writes.</summary>
public sealed class ScratchViewTests : IDisposable
{
    private const string RunId = "20261019T222144-3f2a";

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly string _project;
    private readonly string _logFolder;
    private readonly ScratchSpillPlace _place;

    public ScratchViewTests()
    {
        _project = _temp.Combine("game");
        _logFolder = Path.Combine(_project, ".godot", "godot-mcp");
        Directory.CreateDirectory(_project);
        _place = new ScratchSpillPlace(_project, Now, NullLogger.Instance);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AResultPastTheCapDropsGreenScenesStepsFromTheLastAndNamesTheFile()
    {
        JsonObject result = Result(Scene("A", "green", 10), Scene("B", "red", 5), Scene("C", "green", 8), Scene("D", "green", 0));
        string before = Text(result);

        JsonObject capped = ScratchView.CappedResult(result, result, _place);

        string path = Assert.Single(Directory.GetFiles(_logFolder));
        Assert.Matches(@"scratch-result-20261027-120000-[0-9a-f]{4}\.json$", path);
        Assert.Equal(before, Text(result));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before), JsonNode.Parse(File.ReadAllText(path))), "the spill file holds the whole result");
        JsonArray scenes = capped["scenes"]!.AsArray();
        Assert.Equal(10, scenes[0]!["details"]!.AsArray().Count);
        Assert.Equal(5, scenes[1]!["details"]!.AsArray().Count);
        Assert.False(scenes[2]!.AsObject().ContainsKey("details"), scenes[2]!.ToJsonString());
        Assert.Equal(8, scenes[2]!["detailsOmitted"]!.GetValue<int>());
        Assert.False(scenes[3]!.AsObject().ContainsKey("detailsOmitted"), scenes[3]!.ToJsonString());
        Assert.Equal(path, capped["full"]!.GetValue<string>());
        Assert.Equal(Cut(before.Length, 1, path), capped["cut"]!.GetValue<string>());
        Assert.True(Text(capped).Length <= ScratchView.MaxResultChars, $"{Text(capped).Length} characters");
    }

    [Fact]
    public void AGreenScenesStepsGoBeforeALaterRedScenesWhenEitherWouldFit()
    {
        JsonObject result = Result(Scene("A", "green", 12), Scene("B", "red", 12));

        JsonArray scenes = ScratchView.CappedResult(result, result, _place)["scenes"]!.AsArray();

        Assert.Equal(12, scenes[0]!["detailsOmitted"]!.GetValue<int>());
        Assert.False(scenes[0]!.AsObject().ContainsKey("details"), scenes[0]!.ToJsonString());
        Assert.Equal(12, scenes[1]!["details"]!.AsArray().Count);
        Assert.False(scenes[1]!.AsObject().ContainsKey("detailsOmitted"), scenes[1]!.ToJsonString());
    }

    [Fact]
    public void AResultThatStillDoesNotFitWithEveryScenesStepsLeftOutIsReturnedWithFullAndCut()
    {
        JsonObject red = Scene("B", "red", 3);
        red["known"] = new string('k', 25000);
        JsonObject result = Result(Scene("A", "green", 3), red);
        int length = Text(result).Length;

        JsonObject capped = ScratchView.CappedResult(result, result, _place);

        string path = Assert.Single(Directory.GetFiles(_logFolder));
        Assert.All(capped["scenes"]!.AsArray(), scene => Assert.Equal(3, scene!["detailsOmitted"]!.GetValue<int>()));
        Assert.All(capped["scenes"]!.AsArray(), scene => Assert.False(scene!.AsObject().ContainsKey("details")));
        Assert.Equal(path, capped["full"]!.GetValue<string>());
        Assert.Equal(Cut(length, 2, path), capped["cut"]!.GetValue<string>());
        Assert.True(Text(capped).Length > ScratchView.MaxResultChars);
    }

    [Fact]
    public void AResultUnderTheCapIsUnchanged()
    {
        JsonObject result = Result(Scene("A", "green", 10), Scene("B", "red", 5));
        string before = Text(result);

        JsonObject capped = ScratchView.CappedResult(result, result, _place);

        Assert.Same(result, capped);
        Assert.Equal(before, Text(capped));
        Assert.False(Directory.Exists(_logFolder));
    }

    [Fact]
    public void ARedOnlyResultSpillsTheWholeResultWithItsGreenSteps()
    {
        JsonObject whole = Result(RedScene("A", 25), Scene("B", "green", 5));
        JsonObject shown = ScratchView.Result(whole, redOnly: true);
        int length = Text(shown).Length;

        JsonObject capped = ScratchView.CappedResult(shown, whole, _place);

        string path = capped["full"]!.GetValue<string>();
        JsonNode spilled = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.True(JsonNode.DeepEquals(whole, spilled), "the spill file holds the green steps too");
        Assert.Equal(5, spilled["scenes"]![1]!["details"]!.AsArray().Count);
        Assert.Equal(Cut(length, 1, path), capped["cut"]!.GetValue<string>());
    }

    [Fact]
    public void ASpillThatCannotBeWrittenReturnsTheShrunkResultWithoutFull()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logFolder)!);
        File.WriteAllText(_logFolder, "a file where the log folder goes");
        JsonObject result = Result(Scene("A", "green", 25));
        int length = Text(result).Length;

        JsonObject capped = ScratchView.CappedResult(result, result, _place);

        Assert.False(capped.ContainsKey("full"), Text(capped));
        Assert.Equal(25, capped["scenes"]![0]!["detailsOmitted"]!.GetValue<int>());
        string prefix =
            $"the result ran to {length} characters, past 20000; 1 scenes' steps are left out here; the whole result could not be "
            + $"written to {Path.Combine(_logFolder, "scratch-result-20261027-120000-")}";
        string cut = capped["cut"]!.GetValue<string>();
        Assert.StartsWith(prefix, cut, StringComparison.Ordinal);
        Assert.Matches(@"^[0-9a-f]{4}\.json: \S", cut[prefix.Length..]);
    }

    [Fact]
    public void AnOldSpillFileThatCannotBeDeletedIsLeftAndTheCallSucceeds()
    {
        Directory.CreateDirectory(_logFolder);
        string[] old = [.. Enumerable.Range(0, 20).Select(second => Path.Combine(_logFolder, $"scratch-result-20260101-0000{second:00}-abcd.json"))];
        foreach (string file in old)
        {
            File.WriteAllText(file, "{}");
        }

        JsonObject capped;
        using (new FileStream(old[0], FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            capped = ScratchView.CappedResult(Result(Scene("A", "green", 25)), Result(Scene("A", "green", 25)), _place);
        }

        Assert.True(File.Exists(capped["full"]!.GetValue<string>()));
        Assert.True(File.Exists(old[0]));
        Assert.Equal(21, Directory.GetFiles(_logFolder, "scratch-result-*.json").Length);
    }

    [Fact]
    public void AStatusPastTheCapNamesItsStatusFileAndWritesNothing()
    {
        string statusFile = Path.Combine(_logFolder, $"scratch-{RunId}.json");
        JsonObject status = new()
        {
            ["state"] = "done",
            ["result"] = Result(Scene("A", "red", 12), Scene("B", "green", 12)),
            ["live"] = false,
        };
        Directory.CreateDirectory(_logFolder);
        File.WriteAllText(statusFile, Text(status));
        JsonObject running = new() { ["state"] = "running", ["scenes"] = new JsonArray([.. Enumerable.Range(0, 30).Select(Line)]) };
        string before = Text(status);

        JsonObject capped = ScratchView.CappedStatus(status, status, RunId, _place);

        Assert.Equal(before, Text(status));
        Assert.Equal([statusFile], Directory.GetFiles(_logFolder));
        Assert.Same(running, ScratchView.CappedStatus(running, running, RunId, _place));
        JsonArray scenes = capped["result"]!["scenes"]!.AsArray();
        Assert.Equal(12, scenes[0]!["details"]!.AsArray().Count);
        Assert.Equal(12, scenes[1]!["detailsOmitted"]!.GetValue<int>());
        Assert.Equal(statusFile, capped["full"]!.GetValue<string>());
        Assert.Equal(Cut(before.Length, 1, statusFile), capped["cut"]!.GetValue<string>());
    }

    [Fact]
    public void AStatusWhoseFileLacksTheResultSpillsTheResultAndNamesThatFile()
    {
        string statusFile = Path.Combine(_logFolder, $"scratch-{RunId}.json");
        Directory.CreateDirectory(_logFolder);
        File.WriteAllText(statusFile, """{"runId":"20261019T222144-3f2a","state":"running"}""");
        JsonObject status = new()
        {
            ["state"] = "done",
            ["result"] = Result(Scene("A", "red", 12), Scene("B", "green", 12)),
            ["live"] = false,
        };

        JsonObject capped = ScratchView.CappedStatus(status, status, RunId, _place);

        string path = capped["full"]!.GetValue<string>();
        Assert.Matches(@"scratch-result-20261027-120000-[0-9a-f]{4}\.json$", path);
        Assert.True(JsonNode.DeepEquals(status["result"], JsonNode.Parse(File.ReadAllText(path))), "the spill file holds the result");
        Assert.Equal(12, capped["result"]!["scenes"]![1]!["detailsOmitted"]!.GetValue<int>());
    }

    [Fact]
    public void TheNewestTwentySpillFilesAreKept()
    {
        Directory.CreateDirectory(_logFolder);
        string[] old = [.. Enumerable.Range(0, 25).Select(second => $"scratch-result-20260101-0000{second:00}-abcd.json")];
        foreach (string name in old)
        {
            File.WriteAllText(Path.Combine(_logFolder, name), "{}");
        }

        File.WriteAllText(Path.Combine(_logFolder, $"scratch-{RunId}.json"), "{}");
        JsonObject result = Result(Scene("A", "green", 25));

        JsonObject capped = ScratchView.CappedResult(result, result, _place);

        string written = Path.GetFileName(capped["full"]!.GetValue<string>());
        List<string> kept =
        [
            .. Directory.GetFiles(_logFolder, "scratch-result-*.json").Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal),
        ];
        Assert.Equal([.. old[6..], written], kept);
        Assert.True(File.Exists(Path.Combine(_logFolder, $"scratch-{RunId}.json")));
    }

    private static string Cut(int length, int shrunk, string path) =>
        $"the result ran to {length} characters, past 20000; {shrunk} scenes' steps are left out here; the whole result is in {path}";

    private static string Text(JsonNode node) => node.ToJsonString(ToolJson.Options);

    private static JsonObject Result(params JsonObject[] scenes) => new() { ["passed"] = false, ["scenes"] = new JsonArray(scenes) };

    private static JsonObject Scene(string name, string verdict, int steps)
    {
        JsonObject scene = new() { ["scene"] = name, ["verdict"] = verdict };
        if (steps > 0)
        {
            scene["details"] = new JsonArray([.. Enumerable.Range(0, steps).Select(Line)]);
        }

        return scene;
    }

    private static JsonObject RedScene(string name, int steps) =>
        new()
        {
            ["scene"] = name,
            ["verdict"] = "red",
            ["details"] = new JsonArray([
                .. Enumerable
                    .Range(0, steps)
                    .Select(index =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["index"] = index,
                                ["ok"] = false,
                                ["status"] = new string('r', 1000),
                            }
                    ),
            ]),
        };

    private static JsonNode Line(int index) => JsonValue.Create($"{index} ok step{index} 500ms | [scratch] " + new string('n', 1000))!;
}
