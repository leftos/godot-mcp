using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// list_game_tools and call_game_tool against the CsProbe game with CsTools.cs as its Tools autoload, which marks methods on
/// that autoload and its base type, on static classes and on a type nothing owns, and whose scene root marks one: each owner's
/// on, the schema of a method with an enum and defaults, the reasons a tool is unavailable, paging and the name filter; a call
/// on each owner kind, an awaited Task and one past its timeout, a thrown exception, named and defaulted arguments, each
/// refusal, a stale build and the errors feed, and a call in a batch; on launches of their own, owners read again after the
/// tree changes (a call being the process's first op) and a game that marks nothing; and a GDScript project refused before
/// the game is asked.
/// </summary>
public sealed class GameToolTests(SharedCsToolsSession shared) : IClassFixture<SharedCsToolsSession>
{
    private const int GameToolTestTimeoutMs = 150_000;
    private const int ScriptTimeoutMs = 10_000;

    // Every tool CsProbe marks, as list_game_tools sorts them: by name, then by on.
    private static readonly string[] AllNames =
    [
        "Advance",
        "Blank",
        "Boom",
        "Complain",
        "Dawdle",
        "FetchLater",
        "Greet",
        "Heal",
        "Huge",
        "SetMood",
        "Sum",
        "TryFind",
        "Twin",
        "Twin",
        "Wander",
    ];

    private const string StaleHint = "The game runs an older build than the one on disk; restart_project runs the new one.";

    private const string NoToolsHint =
        "The game marks no method as a tool: declare an attribute class named GodotMcpToolAttribute in the game (any "
        + "namespace) whose constructor takes the description, and put [GodotMcpTool(\"what it does\")] on the methods to list. "
        + "A [Conditional(\"DEBUG\")] attribute class leaves the marks out of a Release build.";

    /// <summary>How a refusal of the helper's own reaches the agent: the tool's failure, then the helper's words.</summary>
    private const string HelperRefused = "call_game_tool failed: The C# helper refused the request: ";

    private const string OwnerRule = "a game tool runs on an autoload, on the current scene's root, or as a static method.";

