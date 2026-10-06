using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The C# helper against a running game: loaded once into the CsProbe's own GodotSharp and answering its ping, a GDScript
/// project refused before the bridge is asked, cs_members listing the CsProbe's own types, cs_get and cs_set reading
/// and writing their members, cs_call calling their methods and constructors, and run_csharp compiling and running snippets.
/// </summary>
public sealed class CSharpToolTests(SharedCsProbeSession shared) : IClassFixture<SharedCsProbeSession>
{
    private const int CSharpTestTimeoutMs = 150_000;
    private const int PingTimeoutMs = 30_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string ExtensionFileName = "godot_mcp_dotnet.gdextension";

    // What the helper's Core answers for TypeNames.Format(typeof(List<int>)).
    private const string CoreListOfInt = "List<int>";

    private const string TargetsName = "CsTargets";
    private const string TargetsPath = "/root/" + TargetsName;
    private const string PlainNodeName = "PlainNode";

    // The CsProbe scene's root has no children; the text is the same from the C# helper and the GDScript bridge.
    private const string MissingUnderProbe =
        "No node 'CsProbe/Missing' in the running game: a path is read from /root, and /root/CsProbe has no child 'Missing' "
        + "(children: none); get_scene_tree lists the nodes' paths.";

    // Two scene-like owners under the root, UniqueA and UniqueB, each holding a Node Shared saved with a unique name; UniqueA
    // also holds a CsTargets Solo saved with one.
    private const string UniqueOwnersScript =
        "for owner_name in [\"UniqueA\", \"UniqueB\"]:\n\t\t"
        + "var scene := Node.new()\n\t\t"
        + "scene.name = owner_name\n\t\t"
        + "scene_tree.root.add_child(scene)\n\t\t"
        + "var shared := Node.new()\n\t\t"
        + "shared.name = \"Shared\"\n\t\t"
        + "scene.add_child(shared)\n\t\t"
        + "shared.owner = scene\n\t\t"
        + "shared.unique_name_in_owner = true\n\t"
        + "var first: Node = scene_tree.root.get_node(\"UniqueA\")\n\t"
        + "var solo: Node = load(\"res://CsTargets.cs\").new()\n\t"
        + "solo.name = \"Solo\"\n\t"
        + "first.add_child(solo)\n\t"
        + "solo.owner = first\n\t"
        + "solo.unique_name_in_owner = true\n\t"
        + "return true";

    private const string RemoveUniqueOwnersScript =
        "for owner_name in [\"UniqueA\", \"UniqueB\"]:\n\t\t"
        + "var scene: Node = scene_tree.root.get_node_or_null(owner_name)\n\t\t"
        + "if scene != null:\n\t\t\t"
        + "scene_tree.root.remove_child(scene)\n\t\t\t"
        + "scene.queue_free()\n\t"
        + "return true";

    private const string AmbiguousShared =
        "'%Shared' is a unique name in 2 scenes: /root/UniqueA, /root/UniqueB; put its owner's path first, as in /root/UniqueA/%Shared.";

