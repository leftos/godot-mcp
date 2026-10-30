using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Tests.Tools;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Session;

public sealed class RecordingMarksTests
{
    private const string Movie = @"C:\Games\Probe\.godot\godot-mcp\recordings\20260926-120000-000-Probe.avi";

    /// <summary>How record_mark serialises its results: the web defaults, camelCase names.</summary>
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMovieStartWhosePathIsRefusedNeverAsksForTheFrame(bool takenByAnEarlierClip)
    {
        await using RegistryHarness harness = new();
        string project = harness.Project("game");
        using FakeBridge game = await harness.AttachFakeGameAsync(project, "game", new FakeHello());
        GodotSession session = harness.Sessions.Resolve("game");
        Recording recording = new(Path.Combine(project, ".godot", "godot-mcp", "recordings", "movie.avi"));
        using TempDirectory outside = new();
        string path;
        string expected;
        if (takenByAnEarlierClip)
        {
            // The path passes the output rule, but an earlier clip of the same recording goes there.
            path = "clips/a.mp4";
            recording.Mark(
                Recording.StartMark,
                10,
                OutputDestination.Resolve(OutputPlaces.Find(project, NullLogger.Instance), path, ".mp4", false),
                false
            );
            recording.Mark(Recording.StopMark, 20);
            expected = $"path {path} is already where clip 1 of this recording goes; name another file";
        }
        else
        {
            path = Path.Combine(outside.Path, "x.mp4");
            expected = $"path {path} resolves outside the project {project}; save inside the project";
        }

        int clipsBefore = recording.Clips().Count;
        bool asked = false;

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            RuntimeTools.MarkMovieAsync(
                session,
                recording,
                Recording.StartMark,
                new RecordMarkOptions(Path: path),
                _ =>
                {
                    asked = true;
                    return Task.FromResult(new RuntimeTools.BridgeResult(JsonNode.Parse("""{"frame": 30}"""), []));
                },
                _ => true,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(expected, refused.Message);
        Assert.False(asked, "the game was asked for the movie frame before the path was refused");
        Assert.Equal(clipsBefore, recording.Clips().Count);
    }

    [Theory]
    [InlineData("""{"frame": 100, "open": [[7, 90], [8, 95]]}""", true)]
    [InlineData("""{"frame": 100, "open": [[7, 90], [9, 80]]}""", true)]
    [InlineData("""{"frame": 100, "open": [[9, 80]]}""", false)]
    [InlineData("""{"frame": 100, "open": [[7, 120], [7, 1.5], [7, -1], [7], [7, 90, 1], [1.5, 90], [-1, 90], ["x", 90], 90, "x"]}""", false)]
    [InlineData("""{"frame": 100, "open": "x"}""", false)]
    [InlineData("""{"frame": 100}""", false)]
    public async Task AMarkClosesTheOpenRequestsStillPendingAtItsFrame(string movieFrameReply, bool closes)
    {
        // Request 9 is no longer pending on the server (it timed out), so the bridge's stale entry for it is skipped.
        await using RegistryHarness harness = new();
        string project = harness.Project("game");
        using FakeBridge game = await harness.AttachFakeGameAsync(project, "game", new FakeHello());
        GodotSession session = harness.Sessions.Resolve("game");
        Recording recording = new(Path.Combine(project, ".godot", "godot-mcp", "recordings", "movie.avi"));

        await RuntimeTools.MarkMovieAsync(
            session,
            recording,
            Recording.StartMark,
            null,
            _ => Task.FromResult(new RuntimeTools.BridgeResult(JsonNode.Parse(movieFrameReply), [])),
            id => id != 9,
            TestContext.Current.CancellationToken
        );

        FrameRange[] expected = closes ? [new FrameRange(90, 100)] : [];
        Assert.Equal(expected, recording.CallRanges(new ClipSpan(0, null)));
        Assert.Equal([new ClipSpan(100, null)], recording.Clips());
    }

    [Theory]
    [InlineData("start", "A clip is already open, started at frame 10; mark stop before the next start.")]
    [InlineData("stop", "No clip is open; mark start first.")]
    public async Task AThenRecordTheRecordingWouldRefuseIsRefusedBeforeTheWait(string mark, string expected)
    {
        await using RegistryHarness harness = new();
        string project = harness.Project("game");
        using FakeBridge game = await harness.AttachFakeGameAsync(project, "game", new FakeHello());
        Recording recording = new(Path.Combine(project, ".godot", "godot-mcp", "recordings", "movie.avi"));
        if (mark == Recording.StartMark)
        {
            recording.Mark(Recording.StartMark, 10);
        }

        int clipsBefore = recording.Clips().Count;

        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.CheckThenRecord(harness.Sessions.Resolve("game"), recording, new ThenRecord(mark))
        );

        Assert.Equal(expected, refused.Message);
        Assert.Equal(clipsBefore, recording.Clips().Count);
    }

