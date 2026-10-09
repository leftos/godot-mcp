using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// capture_frames' compact result: the distinct files once, each point indexing the file its path named, and shared, the
/// points that took a file an earlier point took. No Godot runs here.
/// </summary>
public sealed class CaptureFramesResultTests
{
    // One capture writes all its frames into the folder save_screenshot uses, and points due in one frame share one file.
    private static readonly string Folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "godot-mcp-capture-frames"));
    private static readonly string First = Path.Combine(Folder, "capture-0001.png");
    private static readonly string Second = Path.Combine(Folder, "capture-0002.png");
    private static readonly string Third = Path.Combine(Folder, "capture-0003.png");

    [Fact]
    public void TheFilesAreTheDistinctOnesInFirstTakenOrder()
    {
        JsonObject result = RuntimeTools.CompactFrames(Reply(Frame(First, 0.1), Frame(Second, 0.2), Frame(First, 0.3)));

        Assert.Equal(["capture-0001.png", "capture-0002.png"], Names(result));
    }

    [Fact]
    public void EachPointIndexesTheFileItsPathNamed()
    {
        JsonObject result = RuntimeTools.CompactFrames(Reply(Frame(First, 0.1), Frame(Second, 0.2), Frame(First, 0.3), Frame(Third, 0.4)));

        Assert.Equal([0, 1, 0, 2], result["points"]!.AsArray().Select(point => point!["file"]!.GetValue<int>()));
    }

    [Fact]
    public void APointKeepsItsAtFrameGameSecondsAndLate()
    {
        JsonObject frame = Frame(First, 0.1);
        frame["frame"] = 42;
        frame["gameSeconds"] = 0.104;
        frame["late"] = 0.004;

        JsonObject result = RuntimeTools.CompactFrames(Reply(frame));

        JsonNode point = Assert.Single(result["points"]!.AsArray())!;
        Assert.Equal(0.1, point["at"]!.GetValue<double>());
        Assert.Equal(42, point["frame"]!.GetValue<int>());
        Assert.Equal(0.104, point["gameSeconds"]!.GetValue<double>());
        Assert.Equal(0.004, point["late"]!.GetValue<double>());
    }

    [Fact]
    public void SharedCountsThePointsThatTookAFileAnEarlierPointTook()
    {
        Assert.Equal(0, Shared(Frame(First, 0.1), Frame(Second, 0.2)));
        Assert.Equal(1, Shared(Frame(First, 0.1), Frame(First, 0.2), Frame(Second, 0.3)));
        Assert.Equal(2, Shared(Frame(First, 0.1), Frame(First, 0.2), Frame(First, 0.3)));
    }

    [Fact]
    public void TheFolderIsTheFramesDirectoryAndTheNamesCarryNone()
    {
        JsonObject result = RuntimeTools.CompactFrames(Reply(Frame(First, 0.1), Frame(Second, 0.2)));

        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(First)), result["folder"]!.GetValue<string>());
        Assert.All(
            Names(result),
            name =>
            {
                Assert.DoesNotContain("/", name);
                Assert.DoesNotContain("\\", name);
            }
        );
    }

    [Fact]
    public void TheSizeOfTheFramesComesBackOnce()
    {
        JsonObject first = Frame(First, 0.1);
        first["width"] = 640;
        first["height"] = 360;
        JsonObject second = Frame(Second, 0.2);
        second["width"] = 640;
        second["height"] = 360;

        JsonObject result = RuntimeTools.CompactFrames(Reply(first, second));

        Assert.Equal(640, result["width"]!.GetValue<int>());
        Assert.Equal(360, result["height"]!.GetValue<int>());
    }

    [Fact]
    public void StoppedMissedAndCallPassThrough()
    {
        JsonObject reply = Reply(Frame(First, 0.1));
        reply["stopped"] = true;
        reply["missed"] = new JsonArray(5.0);
        reply["call"] = new JsonObject { ["value"] = 7 };

        JsonObject result = RuntimeTools.CompactFrames(reply);

        Assert.True(result["stopped"]!.GetValue<bool>());
        Assert.Equal([5.0], result["missed"]!.AsArray().Select(point => point!.GetValue<double>()));
        Assert.Equal(7, result["call"]!["value"]!.GetValue<int>());
    }

    [Fact]
    public void ACaptureStoppedBeforeItsFirstPointHasNoFilesPointsFolderOrSize()
    {
        JsonObject reply = Reply();
        reply["stopped"] = true;
        reply["missed"] = new JsonArray(1.0);

        JsonObject result = RuntimeTools.CompactFrames(reply);

        Assert.Empty(result["files"]!.AsArray());
        Assert.Empty(result["points"]!.AsArray());
        Assert.Equal(0, result["shared"]!.GetValue<int>());
        Assert.True(result["stopped"]!.GetValue<bool>());
        Assert.False(result.ContainsKey("folder"));
        Assert.False(result.ContainsKey("width"));
        Assert.False(result.ContainsKey("height"));
    }

    [Fact]
    public void AMetStartPassesThroughWithItsFrameAndThen()
    {
        JsonObject reply = Reply(Frame(First, 0.1));
        reply["start"] = new JsonObject
        {
            ["met"] = true,
            ["frame"] = 42,
            ["then"] = new JsonObject { ["frame"] = 42, ["timeScale"] = 0.2 },
        };

        JsonObject result = RuntimeTools.CompactFrames(reply);

        Assert.True(result["start"]!["met"]!.GetValue<bool>());
        Assert.Equal(42, result["start"]!["frame"]!.GetValue<int>());
        Assert.Equal(0.2, result["start"]!["then"]!["timeScale"]!.GetValue<double>());
    }

    [Fact]
    public void AStartThatTimedOutPassesThroughWithItsLastValueAndNoFrames()
    {
        JsonObject reply = Reply();
        reply["stopped"] = true;
        reply["missed"] = new JsonArray(0.05, 0.15);
        reply["start"] = new JsonObject { ["met"] = false, ["last"] = "idle" };

        JsonObject result = RuntimeTools.CompactFrames(reply);

        Assert.Empty(result["points"]!.AsArray());
        Assert.Empty(result["files"]!.AsArray());
        Assert.True(result["stopped"]!.GetValue<bool>());
        Assert.False(result["start"]!["met"]!.GetValue<bool>());
        Assert.Equal("idle", result["start"]!["last"]!.GetValue<string>());
    }

    private static IEnumerable<string> Names(JsonObject result) => result["files"]!.AsArray().Select(name => name!.GetValue<string>());

    private static int Shared(params JsonObject[] frames) => RuntimeTools.CompactFrames(Reply(frames))["shared"]!.GetValue<int>();

    private static JsonObject Reply(params JsonObject[] frames) => new() { ["frames"] = new JsonArray([.. frames]) };

    /// <summary>One of the bridge's frame entries, as bridge/godot_mcp_time.gd's frame_entries writes it.</summary>
    private static JsonObject Frame(string path, double at) =>
        new()
        {
            ["at"] = at,
            ["frame"] = 1,
            ["gameSeconds"] = at,
            ["late"] = 0.0,
            ["path"] = path,
            ["width"] = 320,
            ["height"] = 180,
        };
}
