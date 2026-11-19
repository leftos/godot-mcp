using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Tools;

/// <summary>batch_drive's per-step summary: which results it keeps, what it puts in place of the rest, and its cut texts.</summary>
public sealed class BatchSummaryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _project;

    public BatchSummaryTests()
    {
        _project = _temp.Combine("game");
        Directory.CreateDirectory(_project);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ACaptureDigestKeepsTheGamesErrorsAndARaisingCall()
    {
        JsonObject capture = Capture(200);
        capture["errors"] = new JsonArray("E 0:00:01 a raised error");
        capture["errorsOmitted"] = 4;
        capture["call"] = new JsonObject { ["method"] = "jump", ["raised"] = "Invalid call" };

        (JsonObject summary, _) = BatchSummary.Summarise(Result(Passed(0, "capture_frames", capture)));

        JsonObject digest = summary["steps"]![0]!["result"]!.AsObject();
        Assert.True(JsonNode.DeepEquals(capture["errors"], digest["errors"]), digest.ToJsonString());
        Assert.Equal(4, digest["errorsOmitted"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(capture["call"], digest["call"]), digest.ToJsonString());
    }

    [Fact]
    public void AResultOfExactlyTheCapIsReturnedUnchangedAndOneCharacterMoreIsSummarised()
    {
        JsonObject exact = OfLength(20000);
        JsonObject over = OfLength(20001);

        string unchanged = BatchSummary.Reply(exact, null, _project, NullLogger.Instance);
        bool spilledAtTheCap = Directory.Exists(LogFolder);
        JsonObject summary = Reply(over, null);

        Assert.Equal(exact.ToJsonString(), unchanged);
        Assert.False(spilledAtTheCap, "nothing is spilled at the cap");
        string path = summary["full"]!.GetValue<string>();
        Assert.Equal(
            $"the result ran to 20001 characters, past 20000; 1 steps' results are left out here; the whole result is in {path}",
            summary["cut"]!.GetValue<string>()
        );
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(over.ToJsonString()), JsonNode.Parse(File.ReadAllText(path))),
            "the spill file holds the whole result"
        );
    }

    [Fact]
    public void SummaryOnASmallResultStillSpillsAndNamesTheFile()
    {
        JsonObject result = Result(Passed(0, "click", new JsonObject { ["clicked"] = true }));

        JsonObject summary = Reply(result, SummaryMode());

        string path = summary["full"]!.GetValue<string>();
        Assert.Matches(@"batch-result-\d{8}-\d{6}-[0-9a-f]{4}\.json$", path);
        Assert.Equal(
            $"options.result is summary: 0 steps' results are left out here; the whole result is in {path}",
            summary["cut"]!.GetValue<string>()
        );
        Assert.True(JsonNode.DeepEquals(result["steps"], summary["steps"]), summary.ToJsonString());
    }

    [Fact]
    public void WithNoSessionResolvedTheCutSaysSoAndThereIsNoFull()
    {
        JsonObject summary = JsonNode.Parse(BatchSummary.Reply(OfLength(20001), null, null, NullLogger.Instance))!.AsObject();

        Assert.False(summary.ContainsKey("full"), summary.ToJsonString());
        Assert.Equal(
            "the result ran to 20001 characters, past 20000; 1 steps' results are left out here; the whole result could not be written: "
                + "no session resolved",
            summary["cut"]!.GetValue<string>()
        );
    }

    [Fact]
    public void AFailedWriteNamesTheExceptionsMessageAndThereIsNoFull()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFolder)!);
        File.WriteAllText(LogFolder, "a file where the log folder goes");
        IOException expected = Assert.Throws<IOException>(() => Directory.CreateDirectory(LogFolder));

        JsonObject summary = Reply(OfLength(20001), null);

        Assert.False(summary.ContainsKey("full"), summary.ToJsonString());
        Assert.Equal(
            "the result ran to 20001 characters, past 20000; 1 steps' results are left out here; the whole result could not be written: "
                + expected.Message,
            summary["cut"]!.GetValue<string>()
        );
    }

    [Fact]
    public void AShortResultLeftOutKeepsTheGamesErrors()
    {
        JsonArray errors = new(
            new JsonObject
            {
                ["seq"] = 1,
                ["message"] = "boom",
                ["file"] = "res://main.gd",
                ["line"] = 3,
            }
        );
        JsonObject click = Passed(25, "click", new JsonObject { ["clicked"] = true, ["errors"] = errors.DeepClone() });
        JsonObject result = Result([.. Enumerable.Range(0, 25).Select(index => Passed(index, "run_script", Value(880))), click]);

        JsonObject summary = Reply(result, null);

        JsonObject shown = summary["steps"]![25]!.AsObject();
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["errors"] = errors }, shown["result"]), shown.ToJsonString());
        Assert.Equal(click["result"]!.ToJsonString().Length, shown["resultOmitted"]!.GetValue<int>());
    }

    [Fact]
    public void AShortResultLeftOutKeepsACallThatRaised()
    {
        JsonObject call = new() { ["value"] = null, ["raised"] = "boom" };
        JsonObject wait = new()
        {
            ["met"] = true,
            ["frames"] = 3,
            ["call"] = call.DeepClone(),
        };
        JsonObject result = Result([
            .. Enumerable.Range(0, 25).Select(index => Passed(index, "run_script", Value(880))),
            Passed(25, "wait_for", wait),
        ]);

        JsonObject summary = Reply(result, null);

        JsonObject shown = summary["steps"]![25]!.AsObject();
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["call"] = call }, shown["result"]), shown.ToJsonString());
        Assert.Equal(wait.ToJsonString().Length, shown["resultOmitted"]!.GetValue<int>());
    }

    [Fact]
    public void AKeptCallThatHoldsTheReplyOverTheCapIsTrimmedToItsRaisedText()
    {
        JsonObject[] waits = [.. Enumerable.Range(0, 12).Select(index => WaitThatRaised($"boom {index + 1}"))];
        JsonObject result = Result([.. waits.Select((wait, index) => Passed(index, "wait_for", wait))]);

        string reply = BatchSummary.Reply(result, null, _project, NullLogger.Instance);
        JsonObject summary = JsonNode.Parse(reply)!.AsObject();
        JsonArray steps = summary["steps"]!.AsArray();

        Assert.True(reply.Length <= 20000, $"{reply.Length} characters");
        List<int> trimmed = [.. Enumerable.Range(0, steps.Count).Where(index => !CallAt(steps, index).ContainsKey("valuePreview"))];
        Assert.NotEmpty(trimmed);
        int first = trimmed[0];
        Assert.Equal(Enumerable.Range(first, steps.Count - first), trimmed);
        for (int index = 0; index < first; index++)
        {
            Assert.True(JsonNode.DeepEquals(waits[index]["call"], CallAt(steps, index)), steps[index]!.ToJsonString());
        }

        for (int index = first; index < steps.Count; index++)
        {
            JsonObject expected = new() { ["raised"] = waits[index]["call"]!["raised"]!.DeepClone(), ["valueLength"] = 5000 };
            Assert.True(JsonNode.DeepEquals(expected, CallAt(steps, index)), steps[index]!.ToJsonString());
        }

        Assert.True(
            JsonNode.DeepEquals(new JsonObject { ["raised"] = "boom 12", ["valueLength"] = 5000 }, CallAt(steps, 11)),
            CallAt(steps, 11).ToJsonString()
        );
    }

    [Fact]
    public void ARepeatedFailedAtReasonIsShortenedBeforeAnyResultIsLeftOut()
    {
        string error = new('e', 4000);
        JsonObject failed = new()
        {
            ["index"] = 13,
            ["tool"] = "click",
            ["ok"] = false,
            ["error"] = error,
        };
        JsonObject result = Result([.. Enumerable.Range(0, 13).Select(index => Passed(index, "run_script", Value(880))), failed]);
        result["passed"] = false;
        result["failedAt"] = new JsonObject { ["index"] = 13, ["reason"] = error };
        Assert.True(result.ToJsonString().Length > 20000, $"{result.ToJsonString().Length} characters");

        JsonObject summary = Reply(result, null);

        Assert.Equal(BatchSummary.RepeatedReason, summary["failedAt"]!["reason"]!.GetValue<string>());
        Assert.All(summary["steps"]!.AsArray(), step => Assert.False(step!.AsObject().ContainsKey("resultOmitted"), step.ToJsonString()));
        Assert.True(summary.ToJsonString().Length <= 20000);
    }

    [Fact]
    public void AFailedAtReasonShorterThanItsReplacementIsLeftAsItIs()
    {
        JsonObject failed = new()
        {
            ["index"] = 0,
            ["tool"] = "click",
            ["ok"] = false,
            ["error"] = "no",
            ["errorsElsewhere"] = new JsonObject { ["guest"] = new JsonArray(new string('x', 25000)) },
        };
        JsonObject result = Result(failed);
        result["passed"] = false;
        result["failedAt"] = new JsonObject { ["index"] = 0, ["reason"] = "no" };

        JsonObject summary = Reply(result, null);

        Assert.Equal("no", summary["failedAt"]!["reason"]!.GetValue<string>());
        Assert.Contains("still over", summary["cut"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALongStringCutKeepsACaptureDigestsWarningAndFolder()
    {
        JsonObject capture = Capture(1000);
        capture["folder"] = "C:/" + new string('f', 300);
        capture["warning"] = new string('w', 300);
        capture["stopped"] = new string('s', 300);
        capture["errors"] = new JsonArray(new string('e', 25000));

        JsonObject summary = Reply(Result(Passed(0, "capture_frames", capture)), null);

        JsonObject digest = summary["steps"]![0]!["result"]!.AsObject();
        Assert.False(digest.ContainsKey("stopped"), digest.ToJsonString());
        Assert.Equal(capture["folder"]!.GetValue<string>(), digest["folder"]!.GetValue<string>());
        Assert.Equal(capture["warning"]!.GetValue<string>(), digest["warning"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(capture["errors"], digest["errors"]));
    }

    [Fact]
    public void ARepeatedReasonOfAFailedGroupMemberIsShortenedAndItsSiblingKept()
    {
        string error = new('e', 10000);
        JsonObject member = new()
        {
            ["index"] = 1,
            ["tool"] = "click",
            ["session"] = "guest",
            ["ok"] = false,
            ["error"] = error,
        };
        JsonObject sibling = Passed(0, "run_script", Value(500));
        JsonObject group = new()
        {
            ["index"] = 0,
            ["parallel"] = true,
            ["ok"] = false,
            ["members"] = new JsonArray(sibling, member),
        };
        JsonObject result = Result(group);
        result["passed"] = false;
        result["failedAt"] = new JsonObject
        {
            ["index"] = 0,
            ["member"] = 1,
            ["reason"] = error,
        };

        JsonObject summary = Reply(result, null);

        Assert.Equal(BatchSummary.RepeatedReason, summary["failedAt"]!["reason"]!.GetValue<string>());
        JsonArray members = summary["steps"]![0]!["members"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(sibling, members[0]), members[0]!.ToJsonString());
        Assert.Equal(error, members[1]!["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(1000, false)]
    [InlineData(1001, true)]
    public void APassedResultIsKeptAtAThousandCharactersAndReplacedPastThem(int length, bool replaced)
    {
        int bare = new JsonObject { ["value"] = "" }
            .ToJsonString()
            .Length;
        JsonObject value = Value(length - bare);
        Assert.Equal(length, value.ToJsonString().Length);

        (JsonObject summary, int count) = BatchSummary.Summarise(Result(Passed(0, "run_script", value)));

        Assert.Equal(replaced ? 1 : 0, count);
        Assert.Equal(replaced, summary["steps"]![0]!.AsObject().ContainsKey("resultOmitted"));
    }

    private static JsonObject Value(int characters) => new() { ["value"] = new string('v', characters) };

    /// <summary>A samples array of 120 ten-character strings: over the 1000-character limit past which a result is digested.</summary>
    private static JsonArray Samples() => new([.. Enumerable.Range(0, 120).Select(_ => (JsonNode)new string('x', 10))]);

    /// <summary>A wait_for result whose call raised after a 2000-character value preview.</summary>
    private static JsonObject WaitThatRaised(string raised) =>
        new()
        {
            ["met"] = true,
            ["frames"] = 3,
            ["samples"] = Samples(),
            ["call"] = new JsonObject
            {
                ["valuePreview"] = new string('v', 2000),
                ["valueLength"] = 5000,
                ["raised"] = raised,
            },
        };

    /// <summary>The step's result call.</summary>
    private static JsonObject CallAt(JsonArray steps, int index) => steps[index]!["result"]!["call"]!.AsObject();

    [Fact]
    public void ManyShortResultsPastTheCapLeaveTheLaterOnesOutUntilTheReplyFits()
    {
        JsonObject result = Result([
            .. Enumerable.Range(0, 100).Select(index => Passed(index, "run_script", new JsonObject { ["value"] = new string('v', 880) })),
        ]);

        JsonObject summary = Reply(result, null);

        string reply = summary.ToJsonString();
        Assert.True(reply.Length <= 20000, $"{reply.Length} characters");
        JsonArray steps = summary["steps"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(result["steps"]![0], steps[0]), steps[0]!.ToJsonString());
        Assert.False(steps[99]!.AsObject().ContainsKey("result"), steps[99]!.ToJsonString());
        Assert.Equal(result["steps"]![99]!["result"]!.ToJsonString().Length, steps[99]!["resultOmitted"]!.GetValue<int>());
        int omitted = steps.Count(step => step!.AsObject().ContainsKey("resultOmitted"));
        Assert.Equal(steps.Skip(100 - omitted), steps.Where(step => step!.AsObject().ContainsKey("resultOmitted")));
        Assert.Contains($"; {omitted} steps' results are left out here; ", summary["cut"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ACaptureDigestOfAThousandFilesNamesTheirCount()
    {
        JsonObject capture = Capture(1000);
        capture["files"] = new JsonArray([.. Enumerable.Range(0, 1000).Select(index => (JsonNode)$"capture-frame-{index:0000}.png")]);

        JsonObject summary = Reply(Result(Passed(0, "capture_frames", capture)), null);

        JsonObject digest = summary["steps"]![0]!["result"]!.AsObject();
        Assert.Equal(1000, digest["filesOmitted"]!.GetValue<int>());
        Assert.False(digest.ContainsKey("files"), digest.ToJsonString());
        Assert.Equal("C:/game/.tmp/frames", digest["folder"]!.GetValue<string>());
        Assert.True(summary.ToJsonString().Length <= 20000);
    }

    [Fact]
    public void ALongValuePreviewIsDroppedAndTheOriginalLengthStays()
    {
        JsonObject value = new()
        {
            ["valuePreview"] = new string('p', 25000),
            ["valueLength"] = 90000,
            ["errors"] = new JsonArray("E " + new string('e', 300)),
        };
        JsonObject summary = Reply(Result(Passed(0, "run_script", value)), null);

        JsonObject entry = summary["steps"]![0]!.AsObject();
        Assert.False(entry["result"]!.AsObject().ContainsKey("valuePreview"), entry.ToJsonString());
        Assert.Equal(90000, entry["result"]!["valueLength"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(value["errors"], entry["result"]!["errors"]));
        Assert.Equal(value.ToJsonString().Length, entry["resultOmitted"]!.GetValue<int>());
    }

    [Fact]
    public void AFailedAtReasonRepeatingTheFailedStepsErrorIsShortened()
    {
        string error = new('e', 15000);
        JsonObject failed = new()
        {
            ["index"] = 0,
            ["tool"] = "click",
            ["ok"] = false,
            ["error"] = error,
        };
        JsonObject result = Result(failed);
        result["passed"] = false;
        result["failedAt"] = new JsonObject { ["index"] = 0, ["reason"] = error };

        JsonObject summary = Reply(result, null);

        Assert.Equal("the failed step's error, above", summary["failedAt"]!["reason"]!.GetValue<string>());
        Assert.Equal(error, summary["steps"]![0]!["error"]!.GetValue<string>());
        Assert.DoesNotContain("still over", summary["cut"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void AReplyThatStillDoesNotFitSaysSoBeforeTheFile()
    {
        JsonObject failed = new()
        {
            ["index"] = 0,
            ["tool"] = "click",
            ["ok"] = false,
            ["error"] = new string('e', 25000),
        };

        JsonObject summary = Reply(Result(failed), null);

        string path = summary["full"]!.GetValue<string>();
        Assert.EndsWith(
            $"; 0 steps' results are left out here; the reply is still over 20000 characters; the whole result is in {path}",
            summary["cut"]!.GetValue<string>(),
            StringComparison.Ordinal
        );
        Assert.Equal(25000, summary["steps"]![0]!["error"]!.GetValue<string>().Length);
    }

    private string LogFolder => Path.Combine(_project, ".godot", "godot-mcp");

    private static BatchOptions SummaryMode() => new("summary");

    private JsonObject Reply(JsonObject result, BatchOptions? options) =>
        JsonNode.Parse(BatchSummary.Reply(result, options, _project, NullLogger.Instance))!.AsObject();

    /// <summary>A one-step result whose serialised length is exactly <paramref name="length"/>.</summary>
    private static JsonObject OfLength(int length)
    {
        int bare = Result(Passed(0, "run_script", new JsonObject { ["value"] = "" })).ToJsonString().Length;
        return Result(Passed(0, "run_script", new JsonObject { ["value"] = new string('v', length - bare) }));
    }

    [Fact]
    public void ACaptureFramesResultOfManyPointsIsReplacedByItsDigest()
    {
        JsonObject capture = Capture(200);
        int length = capture.ToJsonString().Length;
        JsonObject result = Result(Passed(0, "capture_frames", capture));

        (JsonObject summary, int replaced) = BatchSummary.Summarise(result);

        JsonObject entry = summary["steps"]![0]!.AsObject();
        JsonObject expected = new()
        {
            ["folder"] = "C:/game/.tmp/frames",
            ["files"] = new JsonArray("f0.png", "f1.png"),
            ["width"] = 640,
            ["height"] = 360,
            ["shared"] = 198,
            ["stopped"] = "the game paused",
            ["pointsOmitted"] = 200,
        };
        Assert.True(JsonNode.DeepEquals(expected, entry["result"]), entry["result"]!.ToJsonString());
        Assert.Equal(length, entry["resultOmitted"]!.GetValue<int>());
        Assert.Equal(1, replaced);
        Assert.Equal(200, result["steps"]![0]!["result"]!["points"]!.AsArray().Count);
    }

    [Fact]
    public void APassedResultOfAtMostAThousandCharactersIsKept()
    {
        JsonObject small = new() { ["value"] = new string('v', 900) };
        JsonObject result = Result(Passed(0, "run_script", small));

        (JsonObject summary, int replaced) = BatchSummary.Summarise(result);

        Assert.True(JsonNode.DeepEquals(result, summary), summary.ToJsonString());
        Assert.Equal(0, replaced);
    }

    [Fact]
    public void AFailedEntryIsKeptWhole()
    {
        JsonObject failed = new()
        {
            ["index"] = 1,
            ["assert"] = "property",
            ["ok"] = false,
            ["error"] = new string('e', 3000),
            ["startMs"] = 10.5,
            ["endMs"] = 12.5,
        };
        JsonObject result = Result(Passed(0, "capture_frames", Capture(200)), failed);
        result["passed"] = false;
        result["failedAt"] = new JsonObject { ["index"] = 1, ["reason"] = "no" };

        (JsonObject summary, int replaced) = BatchSummary.Summarise(result);

        Assert.True(JsonNode.DeepEquals(failed, summary["steps"]![1]), summary["steps"]![1]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(result["failedAt"], summary["failedAt"]));
        Assert.False(summary["passed"]!.GetValue<bool>());
        Assert.Equal(1, replaced);
    }

    [Fact]
    public void AGroupMemberPastAThousandIsShapedAndACancelledMemberKeepsCancelledAndSent()
    {
        JsonObject cancelled = new()
        {
            ["index"] = 1,
            ["tool"] = "click",
            ["session"] = "guest",
            ["ok"] = false,
            ["cancelled"] = true,
            ["sent"] = true,
            ["startMs"] = 1.0,
            ["endMs"] = 2.0,
        };
        JsonObject member = Passed(0, "capture_frames", Capture(100));
        member["session"] = "host";
        JsonObject group = new()
        {
            ["index"] = 0,
            ["parallel"] = true,
            ["ok"] = false,
            ["startMs"] = 1.0,
            ["endMs"] = 3.0,
            ["members"] = new JsonArray(member, cancelled),
        };
        JsonObject result = Result(group);

        (JsonObject summary, int replaced) = BatchSummary.Summarise(result);

        JsonArray members = summary["steps"]![0]!["members"]!.AsArray();
        Assert.Equal(100, members[0]!["result"]!["pointsOmitted"]!.GetValue<int>());
        Assert.Equal("host", members[0]!["session"]!.GetValue<string>());
        Assert.Equal(1.0, members[0]!["startMs"]!.GetValue<double>());
        Assert.True(members[0]!.AsObject().ContainsKey("resultOmitted"), members[0]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(cancelled, members[1]), members[1]!.ToJsonString());
        Assert.True(members[1]!["cancelled"]!.GetValue<bool>());
        Assert.True(members[1]!["sent"]!.GetValue<bool>());
        Assert.True(summary["steps"]![0]!["parallel"]!.GetValue<bool>());
        Assert.Equal(1, replaced);
    }

    [Fact]
    public void AGroupMembersDigestKeepsACallThatRaised()
    {
        JsonObject member = Passed(0, "wait_for", WaitThatRaised("boom 1"));
        member["session"] = "host";
        JsonObject group = new()
        {
            ["index"] = 0,
            ["parallel"] = true,
            ["ok"] = true,
            ["members"] = new JsonArray(member),
        };

        (JsonObject summary, int replaced) = BatchSummary.Summarise(Result(group));

        JsonObject shaped = summary["steps"]![0]!["members"]![0]!.AsObject();
        JsonObject digest = shaped["result"]!.AsObject();
        Assert.True(JsonNode.DeepEquals(member["result"]!["call"], digest["call"]), digest.ToJsonString());
        Assert.False(digest.ContainsKey("samples"), digest.ToJsonString());
        Assert.Equal(1, replaced);
    }

    [Fact]
    public void AnotherToolsLongResultKeepsItsScalarFieldsAndErrors()
    {
        JsonObject state = new()
        {
            ["node"] = "Main/Player",
            ["count"] = 3,
            ["live"] = true,
            ["missing"] = null,
            ["tree"] = new JsonArray([.. Enumerable.Range(0, 100).Select(index => (JsonNode)$"child number {index} of the tree")]),
            ["values"] = new JsonObject { ["x"] = 1 },
            ["errors"] = new JsonArray("a raised error"),
        };
        JsonObject entry = Passed(0, "get_game_state", state);
        entry["errorsElsewhere"] = new JsonObject { ["guest"] = new JsonArray("boom") };
        JsonObject result = Result(entry);

        (JsonObject summary, int replaced) = BatchSummary.Summarise(result);

        JsonObject shaped = summary["steps"]![0]!.AsObject();
        JsonObject expected = new()
        {
            ["node"] = "Main/Player",
            ["count"] = 3,
            ["live"] = true,
            ["missing"] = null,
            ["errors"] = new JsonArray("a raised error"),
        };
        Assert.True(JsonNode.DeepEquals(expected, shaped["result"]), shaped["result"]!.ToJsonString());
        Assert.Equal(state.ToJsonString().Length, shaped["resultOmitted"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(entry["errorsElsewhere"], shaped["errorsElsewhere"]));
        Assert.Equal("get_game_state", shaped["tool"]!.GetValue<string>());
        Assert.Equal(1, replaced);
    }

    [Fact]
    public void AnotherToolsDigestKeepsACallThatRaised()
    {
        JsonObject call = new() { ["value"] = null, ["raised"] = "Invalid call. Nonexistent function 'PlayStep'" };
        JsonObject wait = new()
        {
            ["met"] = true,
            ["frames"] = 3,
            ["samples"] = Samples(),
            ["call"] = call.DeepClone(),
        };

        (JsonObject summary, _) = BatchSummary.Summarise(Result(Passed(0, "wait_for", wait)));

        JsonObject digest = summary["steps"]![0]!["result"]!.AsObject();
        Assert.True(digest["met"]!.GetValue<bool>(), digest.ToJsonString());
        Assert.Equal(3, digest["frames"]!.GetValue<int>());
        Assert.False(digest.ContainsKey("samples"), digest.ToJsonString());
        Assert.True(JsonNode.DeepEquals(call, digest["call"]), digest.ToJsonString());
    }

    [Fact]
    public void AnotherToolsDigestDropsACallThatDidNotRaise()
    {
        JsonObject wait = new()
        {
            ["met"] = true,
            ["frames"] = 3,
            ["samples"] = Samples(),
            ["call"] = new JsonObject { ["value"] = 3 },
        };

        (JsonObject summary, _) = BatchSummary.Summarise(Result(Passed(0, "wait_for", wait)));

        JsonObject digest = summary["steps"]![0]!["result"]!.AsObject();
        Assert.False(digest.ContainsKey("call"), digest.ToJsonString());
    }

    [Fact]
    public void TheAutoCutNamesTheLengthTheStepsLeftOutAndTheFile() =>
        Assert.Equal(
            "the result ran to 110000 characters, past 20000; 3 steps' results are left out here; the whole result is in C:/g/r.json",
            new BatchCut(null, 110000, "C:/g/r.json", null).Text(3, stillOver: false)
        );

    [Fact]
    public void TheSummaryCutNamesTheOptionTheStepsLeftOutAndTheFile() =>
        Assert.Equal(
            "options.result is summary: 2 steps' results are left out here; the whole result is in C:/g/r.json",
            new BatchCut("summary", 500, "C:/g/r.json", null).Text(2, stillOver: false)
        );

    [Fact]
    public void ACutWithNoFileWrittenSaysWhy()
    {
        Assert.Equal(
            "the result ran to 30000 characters, past 20000; 1 steps' results are left out here; the whole result could not be written: "
                + "no session resolved",
            new BatchCut(null, 30000, null, "no session resolved").Text(1, stillOver: false)
        );
        Assert.Equal(
            "options.result is summary: 0 steps' results are left out here; the whole result could not be written: access denied",
            new BatchCut("summary", 30000, null, "access denied").Text(0, stillOver: false)
        );
    }

    private static JsonObject Result(params JsonObject[] entries) =>
        new()
        {
            ["passed"] = true,
            ["startedAt"] = 100.25,
            ["steps"] = new JsonArray(entries),
        };

    private static JsonObject Passed(int index, string tool, JsonObject result) =>
        new()
        {
            ["index"] = index,
            ["tool"] = tool,
            ["ok"] = true,
            ["result"] = result,
            ["startMs"] = 1.0,
            ["endMs"] = 2.0,
        };

    /// <summary>A capture_frames result of <paramref name="points"/> points over two files.</summary>
    private static JsonObject Capture(int points) =>
        new()
        {
            ["folder"] = "C:/game/.tmp/frames",
            ["width"] = 640,
            ["height"] = 360,
            ["window"] = new JsonObject { ["width"] = 1280, ["height"] = 720 },
            ["viewport"] = new JsonObject { ["width"] = 640, ["height"] = 360 },
            ["files"] = new JsonArray("f0.png", "f1.png"),
            ["points"] = new JsonArray([
                .. Enumerable
                    .Range(0, points)
                    .Select(index =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["at"] = index * 0.1,
                                ["frame"] = index,
                                ["file"] = Math.Min(index, 1),
                                ["gameSeconds"] = index * 0.1,
                                ["late"] = 0,
                            }
                    ),
            ]),
            ["shared"] = points - 2,
            ["stopped"] = "the game paused",
            ["call"] = new JsonObject { ["every"] = 0.1 },
        };
}