    [Fact]
    public async Task AThenRecordPathAnEarlierClipTookIsRefusedBeforeTheWait()
    {
        await using RegistryHarness harness = new();
        string project = harness.Project("game");
        using FakeBridge game = await harness.AttachFakeGameAsync(project, "game", new FakeHello());
        Recording recording = new(Path.Combine(project, ".godot", "godot-mcp", "recordings", "movie.avi"));
        recording.Mark(
            Recording.StartMark,
            10,
            OutputDestination.Resolve(OutputPlaces.Find(project, NullLogger.Instance), "clips/a.mp4", ".mp4", false),
            false
        );
        recording.Mark(Recording.StopMark, 20);

        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.CheckThenRecord(harness.Sessions.Resolve("game"), recording, new ThenRecord("start", "clips/a.mp4"))
        );

        Assert.Equal("path clips/a.mp4 is already where clip 1 of this recording goes; name another file", refused.Message);
    }

    [Fact]
    public async Task AMetThenRecordMarksTheMovieFrameTheBridgeReadInTheMetFrame()
    {
        await using RegistryHarness harness = new();
        string project = harness.Project("game");
        using FakeBridge game = await harness.AttachFakeGameAsync(project, "game", new FakeHello());
        Recording recording = new(Path.Combine(project, ".godot", "godot-mcp", "recordings", "movie.avi"));
        RuntimeTools.ThenMark prepared = RuntimeTools.CheckThenRecord(
            harness.Sessions.Resolve("game"),
            recording,
            new ThenRecord("start", "clips/a.mp4")
        );
        JsonObject reply = JsonNode.Parse("""{"met": true, "frames": 3, "then": {"frame": 42, "movieFrame": 42}}""")!.AsObject();

        RuntimeTools.PlaceThenMark(reply, prepared);

        string destination = OutputDestination.Resolve(OutputPlaces.Find(project, NullLogger.Instance), "clips/a.mp4", ".mp4", false).FullPath;
        JsonObject expected = new()
        {
            ["frame"] = 42,
            ["record"] = new JsonObject
            {
                ["mark"] = "start",
                ["frame"] = 42,
                ["seconds"] = 0.7,
                ["mode"] = "movie",
                ["destination"] = destination,
            },
        };
        Assert.True(JsonNode.DeepEquals(expected, reply["then"]), reply.ToJsonString());
        ClipSpan open = Assert.Single(recording.Clips());
        Assert.Equal(42, open.StartFrame);
        Assert.Null(open.StopFrame);
        Assert.Equal(destination, open.Destination?.FullPath);
    }

    [Fact]
    public async Task AThenStopOnItsStartsFrameReportsTheErrorAndKeepsTheMetWait()
    {
        await using RegistryHarness harness = new();
        string project = harness.Project("game");
        using FakeBridge game = await harness.AttachFakeGameAsync(project, "game", new FakeHello());
        Recording recording = new(Path.Combine(project, ".godot", "godot-mcp", "recordings", "movie.avi"));
        recording.Mark(Recording.StartMark, 42);
        RuntimeTools.ThenMark prepared = RuntimeTools.CheckThenRecord(harness.Sessions.Resolve("game"), recording, new ThenRecord("stop"));
        JsonObject reply = JsonNode.Parse("""{"met": true, "frames": 0, "then": {"frame": 42, "movieFrame": 42}}""")!.AsObject();

        RuntimeTools.PlaceThenMark(reply, prepared);

        Assert.True(reply["met"]!.GetValue<bool>());
        JsonObject expected = new()
        {
            ["mark"] = "stop",
            ["error"] = "A clip needs at least one frame: stop is at frame 42 and its start at frame 42.",
        };
        Assert.True(JsonNode.DeepEquals(expected, reply["then"]!["record"]), reply.ToJsonString());
        Assert.Null(Assert.Single(recording.Clips()).StopFrame);
    }

    [Fact]
    public async Task AMetThenWithoutAMovieFrameReportsTheErrorAndMarksNothing()
    {
        await using RegistryHarness harness = new();
        string project = harness.Project("game");
        using FakeBridge game = await harness.AttachFakeGameAsync(project, "game", new FakeHello());
        Recording recording = new(Path.Combine(project, ".godot", "godot-mcp", "recordings", "movie.avi"));
        RuntimeTools.ThenMark prepared = RuntimeTools.CheckThenRecord(harness.Sessions.Resolve("game"), recording, new ThenRecord("start"));
        JsonObject reply = JsonNode.Parse("""{"met": true, "frames": 1, "then": {"frame": 42}}""")!.AsObject();

        RuntimeTools.PlaceThenMark(reply, prepared);

        JsonObject expected = new() { ["mark"] = "start", ["error"] = "The bridge's met then has no movieFrame: null." };
        Assert.True(JsonNode.DeepEquals(expected, reply["then"]!["record"]), reply.ToJsonString());
        Assert.Empty(recording.Clips());
    }

    [Fact]
    public async Task AMovieFrameSentAsAWholeFloatPlacesTheMarkAtThatFrame()
    {
        await using RegistryHarness harness = new();
        string project = harness.Project("game");
        using FakeBridge game = await harness.AttachFakeGameAsync(project, "game", new FakeHello());
        Recording recording = new(Path.Combine(project, ".godot", "godot-mcp", "recordings", "movie.avi"));
        RuntimeTools.ThenMark prepared = RuntimeTools.CheckThenRecord(harness.Sessions.Resolve("game"), recording, new ThenRecord("start"));
        JsonObject reply = JsonNode.Parse("""{"met": true, "frames": 1, "then": {"frame": 42, "movieFrame": 42.0}}""")!.AsObject();

        RuntimeTools.PlaceThenMark(reply, prepared);

        Assert.Equal(42, reply["then"]!["record"]!["frame"]!.GetValue<long>());
        Assert.Equal(42, Assert.Single(recording.Clips()).StartFrame);
    }

    [Fact]
    public async Task AnUnmetWaitPlacesNoMark()
    {
        await using RegistryHarness harness = new();
        string project = harness.Project("game");
        using FakeBridge game = await harness.AttachFakeGameAsync(project, "game", new FakeHello());
        Recording recording = new(Path.Combine(project, ".godot", "godot-mcp", "recordings", "movie.avi"));
        RuntimeTools.ThenMark prepared = RuntimeTools.CheckThenRecord(harness.Sessions.Resolve("game"), recording, new ThenRecord("start"));
        JsonObject reply = JsonNode.Parse("""{"met": false, "frames": 60, "last": false}""")!.AsObject();

        RuntimeTools.PlaceThenMark(reply, prepared);

        Assert.Equal("""{"met":false,"frames":60,"last":false}""", reply.ToJsonString());
        Assert.Empty(recording.Clips());
    }

    [Fact]
    public void MarksMustAlternate()
    {
        Recording recording = new(Movie);

        SessionException stopFirst = Assert.Throws<SessionException>(() => recording.Mark("stop", 5));
        MarkResult started = recording.Mark("start", 30);
        SessionException secondStart = Assert.Throws<SessionException>(() => recording.Mark("start", 40));
        SessionException emptyClip = Assert.Throws<SessionException>(() => recording.Mark("stop", 30));
        MarkResult stopped = recording.Mark("stop", 90);
        SessionException secondStop = Assert.Throws<SessionException>(() => recording.Mark("stop", 100));

        Assert.Equal("No clip is open; mark start first.", stopFirst.Message);
        Assert.Equal(new MarkResult("start", 30, 0.5), started);
        Assert.Equal("A clip is already open, started at frame 30; mark stop before the next start.", secondStart.Message);
        Assert.Equal("A clip needs at least one frame: stop is at frame 30 and its start at frame 30.", emptyClip.Message);
        Assert.Equal(new MarkResult("stop", 90, 1.5), stopped);
        Assert.Equal("No clip is open; mark start first.", secondStop.Message);
        Assert.Equal([new ClipSpan(30, 90)], recording.Clips());
    }

    [Fact]
    public void AMovieMarkSerialisesItsModeAsMovie()
    {
        Recording recording = new(Movie);

        MarkResult started = recording.Mark("start", 30);
        string json = JsonSerializer.Serialize(started, WebJson);

        Assert.Equal("""{"mark":"start","frame":30,"seconds":0.5,"mode":"movie"}""", json);
        Assert.Equal(new MarkResult("start", 30, 0.5), started);
    }

    [Fact]
    public void AnOpenStartClipsToTheEnd()
    {
        Recording recording = new(Movie);
        recording.Mark("start", 0);
        recording.Mark("stop", 60);
        recording.Mark("start", 120);

        IReadOnlyList<ClipSpan> clips = recording.Clips();
        List<string> encode = RecordingCut.EncodeArguments(Movie, clips[1], RecordingCut.ClipPath(Movie, 2), dropIdle: null);
        List<string> copy = RecordingCut.CopyArguments(Movie, clips[1], RecordingCut.FallbackClipPath(Movie, 2));

        Assert.Equal([new ClipSpan(0, 60), new ClipSpan(120, null)], clips);
        Assert.DoesNotContain("-to", encode);
        Assert.DoesNotContain("-to", copy);
        Assert.Equal(@"C:\Games\Probe\.godot\godot-mcp\recordings\20260926-120000-000-Probe-clip2.mp4", encode[^1]);
        Assert.Equal(@"C:\Games\Probe\.godot\godot-mcp\recordings\20260926-120000-000-Probe-clip2.avi", copy[^1]);
    }

    [Fact]
    public void AKilledOrMissingMovieIsNeverAGoodPath()
    {
        string notWritten = $"The recording {Movie} was not written (the game may have failed before its first frame), so there is nothing to cut.";

        Assert.Equal(
            new RecordingResult
            {
                Mode = Recording.MovieMode,
                Path = Movie,
                Error = RecordingCut.KilledMovie,
            },
            RecordingCut.Unusable(Movie, exists: true, killed: true)
        );
        Assert.Equal(
            new RecordingResult { Mode = Recording.MovieMode, Error = notWritten },
            RecordingCut.Unusable(Movie, exists: false, killed: true)
        );
        Assert.Equal(
            new RecordingResult { Mode = Recording.MovieMode, Error = notWritten },
            RecordingCut.Unusable(Movie, exists: false, killed: false)
        );
        Assert.Null(RecordingCut.Unusable(Movie, exists: true, killed: false));
    }

    [Fact]
    public async Task AMissingMovieWithoutMarksHasNoPath()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"godot-mcp-missing-{Guid.NewGuid():N}.avi");

        RecordingResult result = await RecordingCut.FinishAsync(new Recording(missing), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Null(result.Path);
        Assert.Null(result.Clips);
        Assert.StartsWith($"The recording {missing} was not written", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodeArgumentsSeekTheInputBetweenTheMarks()
    {
        string clip = RecordingCut.ClipPath(Movie, 1);

        List<string> fromTheFirstFrame = RecordingCut.EncodeArguments(Movie, new ClipSpan(0, 30), clip, dropIdle: null);
        List<string> between = RecordingCut.EncodeArguments(Movie, new ClipSpan(90, 150), clip, dropIdle: null);

        // Each boundary sits a quarter frame before its frame: frame 90 is at 1.5 s, frame 150 at 2.5 s.
        Assert.Equal(@"C:\Games\Probe\.godot\godot-mcp\recordings\20260926-120000-000-Probe-clip1.mp4", clip);
        Assert.Equal([.. Head("0", "0.495833"), .. Encode(EvenSize), "-c:a", "aac", "-movflags", "+faststart", clip], fromTheFirstFrame);
        Assert.Equal([.. Head("1.495833", "2.495833"), .. Encode(EvenSize), "-c:a", "aac", "-movflags", "+faststart", clip], between);
    }

    [Fact]
    public void DropIdleReadsItsFiltersFromScriptsAndKeepsTheAudio()
    {
        string clip = RecordingCut.ClipPath(Movie, 1);
        RecordingCut.DropIdleScripts scripts = RecordingCut.DropIdleScriptsFor(Movie, 1);

        List<string> arguments = RecordingCut.EncodeArguments(Movie, new ClipSpan(90, 150), clip, scripts);

        Assert.Equal(
            new RecordingCut.DropIdleScripts(
                @"C:\Games\Probe\.godot\godot-mcp\recordings\20260926-120000-000-Probe-clip1.vf.txt",
                @"C:\Games\Probe\.godot\godot-mcp\recordings\20260926-120000-000-Probe-clip1.af.txt"
            ),
            scripts
        );
        Assert.Equal(
            [
                .. Head("1.495833", "2.495833"),
                "-/vf",
                scripts.Video,
                "-/af",
                scripts.Audio,
                .. Encode(EvenSize)[2..],
                "-c:a",
                "aac",
                "-movflags",
                "+faststart",
                clip,
            ],
            arguments
        );
    }

    [Fact]
    public void DropIdleFiltersKeepTheCallRangesCountedFromTheClipsStart()
    {
        (string video, string audio) = RecordingCut.DropIdleFilters([new FrameRange(90, 90), new FrameRange(130, 149)], 90);

        // The seek is a quarter frame before frame 90, so each audio bound sits midway between two chunks' timestamps.
        Assert.Equal(@"select=between(n\,0\,0)+between(n\,40\,59),setpts=N/60/TB,scale=trunc(iw/2)*2:trunc(ih/2)*2", video);
        Assert.Equal(@"aselect=between(t\,0.002083\,0.0125)+between(t\,0.6625\,0.995833),asetpts=N/SR/TB", audio);
    }

    [Fact]
    public void DropIdleFiltersFromTheMoviesFirstFrameStartTheAudioAtZero()
    {
        (string video, string audio) = RecordingCut.DropIdleFilters([new FrameRange(0, 0), new FrameRange(40, 59)], 0);

        Assert.Equal(@"select=between(n\,0\,0)+between(n\,40\,59),setpts=N/60/TB,scale=trunc(iw/2)*2:trunc(ih/2)*2", video);
        Assert.Equal(@"aselect=between(t\,0\,0.008333)+between(t\,0.658333\,0.991667),asetpts=N/SR/TB", audio);
    }

    [Fact]
    public void DropIdleFiltersWithoutCallsKeepTheStartFrame()
    {
        (string video, string audio) = RecordingCut.DropIdleFilters([], 90);

        Assert.Equal(@"select=between(n\,0\,0),setpts=N/60/TB,scale=trunc(iw/2)*2:trunc(ih/2)*2", video);
        Assert.Equal(@"aselect=between(t\,0.002083\,0.0125),asetpts=N/SR/TB", audio);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public async Task DropIdleScriptsLastOnlyAsLongAsTheEncode(int encodeExitCode)
    {
        using TempDirectory directory = new();
        string movie = Path.Combine(directory.Path, "20260926-120000-000-Probe.avi");
        await File.WriteAllTextAsync(movie, "movie", TestContext.Current.CancellationToken);
        Recording recording = new(movie) { DropIdle = true };
        recording.Mark("start", 90);
        recording.AddCall(90, 90);
        recording.AddCall(130, 149);
        recording.Mark("stop", 150);
        RecordingCut.DropIdleScripts scripts = RecordingCut.DropIdleScriptsFor(movie, 1);
        List<(IReadOnlyList<string> Arguments, string? Video, string? Audio)> runs = [];

        RecordingResult result = await RecordingCut.CutClipsAsync(
            recording,
            "ffmpeg.exe",
            (request, _) =>
            {
                runs.Add((request.Arguments, ReadScript(scripts.Video), ReadScript(scripts.Audio)));
                int exitCode = runs.Count == 1 ? encodeExitCode : 0;
                return Task.FromResult(new ToolProcessResult(exitCode, TimeSpan.Zero, KillReason.None));
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(encodeExitCode == 0 ? 1 : 2, runs.Count);
        Assert.Contains("-/vf", runs[0].Arguments);
        Assert.Equal(@"select=between(n\,0\,0)+between(n\,40\,59),setpts=N/60/TB,scale=trunc(iw/2)*2:trunc(ih/2)*2", runs[0].Video);
        Assert.Equal(@"aselect=between(t\,0.002083\,0.0125)+between(t\,0.6625\,0.995833),asetpts=N/SR/TB", runs[0].Audio);
        if (runs.Count == 2)
        {
            Assert.Null(runs[1].Video);
            Assert.Null(runs[1].Audio);
        }

        Assert.False(File.Exists(scripts.Video), "the video filter script was left behind");
        Assert.False(File.Exists(scripts.Audio), "the audio filter script was left behind");
        string expectedClip = encodeExitCode == 0 ? RecordingCut.ClipPath(movie, 1) : RecordingCut.FallbackClipPath(movie, 1);
        Assert.Equal([expectedClip], result.Clips!);
    }

    [Fact]
    public void CopyArgumentsCopyStreamsBetweenTheMarks()
    {
        string clip = RecordingCut.FallbackClipPath(Movie, 1);

        List<string> fromTheFirstFrame = RecordingCut.CopyArguments(Movie, new ClipSpan(0, 30), clip);
        List<string> between = RecordingCut.CopyArguments(Movie, new ClipSpan(90, 150), clip);

        Assert.Equal(@"C:\Games\Probe\.godot\godot-mcp\recordings\20260926-120000-000-Probe-clip1.avi", clip);
        Assert.Equal(["-hide_banner", "-nostdin", "-y", "-i", Movie, "-ss", "0", "-to", "0.495833", "-c", "copy", clip], fromTheFirstFrame);
        Assert.Equal(["-hide_banner", "-nostdin", "-y", "-i", Movie, "-ss", "1.495833", "-to", "2.495833", "-c", "copy", clip], between);
    }

    [Fact]
    public void TheCeilingScalesWithTheClip()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), RecordingCut.CeilingFor(new ClipSpan(0, 60)));
        Assert.Equal(TimeSpan.FromSeconds(120), RecordingCut.CeilingFor(new ClipSpan(600, 600 + (40 * 60))));
        Assert.Equal(TimeSpan.FromSeconds(300), RecordingCut.CeilingFor(new ClipSpan(600, 600 + (100 * 60))));
        Assert.Equal(TimeSpan.FromSeconds(1800), RecordingCut.CeilingFor(new ClipSpan(0, null)));
        Assert.Equal(TimeSpan.FromSeconds(1500), RecordingCut.CeilingFor(new ClipSpan(6000, null)));
    }

    [Fact]
    public void DropIdleWithoutRecordIsRefused()
    {
        LaunchRequest request = ProjectProfile.Empty(@"C:\Games\Probe").Merge(null, [], [], new RunOptions(DropIdle: true)).Request;

        SessionException refused = Assert.Throws<SessionException>(() => GodotCommandLine.RefuseUnrecordable(request));
        GodotCommandLine.RefuseUnrecordable(request with { Record = true });

        Assert.Equal(
            "options.dropIdle drops the idle frames of a recording's clips, and this run does not record. Add options.record: true, or drop "
                + "options.dropIdle.",
            refused.Message
        );
    }

    [Fact]
    public async Task AFailedEncodeFallsBackToACopy()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"godot-mcp-cut-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string movie = Path.Combine(directory, "20260926-120000-000-Probe.avi");
        await File.WriteAllTextAsync(movie, "movie", TestContext.Current.CancellationToken);
        Recording recording = new(movie) { DropIdle = true };
        recording.Mark("start", 0);
        recording.Mark("stop", 60);
        recording.Mark("start", 120);
        recording.Mark("stop", 180);
        List<ToolProcessRequest> requests = [];
        string log = Path.Combine(directory, RecordingCut.LogFileName);

        try
        {
            // The first clip's encode fails as an ffmpeg without libx264 does; every other run succeeds.
            RecordingResult result = await RecordingCut.CutClipsAsync(
                recording,
                "ffmpeg.exe",
                (request, _) =>
                {
                    requests.Add(request);
                    int exitCode = requests.Count == 1 ? 8 : 0;
                    return Task.FromResult(new ToolProcessResult(exitCode, TimeSpan.Zero, KillReason.None));
                },
                TestContext.Current.CancellationToken
            );

            string copied = RecordingCut.FallbackClipPath(movie, 1);
            Assert.Null(result.Path);
            Assert.Equal([copied, RecordingCut.ClipPath(movie, 2)], result.Clips!);
            Assert.Equal(
                $"ffmpeg failed (exit code 8) encoding {RecordingCut.ClipPath(movie, 1)}. See {log}. Clip 1 was copied to {copied}, "
                    + "its idle frames kept, as recorded instead.",
                result.Error
            );
            Assert.Equal(["libx264", "copy", "libx264"], requests.Select(request => request.Arguments.Contains("libx264") ? "libx264" : "copy"));
            Assert.Equal([false, true, true], requests.Select(request => request.AppendToLog));
            Assert.False(File.Exists(movie), "the full movie was not deleted");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AMovieClipIsDeliveredToItsMarksPath()
    {
        using TempDirectory project = new();
        Recording recording = await MovieInAsync(project);
        OutputTarget destination = Destination(project, "clips/a.mp4", overwrite: false);
        recording.Mark("start", 0, destination, overwrite: false);
        recording.Mark("stop", 60);

        RecordingResult result = await CutMakingClipsAsync(recording, failFirst: false);

        Assert.Equal([destination.FullPath], result.Clips!);
        Assert.True(File.Exists(destination.FullPath), $"the clip was not delivered to {destination.FullPath}");
        Assert.False(File.Exists(RecordingCut.ClipPath(recording.Path, 1)), "the clip was left in recordings/ too");
        Assert.Null(result.Warning);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task AClipWhoseDestinationAppearedBeforeTheCutIsKeptWithAWarning()
    {
        using TempDirectory project = new();
        Recording recording = await MovieInAsync(project);
        OutputTarget destination = Destination(project, "clips/a.mp4", overwrite: false);
        recording.Mark("start", 0);
        recording.Mark("stop", 60);
        recording.Mark("start", 120, destination, overwrite: false);
        recording.Mark("stop", 180);
        Directory.CreateDirectory(Path.GetDirectoryName(destination.FullPath)!);
        File.WriteAllText(destination.FullPath, "taken");

        RecordingResult result = await CutMakingClipsAsync(recording, failFirst: false);

        string second = RecordingCut.ClipPath(recording.Path, 2);
        Assert.Equal([RecordingCut.ClipPath(recording.Path, 1), second], result.Clips!);
        Assert.Equal("taken", File.ReadAllText(destination.FullPath));
        Assert.True(File.Exists(second), "the second clip was not kept in recordings/");
        // The reason is the system's own message for a file already there, in the system's language.
        Assert.Matches(
            $"^the clip could not be moved to {Regex.Escape(destination.FullPath)}: [^;]+; it is kept at {Regex.Escape(second)}$",
            result.Warning
        );
    }

    [Fact]
    public async Task AnAviFallbackIsKeptWithAWarning()
    {
        using TempDirectory project = new();
        Recording recording = await MovieInAsync(project);
        OutputTarget destination = Destination(project, "clips/a.mp4", overwrite: false);
        recording.Mark("start", 0, destination, overwrite: false);
        recording.Mark("stop", 60);

        RecordingResult result = await CutMakingClipsAsync(recording, failFirst: true);

        string copied = RecordingCut.FallbackClipPath(recording.Path, 1);
        Assert.Equal([copied], result.Clips!);
        Assert.True(File.Exists(copied), "the .avi clip was not kept in recordings/");
        Assert.False(File.Exists(destination.FullPath), "an .avi was delivered to an .mp4 path");
        Assert.Equal($"the clip fell back to .avi, so it is kept at {copied} instead of {destination.FullPath}", result.Warning);
        Assert.Contains($"Clip 1 was copied to {copied} as recorded instead.", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondMarkNamingAnEarlierClipsPathIsRefused()
    {
        using TempDirectory project = new();
        Recording recording = await MovieInAsync(project);
        recording.Mark("start", 0);
        recording.Mark("stop", 60);
        recording.Mark("start", 120, Destination(project, "clips/a.mp4", overwrite: false), overwrite: false);
        recording.Mark("stop", 180);
        OutputTarget again = Destination(project, "CLIPS/A.mp4", overwrite: true);

        SessionException refused = Assert.Throws<SessionException>(() => recording.Mark("start", 240, again, overwrite: true));

        Assert.Equal("path CLIPS/A.mp4 is already where clip 2 of this recording goes; name another file", refused.Message);
        Assert.Equal(2, recording.Clips().Count);
    }

    [Fact]
    public async Task ASecondMarkNamingAnEarlierClipsFileThroughAJunctionIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        using TempDirectory project = new();
        Recording recording = await MovieInAsync(project);
        ScreenshotPathValidationTests.MakeJunction(project.Combine("j"), Directory.CreateDirectory(project.Combine("clips")).FullName);
        recording.Mark("start", 0, Destination(project, "clips/a.mp4", overwrite: false), overwrite: false);
        recording.Mark("stop", 60);
        OutputTarget again = Destination(project, "j/a.mp4", overwrite: true);

        SessionException refused = Assert.Throws<SessionException>(() => recording.Mark("start", 120, again, overwrite: true));

        Assert.Equal("path j/a.mp4 is already where clip 1 of this recording goes; name another file", refused.Message);
        Assert.Single(recording.Clips());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AClipWhosePathNowLeadsToAnEarlierDeliveredClipIsKept()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        using TempDirectory project = new();
        Recording recording = await MovieInAsync(project);
        OutputTarget first = Destination(project, "clips/a.mp4", overwrite: false);
        OutputTarget second = Destination(project, "other/a.mp4", overwrite: true);
        recording.Mark("start", 0, first, overwrite: false);
        recording.Mark("stop", 60);
        recording.Mark("start", 120, second, overwrite: true);
        recording.Mark("stop", 180);
        string clips = Directory.CreateDirectory(project.Combine("clips")).FullName;
        ScreenshotPathValidationTests.MakeJunction(project.Combine("other"), clips);

        RecordingResult result = await CutMakingClipsAsync(recording, failFirst: false);

        string kept = RecordingCut.ClipPath(recording.Path, 2);
        string final = Path.Combine(FinalPath.Of(clips), "a.mp4");
        Assert.Equal([first.FullPath, kept], result.Clips!);
        Assert.Equal(Path.GetFileName(RecordingCut.ClipPath(recording.Path, 1)), File.ReadAllText(first.FullPath));
        Assert.Equal(Path.GetFileName(kept), File.ReadAllText(kept));
        Assert.Equal(
            $"the clip could not be moved to {second.FullPath}: clip 1 of this recording was delivered to {final}; it is kept at {kept}",
            result.Warning
        );
    }

    [Fact]
    public async Task AClipWhoseCopyInRecordingsCannotBeDeletedIsReportedAtItsDestination()
    {
        using TempDirectory project = new();
        Recording recording = await MovieInAsync(project);
        string clip = RecordingCut.ClipPath(recording.Path, 1);
        File.WriteAllText(clip, "clip");
        OutputTarget destination = Destination(project, "clips/a.mp4", overwrite: false);
        (string Path, string? FinalPath, string? Warning) delivered;
        using (new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // A copy across volumes, then the source's delete, which the open handle makes fail.
            delivered = ClipDelivery.Deliver(
                clip,
                destination,
                overwrite: false,
                ReadOnlyDictionary<string, int>.Empty,
                (source, to, overwrite) =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(source, to, overwrite);
                    return OutputDestination.DeleteSource(source);
                }
            );
        }

        Assert.Equal(destination.FullPath, delivered.Path);
        Assert.Matches(
            $"^the clip was moved to {Regex.Escape(destination.FullPath)}, but its copy at {Regex.Escape(clip)} could not be deleted: [^;]+$",
            delivered.Warning
        );
        Assert.True(File.Exists(destination.FullPath));
    }

    [Fact]
    public async Task AMarkWithoutAPathStaysInRecordings()
    {
        using TempDirectory project = new();
        Recording recording = await MovieInAsync(project);
        OutputTarget destination = Destination(project, "clips/b.mp4", overwrite: false);
        recording.Mark("start", 0);
        recording.Mark("stop", 60);
        recording.Mark("start", 120, destination, overwrite: false);
        recording.Mark("stop", 180);

        RecordingResult result = await CutMakingClipsAsync(recording, failFirst: false);

        string first = RecordingCut.ClipPath(recording.Path, 1);
        Assert.Equal([first, destination.FullPath], result.Clips!);
        Assert.True(File.Exists(first), "the clip without a path left recordings/");
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task AFailedCopyAfterAFailedEncodeKeepsTheFullMovie()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"godot-mcp-cut-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string movie = Path.Combine(directory, "20260926-120000-000-Probe.avi");
        await File.WriteAllTextAsync(movie, "movie", TestContext.Current.CancellationToken);
        Recording recording = new(movie);
        recording.Mark("start", 0);
        recording.Mark("stop", 60);
        string log = Path.Combine(directory, RecordingCut.LogFileName);

        try
        {
            RecordingResult result = await RecordingCut.CutClipsAsync(
                recording,
                "ffmpeg.exe",
                (_, _) => Task.FromResult(new ToolProcessResult(1, TimeSpan.Zero, KillReason.None)),
                TestContext.Current.CancellationToken
            );

            Assert.Equal(
                new RecordingResult
                {
                    Mode = Recording.MovieMode,
                    Path = movie,
                    Error =
                        $"ffmpeg failed (exit code 1) encoding {RecordingCut.ClipPath(movie, 1)}. See {log}. ffmpeg failed (exit code 1) "
                        + $"cutting {RecordingCut.FallbackClipPath(movie, 1)}; the full file is kept. See {log}.",
                },
                result
            );
            Assert.True(File.Exists(movie));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private const string EvenSize = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

    private static string[] Head(string start, string stop) => ["-hide_banner", "-nostdin", "-y", "-ss", start, "-to", stop, "-i", Movie];

    /// <summary>A filter script's exact text, a byte order mark included; null when it does not exist.</summary>
    private static string? ReadScript(string path) => File.Exists(path) ? Encoding.UTF8.GetString(File.ReadAllBytes(path)) : null;

    private static string[] Encode(string filter) => ["-vf", filter, "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p"];

    /// <summary>A recording whose finished movie lies in the project's recordings folder, as a Movie Maker run leaves it.</summary>
    private static async Task<Recording> MovieInAsync(TempDirectory project)
    {
        string recordings = Directory.CreateDirectory(project.Combine(".godot", "godot-mcp", "recordings")).FullName;
        string movie = Path.Combine(recordings, "20260926-120000-000-Probe.avi");
        await File.WriteAllTextAsync(movie, "movie", TestContext.Current.CancellationToken);
        return new Recording(movie);
    }

    /// <summary>The clip destination <paramref name="path"/> names in the project, checked as a start mark with that path checks it.</summary>
    private static OutputTarget Destination(TempDirectory project, string path, bool overwrite)
    {
        var places = OutputPlaces.Find(project.Path, NullLogger.Instance);
        return OutputDestination.Resolve(places, path, ".mp4", overwrite);
    }

    /// <summary>
    /// Cuts the recording with an ffmpeg that writes each output file it is asked for, holding the file's own name so clips
    /// can be told apart, except that with <paramref name="failFirst"/> the first encode fails, as one without libx264 does.
    /// </summary>
    private static Task<RecordingResult> CutMakingClipsAsync(Recording recording, bool failFirst)
    {
        int runs = 0;
        return RecordingCut.CutClipsAsync(
            recording,
            "ffmpeg.exe",
            (request, _) =>
            {
                bool fails = failFirst && ++runs == 1;
                if (!fails)
                {
                    File.WriteAllText(request.Arguments[^1], Path.GetFileName(request.Arguments[^1]));
                }

                return Task.FromResult(new ToolProcessResult(fails ? 8 : 0, TimeSpan.Zero, KillReason.None));
            },
            TestContext.Current.CancellationToken
        );
    }
}