    private readonly SharedCsProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, shared.Bridge);

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task TheHelperLoadsIntoTheGameAndSharesItsGodotSharp()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonNode sceneId = await RunAsync(_tools, "return str(scene_tree.current_scene.get_instance_id())", cancellation);
        string request = new JsonObject { ["op"] = "ping", ["id"] = sceneId.GetValue<string>() }.ToJsonString();

        CSharpReply reply = await PingAsync(_shared.Sessions, request, cancellation);

        JsonNode result = reply.Result!;
        string? helperContext = result["helperContext"]?.GetValue<string>();
        Assert.Equal("CsProbe.CsProbeNode", result["node"]?.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(helperContext), "The helper's load context has no name.");
        Assert.Equal(result["godotSharpContext"]?.GetValue<string>(), helperContext);
        Assert.Equal(CoreListOfInt, result["core"]?.GetValue<string>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task ASecondCallDoesNotLoadTheExtensionAgain()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        const string request = """{"op":"ping"}""";

        await PingAsync(_shared.Sessions, request, cancellation);
        CSharpReply second = await PingAsync(_shared.Sessions, request, cancellation);
        JsonNode extensions = await RunAsync(_tools, "return Array(GDExtensionManager.get_loaded_extensions())", cancellation);

        Assert.False(second.LoadedNow);
        Assert.Single(Helpers(extensions));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGDScriptProjectIsRefusedBeforeTheBridgeIsAsked()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject project = new();
        await using SessionHarness harness = new();
        await harness.Sessions.LaunchAsync(new LaunchRequest(project.Directory, null, [], [], Quiet: true, false, Prepare: true), null, cancellation);
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PingAsync(harness.Sessions, """{"op":"ping"}""", cancellation)
        );
        JsonNode state = await RunAsync(
            tools,
            "return {\"meta\": scene_tree.has_meta(\"godot_mcp_dotnet\"), \"extensions\": Array(GDExtensionManager.get_loaded_extensions())}",
            cancellation
        );

        Assert.Equal("This project has no C# assembly, so the C# tools cannot reach it.", refused.Message);
        Assert.False(state["meta"]!.GetValue<bool>());
        Assert.Empty(Helpers(state["extensions"]!));
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task MembersListBothHitOverloadsAndPrivateFields()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        JsonObject result = await MembersAsync(new CSharpTarget(Node: TargetsPath), null, cancellation);

        string[] signatures = Signatures(result);
        string[] names = Names(result);
        Assert.Equal("CsProbe.CsTargets", result["type"]?.GetValue<string>());
        Assert.Contains("internal string Hit(int amount)", signatures);
        Assert.Contains("internal string Hit(float amount)", signatures);
        Assert.Contains("private Update _last", signatures);
        Assert.DoesNotContain(names, name => name.Contains("k__BackingField", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal));
        Assert.DoesNotContain("InvokeGodotClassMethod", names);
        Assert.DoesNotContain("HasGodotClassMethod", names);
        Assert.DoesNotContain("GetGodotMethodList", names);
        Assert.DoesNotContain("GetParent", names);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task MembersOfAStaticClassByTypeName()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonObject result = await MembersAsync(new CSharpTarget(Type: "CsProbe.Tally"), null, cancellation);

        JsonObject[] members = Members(result);
        Assert.Equal("CsProbe.Tally", result["type"]?.GetValue<string>());
        foreach (string signature in (string[])["static int Count { get; set; }", "static int Bump(int by)"])
        {
            JsonObject member = Assert.Single(members, member => member["signature"]?.GetValue<string>() == signature);
            Assert.True(member["static"]?.GetValue<bool>(), $"{signature} is not reported static.");
        }

        Assert.DoesNotContain(members, member => member["kind"]?.GetValue<string>() == "constructor");
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task NameFiltersByACaseInsensitiveSubstring()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        JsonObject result = await MembersAsync(new CSharpTarget(Node: TargetsName), new MembersOptions(Name: "hit"), cancellation);

        Assert.Equal(["internal string Hit(float amount)", "internal string Hit(int amount)"], Signatures(result).Order(StringComparer.Ordinal));
        Assert.Equal(2, result["total"]?.GetValue<int>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task NonPublicFalseLeavesOutInternalAndPrivateMembers()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        JsonObject result = await MembersAsync(new CSharpTarget(Node: TargetsPath), new MembersOptions(NonPublic: false), cancellation);

        string[] names = Names(result);
        Assert.DoesNotContain("Hit", names);
        Assert.DoesNotContain("_last", names);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task ANodeWithoutACSharpScriptIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddChildAsync(PlainNodeName, "Node.new()", cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            MembersAsync(new CSharpTarget(Node: "/root/" + PlainNodeName), null, cancellation)
        );

        Assert.Contains("/root/PlainNode is a Node with no C# script; describe_class lists its API.", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task AnUnknownTypeIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            MembersAsync(new CSharpTarget(Type: "CsProbe.NoSuchType"), null, cancellation)
        );

        Assert.Contains("No type 'CsProbe.NoSuchType' in the game's assemblies", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task AHandleFromAnotherRunIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        McpException refused = await Assert.ThrowsAsync<McpException>(() => MembersAsync(new CSharpTarget(Handle: "h1.1"), null, cancellation));

        Assert.Contains("handle h1.1 was dropped when the game restarted", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task PagesMembersByOffsetAndLimit()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);
        CSharpTarget target = new(Node: TargetsPath);

        string[] all = Signatures(await MembersAsync(target, null, cancellation));
        JsonObject first = await MembersAsync(target, new MembersOptions(Limit: 2), cancellation);
        JsonObject second = await MembersAsync(target, new MembersOptions(Offset: 2, Limit: 2), cancellation);

        Assert.Equal(all[..2], Signatures(first));
        Assert.True(first["total"]?.GetValue<int>() > 2, $"total is {first["total"]}, not more than 2.");
        Assert.Equal(2, first["next"]?.GetValue<int>());
        Assert.Equal(all[2], Signatures(second)[0]);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task GetReadsAPrivateRecordFieldGodotReturnsNullFor()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        JsonNode? godot = await RunAsync(_tools, $"return scene_tree.root.get_node(\"{TargetsName}\").get(\"_last\")", cancellation);
        JsonObject result = await GetAsync(new CSharpTarget(Node: TargetsPath), "_last", null, cancellation);

        Assert.Null(godot);
        JsonObject value = result["value"]!.AsObject();
        Assert.Equal("CsProbe.Update", result["type"]?.GetValue<string>());
        Assert.Equal("start", value["Label"]?.GetValue<string>());
        Assert.Equal(1, value["At"]?["X"]?.GetValue<int>());
        Assert.Equal(2, value["At"]?["Y"]?.GetValue<int>());
        Assert.Equal([1, 2, 3], value["Values"]!.AsArray().Select(item => item!.GetValue<int>()));
        Assert.Equal("Calm", value["Mood"]?.GetValue<string>());
        Assert.Null(result["handle"]);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunScriptReadingAMemberGodotCannotMarshalPointsToTheCSharpTools()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        McpException failed = await Assert.ThrowsAsync<McpException>(() =>
            RunAsync(_tools, $"var targets = scene_tree.root.get_node(\"{TargetsName}\")\n\treturn targets.Numbers", cancellation)
        );

        Assert.True(failed.Message.Contains("execute returned null and Godot reported errors", StringComparison.Ordinal), failed.Message);
        Assert.True(failed.Message.Contains("cs_get", StringComparison.Ordinal), failed.Message);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task GetFollowsADottedPathIntoAList()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);
        CSharpTarget target = new(Node: TargetsPath);

        JsonObject number = await GetAsync(target, "Numbers[1]", null, cancellation);
        JsonObject value = await GetAsync(target, "_last.Values[2]", null, cancellation);

        Assert.Equal(5, number["value"]?.GetValue<int>());
        Assert.Equal("System.Int32", number["type"]?.GetValue<string>());
        Assert.Equal(3, value["value"]?.GetValue<int>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task SetConvertsAnEnumByNameAndReadsBack()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);
        CSharpTarget target = new(Node: TargetsPath);

        try
        {
            JsonObject set = await SetAsync(target, "Mood", "Angry", cancellation);
            JsonObject read = await GetAsync(target, "Mood", null, cancellation);

            Assert.Equal("Mood", set["member"]?.GetValue<string>());
            Assert.Equal("Calm", set["before"]?.GetValue<string>());
            Assert.Equal("Angry", set["after"]?.GetValue<string>());
            Assert.Equal("Angry", read["value"]?.GetValue<string>());
            Assert.Equal("CsProbe.Mood", read["type"]?.GetValue<string>());
        }
        finally
        {
            await SetAsync(target, "Mood", "Calm", cancellation);
        }
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task KeepReturnsAHandleUsableAsATarget()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        JsonObject kept = await GetAsync(new CSharpTarget(Node: TargetsPath), "_last", new GetOptions(Keep: true), cancellation);
        string handle = kept["handle"]!.GetValue<string>();
        JsonObject label = await GetAsync(new CSharpTarget(Handle: handle), "Label", null, cancellation);

        Assert.Null(kept["warning"]);
        Assert.Equal("start", label["value"]?.GetValue<string>());
        Assert.Equal("System.String", label["type"]?.GetValue<string>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task GetReadsAStaticByTypeName()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CSharpTarget tally = new(Type: "CsProbe.Tally");
        JsonObject first = await GetAsync(tally, "Count", null, cancellation);
        int before = first["value"]!.GetValue<int>();

        try
        {
            JsonObject set = await SetAsync(tally, "Count", before + 3, cancellation);
            JsonObject read = await GetAsync(tally, "Count", null, cancellation);

            Assert.Equal("System.Int32", first["type"]?.GetValue<string>());
            Assert.Equal(before + 3, set["after"]?.GetValue<int>());
            Assert.Equal(before + 3, read["value"]?.GetValue<int>());
        }
        finally
        {
            await SetAsync(tally, "Count", before, cancellation);
        }
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task GetOfAMethodNameSaysCsCall()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => GetAsync(new CSharpTarget(Node: TargetsPath), "Hit", null, cancellation));

        Assert.StartsWith("cs_get failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("'Hit' is a method of CsTargets; cs_call calls it", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task SetThatDoesNotTakeIsPutBack()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);
        CSharpTarget target = new(Node: TargetsPath);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => SetAsync(target, "Clamped", 50, cancellation));
        JsonObject read = await GetAsync(target, "Clamped", null, cancellation);

        Assert.StartsWith("cs_set failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains(
            "'Clamped' did not take the value: it read 10 after the set, so it was put back to 5.",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(5, read["value"]?.GetValue<int>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task KeepWarnsOnANullAndOnAValueType()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);
        CSharpTarget target = new(Node: TargetsPath);

        JsonObject nothing = await GetAsync(target, "Greeter", new GetOptions(Keep: true), cancellation);
        JsonObject mood = await GetAsync(target, "Mood", new GetOptions(Keep: true), cancellation);

        Assert.Null(nothing["value"]);
        Assert.Null(nothing["handle"]);
        Assert.Equal("CsProbe.IGreeter", nothing["type"]?.GetValue<string>());
        Assert.Equal("the value is null; nothing to keep", nothing["warning"]?.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(mood["handle"]?.GetValue<string>()), "A value type's keep returned no handle.");
        Assert.StartsWith("Mood is a value type: the handle holds a copy", mood["warning"]?.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task SetTakesAHandleIntoAnInterfaceMember()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);
        CSharpTarget target = new(Node: TargetsPath);
        JsonObject friendly = await GetAsync(target, "Friendly", new GetOptions(Keep: true), cancellation);
        JsonObject handle = new() { ["$handle"] = friendly["handle"]!.GetValue<string>() };

        try
        {
            JsonObject set = await SetAsync(target, "Greeter", handle, cancellation);
            JsonObject read = await GetAsync(target, "Greeter", null, cancellation);

            Assert.Null(set["before"]);
            Assert.Equal("CsProbe.Greeter", read["type"]?.GetValue<string>());
        }
        finally
        {
            await SetAsync<JsonNode?>(target, "Greeter", null, cancellation);
        }
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task SetRefusesAKeyAGodotDictionaryLacks()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);
        CSharpTarget target = new(Node: TargetsPath);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => SetAsync(target, "Scores[\"bob\"]", 2, cancellation));
        JsonObject alice = await GetAsync(target, "Scores[\"alice\"]", null, cancellation);

        Assert.Contains("key \"bob\" is not in the dictionary at Scores[\"bob\"]", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, alice["value"]?.GetValue<int>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task CallPicksHitFloatBySignature()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        JsonObject result = await CallAsync(new CSharpTarget(Node: TargetsPath), "Hit", [2], new CsCallOptions(Signature: ["float"]), cancellation);

        Assert.Equal("float 2", result["value"]?.GetValue<string>());
        Assert.Equal("System.String", result["type"]?.GetValue<string>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task AnAmbiguousCallListsEachSignature()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            CallAsync(new CSharpTarget(Node: TargetsPath), "Hit", [2], null, cancellation)
        );

        Assert.StartsWith("cs_call failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("overloads of 'Hit' take these arguments; pass options.signature:", refused.Message, StringComparison.Ordinal);
        Assert.Contains("internal string Hit(int amount) — options.signature [\"int\"]", refused.Message, StringComparison.Ordinal);
        Assert.Contains("internal string Hit(float amount) — options.signature [\"float\"]", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task CallGenericWithTypeArgs()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);
        CSharpTarget target = new(Node: TargetsPath);
        JsonObject point = new() { ["X"] = 3, ["Y"] = 4 };

        JsonObject number = await CallAsync(target, "Echo", [7], new CsCallOptions(TypeArgs: ["int"]), cancellation);
        JsonObject record = await CallAsync(target, "Echo", [point], new CsCallOptions(TypeArgs: ["CsProbe.Point2"]), cancellation);

        Assert.Equal(7, number["value"]?.GetValue<int>());
        Assert.Equal("System.Int32", number["type"]?.GetValue<string>());
        Assert.Equal(3, record["value"]?["X"]?.GetValue<int>());
        Assert.Equal(4, record["value"]?["Y"]?.GetValue<int>());
        Assert.Equal("CsProbe.Point2", record["type"]?.GetValue<string>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task CallAwaitsAsyncTaskInt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        JsonObject result = await CallAsync(new CSharpTarget(Node: TargetsPath), "CountLaterAsync", [21], null, cancellation);

        Assert.Equal(42, result["value"]?.GetValue<int>());
        Assert.Equal("System.Int32", result["type"]?.GetValue<string>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task ACallPastItsTimeoutSaysTheTaskStillRuns()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            CallAsync(new CSharpTarget(Node: TargetsPath), "SlowAsync", [2000], new CsCallOptions(TimeoutMs: 200), cancellation)
        );

        Assert.StartsWith("cs_call failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("the call did not complete within 200 ms; its Task is still running in the game", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task CallPassesAHandleToAnInterfaceParameter()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        JsonObject greeter = await CallAsync(new CSharpTarget(Type: "CsProbe.Greeter"), ".ctor", null, new CsCallOptions(Keep: true), cancellation);
        JsonObject handle = new() { ["$handle"] = greeter["handle"]!.GetValue<string>() };
        JsonObject result = await CallAsync(new CSharpTarget(Node: TargetsPath), "GreetWith", [handle, "bob"], null, cancellation);

        Assert.Equal("CsProbe.Greeter", greeter["type"]?.GetValue<string>());
        Assert.Equal("hello bob", result["value"]?.GetValue<string>());

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            CallAsync(new CSharpTarget(Node: TargetsPath), "GreetWith", [new JsonObject { ["$node"] = "CsProbe/Missing" }, "bob"], null, cancellation)
        );

        Assert.Contains(MissingUnderProbe, refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task CallReportsTheThrownException()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            CallAsync(new CSharpTarget(Node: TargetsPath), "Fail", null, null, cancellation)
        );

        Assert.StartsWith("cs_call failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: probe failure", refused.Message, StringComparison.Ordinal);
        Assert.Contains("at CsProbe.CsTargets.Fail()", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task AStaticMethodByTypeName()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CSharpTarget tally = new(Type: "CsProbe.Tally");
        int before = (await GetAsync(tally, "Count", null, cancellation))["value"]!.GetValue<int>();

        try
        {
            JsonObject result = await CallAsync(tally, "Bump", [3], null, cancellation);

            Assert.Equal(before + 3, result["value"]?.GetValue<int>());
            Assert.Equal("System.Int32", result["type"]?.GetValue<string>());
        }
        finally
        {
            await SetAsync(tally, "Count", before, cancellation);
        }
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task AGodotMethodPointsToCallMethod()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            CallAsync(new CSharpTarget(Node: TargetsPath), "GetChildCount", null, null, cancellation)
        );

        Assert.StartsWith("cs_call failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("'GetChildCount' is a Godot method; call_method calls it", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task ANodeConstructedWithoutKeepIsFreed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonObject result = await CallAsync(new CSharpTarget(Type: "CsProbe.CsTargets"), ".ctor", null, null, cancellation);
        ulong id = result["value"]!["id"]!.GetValue<ulong>();
        JsonNode valid = await RunAsync(_tools, $"return is_instance_id_valid({id})", cancellation);

        Assert.Equal("CsProbe.CsTargets", result["type"]?.GetValue<string>());
        Assert.Null(result["handle"]);
        Assert.False(valid.GetValue<bool>(), $"The constructed node {id} was not freed.");
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task CallReportsAFaultedAwaitedTask()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            CallAsync(new CSharpTarget(Node: TargetsPath), "FailLaterAsync", null, null, cancellation)
        );

        Assert.StartsWith("cs_call failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("FailLaterAsync threw InvalidOperationException: late failure", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task CallReturnsOutsForAnOutParameter()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        JsonObject result = await CallAsync(new CSharpTarget(Node: TargetsPath), "TryOpen", [3, null], null, cancellation);

        Assert.True(result["value"]?.GetValue<bool>());
        Assert.Equal("System.Boolean", result["type"]?.GetValue<string>());
        Assert.Equal("door 3", result["outs"]?["label"]?.GetValue<string>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task AVoidCallAnswersSystemVoid()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        JsonObject result = await CallAsync(new CSharpTarget(Node: TargetsPath), "Nothing", null, null, cancellation);

        Assert.True(result.ContainsKey("value"), "A void call's answer has no value key.");
        Assert.Null(result["value"]);
        Assert.Equal("System.Void", result["type"]?.GetValue<string>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task ACompletedValueTaskAnswersAtOnce()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        JsonObject result = await CallAsync(new CSharpTarget(Node: TargetsPath), "SoonAsync", [4], new CsCallOptions(TimeoutMs: 1), cancellation);

        Assert.Equal(5, result["value"]?.GetValue<int>());
        Assert.Equal("System.Int32", result["type"]?.GetValue<string>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task AKeptConstructedNodeWarnsItIsOutsideTheTree()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonObject result = await CallAsync(new CSharpTarget(Type: "CsProbe.CsTargets"), ".ctor", null, new CsCallOptions(Keep: true), cancellation);
        ulong id = result["value"]!["id"]!.GetValue<ulong>();
        McpException free = await Assert.ThrowsAsync<McpException>(() =>
            CallAsync(new CSharpTarget(Handle: result["handle"]!.GetValue<string>()), "Free", null, null, cancellation)
        );
        JsonNode freed = await RunAsync(_tools, $"instance_from_id({id}).free()\n\treturn is_instance_id_valid({id})", cancellation);

        Assert.Equal("CsTargets is a node outside the tree: free it or add it as a child", result["warning"]?.GetValue<string>());
        Assert.Contains("'Free' is a Godot method; call_method calls it", free.Message, StringComparison.Ordinal);
        Assert.False(freed.GetValue<bool>(), $"The kept node {id} was not freed.");
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task PollAndForgetOfAnUnknownIdAreRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        InvalidOperationException poll = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PingAsync(_shared.Sessions, """{"op":"poll","id":"c999999"}""", cancellation)
        );
        InvalidOperationException forget = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PingAsync(_shared.Sessions, """{"op":"forget","id":"c999999"}""", cancellation)
        );

        string refusal = "The C# helper refused the request: No pending call 'c999999': its reply was given, or it was forgotten.";
        Assert.Equal(refusal, poll.Message);
        Assert.Equal(refusal, forget.Message);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunCSharpCallsBindWithAPlainRecord()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);
        const string code =
            "var t = Node<CsTargets>(\"CsTargets\"); "
            + "var u = new Update(\"hand\", new Point2(1, 2), ImmutableArray.Create(3, 4), Mood.Calm); "
            + "return t.Bind(u) + \"/\" + Call(t, \"Rebind\", u);";

        JsonObject result = await RunCSharpAsync(code, new RunCSharpOptions(Usings: ["System.Collections.Immutable"]), cancellation);

        Assert.Equal("hand@1,2/2", result["value"]?.GetValue<string>());
        Assert.Equal("System.String", result["type"]?.GetValue<string>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunCSharpAwaits()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);
        // The frame count moving while the snippet ran proves the helper answered pending: the game's main thread drew frames
        // between the call and the result.
        const string code =
            "var t = Node<CsTargets>(\"CsTargets\"); ulong before = Engine.GetProcessFrames(); await Task.Delay(50); "
            + "int n = await (Task<int>)Call(t, \"CountLaterAsync\", 21)!; return n + \"@\" + (Engine.GetProcessFrames() > before);";

        JsonObject result = await RunCSharpAsync(code, null, cancellation);

        Assert.Equal("42@True", result["value"]?.GetValue<string>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunCSharpAwaitsAProcessFrameWithToSignal()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        const string code =
            "var before = Engine.GetProcessFrames(); await ToSignal(Tree, SceneTree.SignalName.ProcessFrame); "
            + "return Engine.GetProcessFrames() > before;";

        JsonObject result = await RunCSharpAsync(code, null, cancellation);

        Assert.True(result["value"]?.GetValue<bool>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunCSharpAwaitsAProcessFrameWhileThePausedGameStillDraws()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        const string code =
            "var before = Engine.GetProcessFrames(); await ToSignal(Tree, SceneTree.SignalName.ProcessFrame); "
            + "return Engine.GetProcessFrames() > before;";

        await _tools.FrameControlAsync("pause", cancellationToken: cancellation);
        try
        {
            JsonObject result = await RunCSharpAsync(code, null, cancellation);

            Assert.True(result["value"]?.GetValue<bool>());
        }
        finally
        {
            await _tools.FrameControlAsync("resume", cancellationToken: cancellation);
        }
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunCSharpCompileErrorNamesTheLine()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        McpException refused = await Assert.ThrowsAsync<McpException>(() => RunCSharpAsync("var a = 1;\nreturn b;", null, cancellation));

        Assert.StartsWith("run_csharp failed: the snippet does not compile:", refused.Message, StringComparison.Ordinal);
        Assert.Contains("snippet(2,", refused.Message, StringComparison.Ordinal);
        Assert.Contains("error CS0103", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunCSharpRefusesAStaleBuild()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        await StaleBuild.WhileStaleAsync(
            _shared.ProbeDirectory,
            "CsProbe",
            async () =>
            {
                McpException refused = await Assert.ThrowsAsync<McpException>(() => RunCSharpAsync("1 + 1", null, cancellation));

                Assert.Contains("older build of CsProbe.dll", refused.Message, StringComparison.Ordinal);
                Assert.Contains("restart_project", refused.Message, StringComparison.Ordinal);
            },
            cancellation
        );
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunCSharpReportsTheThrownException()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            RunCSharpAsync("throw new InvalidOperationException(\"boom\");", null, cancellation)
        );

        string[] lines = refused.Message.Split('\n');
        Assert.StartsWith("run_csharp failed: ", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("the snippet threw InvalidOperationException: boom", lines[0], StringComparison.Ordinal);
        Assert.Contains("in snippet:line 1", lines[1], StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunCSharpBindsToTheGamesRuntime()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        // Proves the snippet binds to the game's own runtime: compiled against another framework, five paths and a params join
        // can bind to params-span overloads the game's runtime lacks, which fail in the game with MissingMethodException.
        const string code = "return System.IO.Path.Combine(\"a\", \"b\", \"c\", \"d\", \"e\") + \"|\" + string.Join(\"-\", \"x\", \"y\");";

        JsonObject result = await RunCSharpAsync(code, null, cancellation);

        Assert.Equal(Path.Combine("a", "b", "c", "d", "e") + "|x-y", result["value"]?.GetValue<string>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunCSharpGetsAndSetsPrivateAndStaticMembers()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);
        const string code =
            "var t = Node<CsTargets>(\"CsTargets\"); int clamped = Get<int>(t, \"_clamped\"); Set(t, \"_clamped\", 70); "
            + "int seen = Get<int>(t, \"_clamped\"); Set(t, \"_clamped\", clamped); "
            + "int count = Get<int>(typeof(Tally), \"Count\"); Set(typeof(Tally), \"Count\", count + 5); "
            + "int after = (int)Get(typeof(Tally), \"Count\")!; Set(typeof(Tally), \"Count\", count); "
            + "return seen + \"/\" + (after - count);";

        JsonObject result = await RunCSharpAsync(code, null, cancellation);

        // 70 is past the Clamped setter's range: the private field was written directly.
        Assert.Equal("70/5", result["value"]?.GetValue<string>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunCSharpSharesHandlesWithTheOtherCSharpTools()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);
        JsonObject kept = await GetAsync(new CSharpTarget(Node: TargetsPath), "Friendly", new GetOptions(Keep: true), cancellation);
        string greeter = kept["handle"]!.GetValue<string>();

        JsonObject typed = await RunCSharpAsync($"return Handle<Greeter>(\"{greeter}\").GetType().FullName;", null, cancellation);
        JsonObject made = await RunCSharpAsync("return Keep(new Point2(3, 4));", null, cancellation);
        JsonObject read = await GetAsync(new CSharpTarget(Handle: made["value"]!.GetValue<string>()), "X", null, cancellation);

        Assert.Equal("CsProbe.Greeter", typed["value"]?.GetValue<string>());
        Assert.Equal(3, read["value"]?.GetValue<int>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunCSharpPastItsTimeoutFailsAndTheNextRunSucceeds()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            RunCSharpAsync("await Task.Delay(2000); return 1;", new RunCSharpOptions(TimeoutMs: 200), cancellation)
        );
        JsonObject next = await RunCSharpAsync("1 + 1", null, cancellation);

        Assert.StartsWith("run_csharp failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("the call did not complete within 200 ms; its Task is still running in the game", refused.Message, StringComparison.Ordinal);
        Assert.Equal(2, next["value"]?.GetValue<int>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunCSharpPastItsTimeoutCancelsItsToken()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        const string code =
            "try { await Task.Delay(10000, Cancellation); } "
            + "catch (OperationCanceledException) { Tree.Root.SetMeta(\"gm_cancelled\", true); } return 1;";

        McpException refused = await Assert.ThrowsAsync<McpException>(() => RunCSharpAsync(code, new RunCSharpOptions(TimeoutMs: 200), cancellation));
        Assert.StartsWith("run_csharp failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("the call did not complete within 200 ms; its Task is still running in the game", refused.Message, StringComparison.Ordinal);

        bool cancelled = await WaitsForTheCancelledSnippet(cancellation);
        await RunCSharpAsync("Tree.Root.RemoveMeta(\"gm_cancelled\"); return 0;", null, cancellation);

        Assert.True(cancelled, "The snippet's Cancellation was not cancelled within 5 s of its call timing out.");
    }

    /// <summary>Whether the timed-out snippet's catch block ran within 5 s, well inside the 10 s delay it was awaiting.</summary>
    private async Task<bool> WaitsForTheCancelledSnippet(CancellationToken cancellation)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        bool cancelled = false;
        while (!cancelled && DateTime.UtcNow < deadline)
        {
            JsonObject probe = await RunCSharpAsync("return Tree.Root.HasMeta(\"gm_cancelled\");", null, cancellation);
            cancelled = probe["value"]?.GetValue<bool>() ?? false;
            if (!cancelled)
            {
                await Task.Delay(100, cancellation);
            }
        }

        return cancelled;
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunCSharpCallsAnInternalMethodDirectly()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);

        JsonObject result = await RunCSharpAsync("Node<CsTargets>(\"" + TargetsName + "\").Hit(2)", null, cancellation);

        Assert.Equal("int 2", result["value"]?.GetValue<string>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task RunCSharpNodeNotFoundNamesTheBase()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        McpException refused = await Assert.ThrowsAsync<McpException>(() => RunCSharpAsync("Node(\"CsProbe/Missing\")", null, cancellation));

        Assert.Contains("the snippet threw InvalidOperationException: " + MissingUnderProbe, refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task CsGetOfAMissingNodeNamesTheBaseAsInspectNodeDoes()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            GetAsync(new CSharpTarget(Node: "CsProbe/Missing"), "Numbers", null, cancellation)
        );
        McpException inspected = await Assert.ThrowsAsync<McpException>(() =>
            _tools.InspectNodeAsync("CsProbe/Missing", null, cancellationToken: cancellation)
        );

        Assert.Contains(MissingUnderProbe, refused.Message, StringComparison.Ordinal);
        Assert.Contains(MissingUnderProbe, inspected.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task AUniqueNameReachesItsNodeInCSharpAndIsRefusedWhenSeveralScenesHoldIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(_tools, UniqueOwnersScript, cancellation);
        try
        {
            JsonObject read = await GetAsync(new CSharpTarget(Node: "%Solo"), "_last", null, cancellation);
            JsonObject ran = await RunCSharpAsync("return Node(\"%Solo\").GetPath().ToString();", null, cancellation);
            McpException got = await Assert.ThrowsAsync<McpException>(() => GetAsync(new CSharpTarget(Node: "%Shared"), "_last", null, cancellation));
            McpException snippet = await Assert.ThrowsAsync<McpException>(() => RunCSharpAsync("Node(\"%Shared\")", null, cancellation));

            Assert.Equal("start", read["value"]?["Label"]?.GetValue<string>());
            Assert.Equal("/root/UniqueA/Solo", ran["value"]?.GetValue<string>());
            Assert.Contains(AmbiguousShared, got.Message, StringComparison.Ordinal);
            Assert.Contains("the snippet threw InvalidOperationException: " + AmbiguousShared, snippet.Message, StringComparison.Ordinal);
        }
        finally
        {
            await RunAsync(_tools, RemoveUniqueOwnersScript, cancellation);
        }
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task APathAndABareNameFindTheSameNodeInCSharpAndGDScript()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);
        const string code =
            "var byName = Node(\""
            + TargetsName
            + "\"); var byPath = Node(\""
            + TargetsPath
            + "\"); "
            + "return byName.GetPath() + \"|\" + ReferenceEquals(byName, byPath);";

        JsonObject csharp = await RunCSharpAsync(code, null, cancellation);
        JsonNode byName = JsonNode.Parse(await _tools.InspectNodeAsync(TargetsName, ["name"], cancellationToken: cancellation))!;
        JsonNode byPath = JsonNode.Parse(await _tools.InspectNodeAsync(TargetsPath, ["name"], cancellationToken: cancellation))!;

        Assert.Equal(TargetsPath + "|True", csharp["value"]?.GetValue<string>());
        Assert.Equal(TargetsPath, byName["path"]?.GetValue<string>());
        Assert.Equal(TargetsPath, byPath["path"]?.GetValue<string>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task InspectWaitWatchAndSetReachAPrivateFieldGodotDoesNotList()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTargetsAsync(cancellation);
        using var five = JsonDocument.Parse("5");
        using var seven = JsonDocument.Parse("7");

        JsonObject inspected = JsonNode.Parse(await _tools.InspectNodeAsync(TargetsPath, ["_clamped"], cancellationToken: cancellation))!.AsObject();
        JsonObject waited = JsonNode
            .Parse(
                Text(
                    await _tools.WaitForAsync(
                        new WaitCondition(Node: TargetsPath, Property: "_clamped", EqualsValue: five.RootElement),
                        0,
                        cancellationToken: cancellation
                    )
                )
            )!
            .AsObject();
        JsonObject watched = JsonNode
            .Parse(
                await _tools.WatchAsync(
                    "run",
                    new WatchTracks([new WatchPropertyTrack(TargetsPath, "_clamped")]),
                    new WatchWindow(Frames: 2),
                    cancellationToken: cancellation
                )
            )!
            .AsObject();
        // A method is what `in` also finds on the node, and it is still not a property.
        McpException method = await Assert.ThrowsAsync<McpException>(() =>
            _tools.InspectNodeAsync(TargetsPath, ["Hit"], cancellationToken: cancellation)
        );

        Assert.Equal(5, inspected["properties"]?["_clamped"]?.GetValue<int>());
        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal(5, waited["value"]?.GetValue<int>());
        Assert.Equal(5, watched["tracks"]![0]!["first"]?.GetValue<double>());
        Assert.Contains("has no property 'Hit'", method.Message, StringComparison.Ordinal);

        try
        {
            JsonObject set = JsonNode
                .Parse(await _tools.SetPropertyAsync(TargetsPath, "_clamped", seven.RootElement, cancellationToken: cancellation))!
                .AsObject();

            Assert.Equal(7, set["after"]?.GetValue<int>());
        }
        finally
        {
            await _tools.SetPropertyAsync(TargetsPath, "_clamped", five.RootElement, cancellationToken: cancellation);
        }
    }

    private async Task<JsonObject> RunCSharpAsync(string code, RunCSharpOptions? options, CancellationToken cancellation)
    {
        string json = await _tools.RunCSharpAsync(code, options, cancellationToken: cancellation);
        return JsonNode.Parse(json)!.AsObject();
    }

    private async Task<JsonObject> CallAsync(
        CSharpTarget target,
        string member,
        object?[]? args,
        CsCallOptions? options,
        CancellationToken cancellation
    )
    {
        JsonElement[]? elements = args is null ? null : [.. args.Select(arg => JsonSerializer.SerializeToElement(arg))];
        string json = await _tools.CsCallAsync(target, member, elements, options, cancellationToken: cancellation);
        return JsonNode.Parse(json)!.AsObject();
    }

    private async Task<JsonObject> GetAsync(CSharpTarget target, string member, GetOptions? options, CancellationToken cancellation)
    {
        string json = await _tools.CsGetAsync(target, member, options, cancellationToken: cancellation);
        return JsonNode.Parse(json)!.AsObject();
    }

    private async Task<JsonObject> SetAsync<T>(CSharpTarget target, string member, T value, CancellationToken cancellation)
    {
        string json = await _tools.CsSetAsync(target, member, JsonSerializer.SerializeToElement(value), cancellationToken: cancellation);
        return JsonNode.Parse(json)!.AsObject();
    }

    private async Task<JsonObject> MembersAsync(CSharpTarget target, MembersOptions? options, CancellationToken cancellation)
    {
        string json = await _tools.CsMembersAsync(target, options, cancellationToken: cancellation);
        return JsonNode.Parse(json)!.AsObject();
    }

    private static JsonObject[] Members(JsonObject result) => [.. result["members"]!.AsArray().Select(member => member!.AsObject())];

    private static string[] Signatures(JsonObject result) => [.. Members(result).Select(member => member["signature"]!.GetValue<string>())];

    private static string[] Names(JsonObject result) => [.. Members(result).Select(member => member["name"]!.GetValue<string>())];

    /// <summary>Adds CsProbe's <c>CsTargets</c> node, which no scene holds, under the root, once per game.</summary>
    private Task AddTargetsAsync(CancellationToken cancellation) => AddChildAsync(TargetsName, "load(\"res://CsTargets.cs\").new()", cancellation);

    /// <summary>Adds a child named <paramref name="name"/> under the root unless the game already holds one.</summary>
    private async Task AddChildAsync(string name, string construct, CancellationToken cancellation)
    {
        string body =
            $"if not scene_tree.root.has_node(\"{name}\"):\n\t\tvar child = {construct}\n\t\tchild.name = \"{name}\"\n"
            + "\t\tscene_tree.root.add_child(child)\n\treturn true";
        await RunAsync(_tools, body, cancellation);
    }

    private Task<CSharpReply> PingAsync(SessionRegistry sessions, string request, CancellationToken cancellation) =>
        _shared.Bridge.SendAsync(sessions.Resolve(null), request, PingTimeoutMs, null, cancellation);

    private static string[] Helpers(JsonNode extensions) =>
        [.. extensions.AsArray().Select(path => path!.GetValue<string>()).Where(path => path.EndsWith(ExtensionFileName, StringComparison.Ordinal))];

    private static string Text(IEnumerable<ContentBlock> blocks) => string.Concat(blocks.OfType<TextContentBlock>().Select(block => block.Text));

    private static async Task<JsonNode> RunAsync(RuntimeTools tools, string body, CancellationToken cancellation)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation);
        return JsonNode.Parse(json)!["value"]!;
    }
}