    private readonly SharedCsToolsSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, shared.Bridge);

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task ARebuiltGameListsWhatItLoadedAndSaysItsBuildIsStale()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject fresh = await ListAsync(null, cancellation);

        await StaleBuild.WhileStaleAsync(
            _shared.ProbeDirectory,
            "CsProbe",
            async () =>
            {
                JsonObject stale = await ListAsync(null, cancellation);

                Assert.Equal("stale", stale["build"]!.GetValue<string>());
                Assert.Equal(StaleHint, stale["hint"]!.GetValue<string>());
                Assert.Equal(AllNames, Names(stale));
            },
            cancellation
        );
        Assert.False(fresh.ContainsKey("build"));
        Assert.False(fresh.ContainsKey("hint"));
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task EachOwnerKindRunsOnItsOwnOn()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject[] tools = Tools(await ListAsync(null, cancellation));

        Assert.Equal(AllNames, tools.Select(tool => tool["name"]!.GetValue<string>()));
        AssertAvailableOn(Single(tools, "Heal"), "Tools");
        AssertAvailableOn(Single(tools, "FetchLater"), "Tools");
        AssertAvailableOn(Single(tools, "Boom"), "Tools");
        AssertAvailableOn(Single(tools, "Sum"), "static CsStatics");
        AssertAvailableOn(Single(tools, "Advance"), "/root/CsProbe");
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AMethodDeclaredOnABaseTypeOfAnAutoloadRunsOnTheAutoload()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject greet = Single(Tools(await ListAsync(null, cancellation)), "Greet");

        AssertAvailableOn(greet, "Tools");
        Assert.Equal("Answers a greeting from the autoload's base type.", greet["description"]!.GetValue<string>());
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AMarkWithNoDescriptionIsUnavailableNamingItsMethod()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject blank = Single(Tools(await ListAsync(null, cancellation)), "Blank");

        Assert.False(blank["available"]!.GetValue<bool>());
        Assert.Equal(
            "CsTools.Blank is marked GodotMcpTool, but its mark has no description string; give the attribute a constructor "
                + "taking the description.",
            blank["reason"]!.GetValue<string>()
        );
        Assert.True(blank.ContainsKey("description"));
        Assert.Null(blank["description"]);
        Assert.Equal("Tools", blank["on"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task OwnersAreReadFromTheLiveTreeAtEachList()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using SharedCsToolsSession own = new();
        await own.InitializeAsync();
        RuntimeTools tools = new(own.Sessions, own.Bridge);
        // A call as the game process's first game tool op walks the marks as a first list would.
        JsonObject firstCall = await CallAsync(tools, "Sum", """{"a": 1, "b": 1}""", null, cancellation);
        JsonObject[] before = Tools(await ListAsync(tools, null, cancellation));
        string script =
            "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\tscene_tree.root.get_node(\"Tools\").free()\n"
            + "\tscene_tree.current_scene.free()\n\treturn scene_tree.current_scene == null\n";
        JsonNode freed = JsonNode.Parse(await tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation))!["value"]!;
        JsonObject[] after = Tools(await ListAsync(tools, null, cancellation));

        Assert.Equal(2, firstCall["value"]!.GetValue<int>());
        Assert.True(freed.GetValue<bool>());
        AssertAvailableOn(Single(before, "Heal"), "Tools");
        AssertAvailableOn(Single(before, "Advance"), "/root/CsProbe");
        AssertUnowned(
            Single(after, "Heal"),
            "CsTools is neither an autoload's type nor the current scene root's (there is none), and Heal is not static; " + OwnerRule
        );
        AssertUnowned(
            Single(after, "Advance"),
            "CsProbeNode is neither an autoload's type nor the current scene root's (there is none), and Advance is not static; " + OwnerRule
        );
        AssertAvailableOn(Single(after, "Sum"), "static CsStatics");
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGameThatMarksNothingListsNoToolsWithTheHintJoinedToTheStaleOne()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using SharedCsUnmarkedSession own = new();
        await own.InitializeAsync();
        RuntimeTools tools = new(own.Sessions, own.Bridge);
        JsonObject fresh = await ListAsync(tools, null, cancellation);
        JsonObject? stale = null;

        await StaleBuild.WhileStaleAsync(own.ProbeDirectory, "CsProbe", async () => stale = await ListAsync(tools, null, cancellation), cancellation);

        Assert.Empty(Tools(fresh));
        Assert.Equal(0, fresh["total"]!.GetValue<int>());
        Assert.Equal(NoToolsHint, fresh["hint"]!.GetValue<string>());
        Assert.False(fresh.ContainsKey("build"));
        Assert.Equal("stale", stale!["build"]!.GetValue<string>());
        Assert.Equal(StaleHint + " " + NoToolsHint, stale["hint"]!.GetValue<string>());
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task ATaskToolReturnsItsTaskType()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject[] tools = Tools(await ListAsync(null, cancellation));

        Assert.Equal("Task<string>", Single(tools, "FetchLater")["returns"]!.GetValue<string>());
        Assert.Equal("int", Single(tools, "Heal")["returns"]!.GetValue<string>());
        Assert.Equal("void", Single(tools, "Boom")["returns"]!.GetValue<string>());
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AnEnumAndDefaultsBecomeTheSchemaWithTheMarksWhenAndReadOnly()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject[] tools = Tools(await ListAsync(null, cancellation));
        JsonObject mood = Single(tools, "SetMood");
        JsonObject heal = Single(tools, "Heal");

        JsonObject properties = mood["args"]!["properties"]!.AsObject();
        Assert.Equal(["Calm", "Angry", "Sleepy"], properties["mood"]!["enum"]!.AsArray().Select(name => name!.GetValue<string>()));
        Assert.Equal("How the probe feels.", properties["mood"]!["description"]!.GetValue<string>());
        Assert.Equal(2, properties["times"]!["default"]!.GetValue<int>());
        Assert.Equal("calm", properties["note"]!["default"]!.GetValue<string>());
        Assert.Equal(["mood"], mood["args"]!["required"]!.AsArray().Select(name => name!.GetValue<string>()));
        Assert.Equal("Sets the probe's mood a number of times.", mood["description"]!.GetValue<string>());
        Assert.Equal("any time", mood["when"]!.GetValue<string>());
        Assert.True(mood["readOnly"]!.GetValue<bool>());
        Assert.False(heal.ContainsKey("when"));
        Assert.False(heal["readOnly"]!.GetValue<bool>());
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AnOutParameterMakesAToolUnavailableWithNoArgs()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject tryFind = Single(Tools(await ListAsync(null, cancellation)), "TryFind");

        Assert.False(tryFind["available"]!.GetValue<bool>());
        Assert.Contains("parameter 'found' is out int", tryFind["reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(tryFind.ContainsKey("args"));
        Assert.Equal("Tools", tryFind["on"]!.GetValue<string>());
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AToolNothingOwnsIsUnavailableAndKeepsItsArgs()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject wander = Single(Tools(await ListAsync(null, cancellation)), "Wander");

        Assert.False(wander["available"]!.GetValue<bool>());
        Assert.True(wander.ContainsKey("on"));
        Assert.Null(wander["on"]);
        Assert.Equal(
            "CsStray is neither an autoload's type nor the current scene root's (/root/CsProbe, a CsProbeNode), and Wander is not "
                + "static; a game tool runs on an autoload, on the current scene's root, or as a static method.",
            wander["reason"]!.GetValue<string>()
        );
        Assert.NotNull(wander["args"]);
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task TwoToolsSharingANameAreBothUnavailableNamingTheOther()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject[] twins = [.. Tools(await ListAsync(new GameToolsOptions(Name: "Twin"), cancellation))];

        Assert.Equal(2, twins.Length);
        Assert.Equal(["Tools", "static CsStatics"], twins.Select(twin => twin["on"]!.GetValue<string>()));
        Assert.All(twins, twin => Assert.False(twin["available"]!.GetValue<bool>()));
        Assert.StartsWith("'Twin' also names Mirror on static CsStatics;", twins[0]["reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("'Twin' also names Twin on Tools;", twins[1]["reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task PagesByOffsetAndLimit()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject first = await ListAsync(new GameToolsOptions(Limit: 2), cancellation);
        JsonObject second = await ListAsync(new GameToolsOptions(Offset: 2, Limit: 2), cancellation);
        JsonObject last = await ListAsync(new GameToolsOptions(Offset: 8), cancellation);

        Assert.Equal(AllNames[..2], Names(first));
        Assert.Equal(AllNames.Length, first["total"]!.GetValue<int>());
        Assert.Equal(2, first["next"]!.GetValue<int>());
        Assert.Equal(AllNames[2..4], Names(second));
        Assert.Equal(2, second["offset"]!.GetValue<int>());
        Assert.Equal(AllNames[8..], Names(last));
        Assert.False(last.ContainsKey("next"));
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task NameFiltersByACaseInsensitiveSubstring()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject result = await ListAsync(new GameToolsOptions(Name: "tWi"), cancellation);

        Assert.Equal(["Twin", "Twin"], Names(result));
        Assert.Equal(2, result["total"]!.GetValue<int>());
        Assert.Empty(Names(await ListAsync(new GameToolsOptions(Name: "NoSuchTool"), cancellation)));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGDScriptProjectIsRefusedNamingCallMethod()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject project = new();
        await using SessionHarness harness = new();
        await harness.Sessions.LaunchAsync(new LaunchRequest(project.Directory, null, [], [], Quiet: true, false, Prepare: true), null, cancellation);
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());

        McpException refused = await Assert.ThrowsAsync<McpException>(() => tools.ListGameToolsAsync(cancellationToken: cancellation));
        McpException refusedCall = await Assert.ThrowsAsync<McpException>(() => tools.CallGameToolAsync("Heal", cancellationToken: cancellation));
        McpException refusedWait = await Assert.ThrowsAsync<McpException>(() =>
            tools.WaitForAsync(
                new WaitCondition(Frames: 1),
                null,
                new WaitOptions(Call: new MethodCall(Tool: "Heal")),
                cancellationToken: cancellation
            )
        );
        string script =
            "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\treturn scene_tree.has_meta(\"godot_mcp_dotnet\")\n";
        JsonNode loaded = JsonNode.Parse(await tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation))!["value"]!;

        Assert.Equal(
            "list_game_tools failed: This project has no C# assembly, so it has no game tools, which are C# methods marked "
                + "[GodotMcpTool]; call_method calls a GDScript game's own methods.",
            refused.Message
        );
        Assert.Equal(
            "call_game_tool failed: This project has no C# assembly, so it has no game tools, which are C# methods marked "
                + "[GodotMcpTool]; call_method calls a GDScript game's own methods.",
            refusedCall.Message
        );
        Assert.Equal(
            "wait_for failed: This project has no C# assembly, so it has no game tools, which are C# methods marked "
                + "[GodotMcpTool]; call_method calls a GDScript game's own methods.",
            refusedWait.Message
        );
        Assert.False(loaded.GetValue<bool>());
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AnAutoloadToolAnswersItsValueAndType()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonObject result = await CallAsync("Heal", """{"amount": 7}""", null, cancellation);

        Assert.Equal("Heal", result["tool"]!.GetValue<string>());
        Assert.Equal(7, result["value"]!.GetValue<int>());
        Assert.Equal("System.Int32", result["type"]!.GetValue<string>());
        Assert.False(result.ContainsKey("build"));
        Assert.False(result.ContainsKey("errors"));
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AStaticToolRunsWithNoOwner()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonObject result = await CallAsync("Sum", """{"b": 3, "a": 2}""", null, cancellation);

        Assert.Equal(5, result["value"]!.GetValue<int>());
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task ASceneRootToolRunsOnTheCurrentSceneAndTakesItsDefault()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        int first = (await CallAsync("Advance", """{"steps": 2}""", null, cancellation))["value"]!.GetValue<int>();
        int second = (await CallAsync("Advance", null, null, cancellation))["value"]!.GetValue<int>();

        Assert.True(first >= 2, $"Advance answered {first} after two steps.");
        Assert.Equal(first + 1, second);
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task ATaskToolIsAwaited()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonObject result = await CallAsync("FetchLater", """{"label": "soon"}""", null, cancellation);

        Assert.Equal("later soon", result["value"]!.GetValue<string>());
        Assert.Equal("System.String", result["type"]!.GetValue<string>());
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AThrowingToolFailsWithItsExceptionsTypeMessageAndStack()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        McpException refused = await Assert.ThrowsAsync<McpException>(() => CallAsync("Boom", null, null, cancellation));

        Assert.StartsWith(HelperRefused + "Boom threw InvalidOperationException: tool failure\n", refused.Message, StringComparison.Ordinal);
        Assert.Contains("CsProbe.CsTools.Boom()", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AnEnumTakesItsNameOrNumberAndDefaultsFillTheRest()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonObject byName = await CallAsync("SetMood", """{"mood": "Angry"}""", null, cancellation);
        JsonObject byNumber = await CallAsync("SetMood", """{"mood": 2, "times": 3, "note": "zz"}""", null, cancellation);

        Assert.Equal("Angry x2 (calm)", byName["value"]!.GetValue<string>());
        Assert.Equal("Sleepy x3 (zz)", byNumber["value"]!.GetValue<string>());
    }

    [Theory(Timeout = GameToolTestTimeoutMs)]
    [InlineData("Nope")]
    [InlineData("PlayStep")]
    [InlineData("Mirror")]
    [InlineData("heal")]
    public async Task ANameNoMarkGivesIsRefusedNamingCsCall(string name)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        McpException refused = await Assert.ThrowsAsync<McpException>(() => CallAsync(name, null, null, cancellation));

        Assert.Equal(
            HelperRefused
                + $"No game tool is named '{name}'; list_game_tools lists the game's tools, and cs_call reaches a "
                + "member that has no mark.",
            refused.Message
        );
    }

    [Theory(Timeout = GameToolTestTimeoutMs)]
    [InlineData("""{"amt": 1}""", "Heal has no parameter 'amt'; it takes amount.")]
    [InlineData("{}", "Heal needs 'amount' (int); list_game_tools shows its schema.")]
    [InlineData("""{"amount": "six"}""", "parameter 'amount': expected an int, got \"six\"")]
    public async Task ArgumentsThatDoNotBindAreRefusedBeforeTheToolRuns(string args, string message)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        McpException refused = await Assert.ThrowsAsync<McpException>(() => CallAsync("Heal", args, null, cancellation));

        Assert.StartsWith("call_game_tool failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains(message, refused.Message, StringComparison.Ordinal);
    }

    [Theory(Timeout = GameToolTestTimeoutMs)]
    [InlineData("Blank")]
    [InlineData("TryFind")]
    [InlineData("Twin")]
    [InlineData("Wander")]
    public async Task AnUnavailableToolIsRefusedWithTheListingsReason(string name)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string[] reasons =
        [
            .. Tools(await ListAsync(new GameToolsOptions(Name: name), cancellation))
                .Where(tool => tool["name"]!.GetValue<string>() == name)
                .Select(tool => HelperRefused + tool["reason"]!.GetValue<string>()),
        ];

        McpException refused = await Assert.ThrowsAsync<McpException>(() => CallAsync(name, null, null, cancellation));

        Assert.NotEmpty(reasons);
        Assert.Contains(refused.Message, reasons);
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task ARebuiltGameStillRunsItsToolsAndSaysItsBuildIsStale()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject? heal = null;
        JsonObject? fetch = null;

        await StaleBuild.WhileStaleAsync(
            _shared.ProbeDirectory,
            "CsProbe",
            async () =>
            {
                heal = await CallAsync("Heal", """{"amount": 3}""", null, cancellation);
                fetch = await CallAsync("FetchLater", """{"label": "old"}""", null, cancellation);
            },
            cancellation
        );

        Assert.Equal(3, heal!["value"]!.GetValue<int>());
        Assert.Equal("stale", heal["build"]!.GetValue<string>());
        Assert.Equal("later old", fetch!["value"]!.GetValue<string>());
        Assert.Equal("stale", fetch["build"]!.GetValue<string>());
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task ATaskPastItsTimeoutFailsAndKeepsRunning()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            CallAsync("Dawdle", """{"ms": 3000}""", new CallGameToolOptions(TimeoutMs: 200), cancellation)
        );

        Assert.StartsWith("call_game_tool failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("the call did not complete within 200 ms; its Task is still running in the game", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AnErrorTheToolLogsIsAttached()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonObject result = await CallAsync("Complain", null, null, cancellation);

        Assert.Equal(1, result["value"]!.GetValue<int>());
        Assert.Contains("CsTools complained", result["errors"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task ABatchCallsAToolThenWaitsFrames()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(_shared.Sessions);
        services.AddSingleton(_shared.Bridge);
        services.AddMcpServer().WithToolsFromAssembly(typeof(RuntimeTools).Assembly);
        await using ServiceProvider provider = services.BuildServiceProvider();
        McpServerOptions options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        await using var server = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), options, null, provider);
        BatchStep[] steps =
        [
            new(
                Tool: "call_game_tool",
                Args: new JsonObject
                {
                    ["name"] = "Heal",
                    ["args"] = new JsonObject { ["amount"] = 4 },
                }
            ),
            new(Tool: "wait_for", Args: new JsonObject { ["condition"] = new JsonObject { ["frames"] = 2 } }),
        ];

        JsonObject batch = JsonNode.Parse(await _tools.BatchDriveAsync(steps, server, cancellationToken: cancellation))!.AsObject();

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        JsonObject called = batch["steps"]![0]!["result"]!.AsObject();
        Assert.Equal("Heal", called["tool"]!.GetValue<string>());
        Assert.Equal(4, called["value"]!.GetValue<int>());
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AWaitCallsAGameToolByNameWithNamedArgumentsAsItsCountStarts()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        WaitOptions options = new(Call: SetMoodCall("Sleepy"));

        IEnumerable<ContentBlock> blocks = await _tools.WaitForAsync(new WaitCondition(GameMs: 100), null, options, cancellationToken: cancellation);
        JsonObject waited = JsonNode.Parse(Text(blocks))!.AsObject();

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        AssertSetMoodCalled(waited["call"]!.AsObject(), "Sleepy");
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task ACaptureCallsAGameToolAsItsClockStarts()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        string json = await _tools.CaptureFramesAsync([0.05], new CaptureFramesOptions(Call: SetMoodCall("Angry")), cancellationToken: cancellation);
        JsonObject captured = JsonNode.Parse(json)!.AsObject();

        Assert.Single(captured["points"]!.AsArray());
        AssertSetMoodCalled(captured["call"]!.AsObject(), "Angry");
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AWatchCallsAGameToolInItsFirstFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        WatchTracks tracks = new(Expressions: [new WatchExpressionTrack("one", "1")]);

        JsonObject started = JsonNode
            .Parse(await _tools.WatchAsync("start", tracks, null, new WatchOptions(Call: SetMoodCall("Calm")), cancellationToken: cancellation))!
            .AsObject();
        await _tools.WatchAsync("stop", cancellationToken: cancellation);

        AssertSetMoodCalled(started["call"]!.AsObject(), "Calm");
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AGameToolCallWithABadEnumNameFailsWithTheHelpersText()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        WaitOptions options = new(Call: SetMoodCall("Glum"));

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(GameMs: 100), null, options, cancellationToken: cancellation)
        );

        Assert.Contains("expected one of", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AGameToolReturningATaskAnswersPendingWithoutAwaitingIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        WaitOptions options = new(Call: new MethodCall(Tool: "FetchLater", Args: JsonDocument.Parse("""{"label": "soon"}""").RootElement.Clone()));

        IEnumerable<ContentBlock> blocks = await _tools.WaitForAsync(new WaitCondition(Frames: 2), null, options, cancellationToken: cancellation);
        JsonObject call = JsonNode.Parse(Text(blocks))!["call"]!.AsObject();

        Assert.Equal("FetchLater", call["tool"]!.GetValue<string>());
        Assert.True(call["pending"]!.GetValue<bool>(), call.ToJsonString());
        Assert.True(call.ContainsKey("value"), call.ToJsonString());
        Assert.Null(call["value"]);
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AGameToolCallsNumbersComeBackAsTheHelperWroteThem()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonObject zero = await WaitCallAsync(new MethodCall(Tool: "Heal", Args: Json("""{"amount": 0}""")), cancellation);
        JsonObject huge = await WaitCallAsync(new MethodCall(Tool: "Huge"), cancellation);

        Assert.Equal("0", zero["value"]!.ToJsonString());
        Assert.Equal("9007199254740993", huge["value"]!.ToJsonString());
        Assert.Equal("System.Int64", huge["type"]!.GetValue<string>());
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AGameToolThatLogsAnErrorFailsTheWait()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        WaitOptions options = new(Call: new MethodCall(Tool: "Complain"));

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(Frames: 2), null, options, cancellationToken: cancellation)
        );

        Assert.Contains("CsTools complained", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AThenCallsAGameToolInTheMetFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        WaitOptions options = new(Then: new WaitThen(Call: SetMoodCall("Angry")));

        IEnumerable<ContentBlock> blocks = await _tools.WaitForAsync(new WaitCondition(Frames: 2), null, options, cancellationToken: cancellation);
        JsonObject waited = JsonNode.Parse(Text(blocks))!.AsObject();

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        AssertSetMoodCalled(waited["then"]!["call"]!.AsObject(), "Angry");
    }

    [Fact(Timeout = GameToolTestTimeoutMs)]
    public async Task AWaitCallsAGameToolAsItStartsAndAnotherInTheMetFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        WaitOptions options = new(Call: SetMoodCall("Calm"), Then: new WaitThen(Call: new MethodCall(Tool: "Huge")));

        IEnumerable<ContentBlock> blocks = await _tools.WaitForAsync(new WaitCondition(Frames: 2), null, options, cancellationToken: cancellation);
        JsonObject waited = JsonNode.Parse(Text(blocks))!.AsObject();

        AssertSetMoodCalled(waited["call"]!.AsObject(), "Calm");
        JsonObject then = waited["then"]!["call"]!.AsObject();
        Assert.Equal("Huge", then["tool"]!.GetValue<string>());
        Assert.Equal("9007199254740993", then["value"]!.ToJsonString());
    }

    private async Task<JsonObject> WaitCallAsync(MethodCall call, CancellationToken cancellation)
    {
        WaitOptions options = new(Call: call);
        IEnumerable<ContentBlock> blocks = await _tools.WaitForAsync(new WaitCondition(Frames: 1), null, options, cancellationToken: cancellation);
        return JsonNode.Parse(Text(blocks))!["call"]!.AsObject();
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static MethodCall SetMoodCall(string mood) =>
        new(Tool: "SetMood", Args: JsonDocument.Parse($$"""{"mood": "{{mood}}", "times": 3}""").RootElement.Clone());

    private static void AssertSetMoodCalled(JsonObject call, string mood)
    {
        Assert.Equal($"{mood} x3 (calm)", call["value"]!.GetValue<string>());
        Assert.Equal("SetMood", call["tool"]!.GetValue<string>());
        Assert.Equal("System.String", call["type"]!.GetValue<string>());
    }

    private static string Text(IEnumerable<ContentBlock> blocks) => string.Concat(blocks.OfType<TextContentBlock>().Select(block => block.Text));

    private Task<JsonObject> ListAsync(GameToolsOptions? options, CancellationToken cancellation) => ListAsync(_tools, options, cancellation);

    private static async Task<JsonObject> ListAsync(RuntimeTools tools, GameToolsOptions? options, CancellationToken cancellation)
    {
        string json = await tools.ListGameToolsAsync(options, cancellationToken: cancellation);
        return JsonNode.Parse(json)!.AsObject();
    }

    private Task<JsonObject> CallAsync(string name, string? args, CallGameToolOptions? options, CancellationToken cancellation) =>
        CallAsync(_tools, name, args, options, cancellation);

    private static async Task<JsonObject> CallAsync(
        RuntimeTools tools,
        string name,
        string? args,
        CallGameToolOptions? options,
        CancellationToken cancellation
    )
    {
        JsonElement? given = args is null ? null : JsonDocument.Parse(args).RootElement.Clone();
        string json = await tools.CallGameToolAsync(name, given, options, cancellationToken: cancellation);
        return JsonNode.Parse(json)!.AsObject();
    }

    private static void AssertUnowned(JsonObject tool, string reason)
    {
        Assert.False(tool["available"]!.GetValue<bool>(), tool.ToJsonString());
        Assert.True(tool.ContainsKey("on"));
        Assert.Null(tool["on"]);
        Assert.Equal(reason, tool["reason"]!.GetValue<string>());
    }

    private static JsonObject[] Tools(JsonObject result) => [.. result["tools"]!.AsArray().Select(tool => tool!.AsObject())];

    private static string[] Names(JsonObject result) => [.. Tools(result).Select(tool => tool["name"]!.GetValue<string>())];

    private static JsonObject Single(JsonObject[] tools, string name) => Assert.Single(tools, tool => tool["name"]!.GetValue<string>() == name);

    private static void AssertAvailableOn(JsonObject tool, string on)
    {
        Assert.True(tool["available"]!.GetValue<bool>(), tool.ToJsonString());
        Assert.Equal(on, tool["on"]!.GetValue<string>());
        Assert.False(tool.ContainsKey("reason"));
        Assert.Equal("object", tool["args"]!["type"]!.GetValue<string>());
    }
}
