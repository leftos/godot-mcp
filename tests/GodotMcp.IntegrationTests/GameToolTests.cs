using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// list_game_tools against the CsProbe game with CsTools.cs as its Tools autoload, which marks methods on that autoload and
/// its base type, on static classes and on a type nothing owns, and whose scene root marks one: each owner's on, the schema of
/// a method with an enum and defaults, the reasons a tool is unavailable, paging and the name filter; on launches of their
/// own, owners read again after the tree changes and a game that marks nothing; and a GDScript project refused before the
/// game is asked.
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
        "FetchLater",
        "Greet",
        "Heal",
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
        JsonObject[] before = Tools(await ListAsync(tools, null, cancellation));
        string script =
            "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\tscene_tree.root.get_node(\"Tools\").free()\n"
            + "\tscene_tree.current_scene.free()\n\treturn scene_tree.current_scene == null\n";
        JsonNode freed = JsonNode.Parse(await tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation))!["value"]!;
        JsonObject[] after = Tools(await ListAsync(tools, null, cancellation));

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
        string script =
            "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\treturn scene_tree.has_meta(\"godot_mcp_dotnet\")\n";
        JsonNode loaded = JsonNode.Parse(await tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation))!["value"]!;

        Assert.Equal(
            "list_game_tools failed: This project has no C# assembly, so it has no game tools, which are C# methods marked "
                + "[GodotMcpTool]; call_method calls a GDScript game's own methods.",
            refused.Message
        );
        Assert.False(loaded.GetValue<bool>());
    }

    private Task<JsonObject> ListAsync(GameToolsOptions? options, CancellationToken cancellation) => ListAsync(_tools, options, cancellation);

    private static async Task<JsonObject> ListAsync(RuntimeTools tools, GameToolsOptions? options, CancellationToken cancellation)
    {
        string json = await tools.ListGameToolsAsync(options, cancellationToken: cancellation);
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
