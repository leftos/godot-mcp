using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The C# helper against a running game: loaded once into the CsProbe's own GodotSharp and answering its ping, a GDScript
/// project refused before the bridge is asked, cs_members listing the CsProbe's own types, cs_get and cs_set reading
/// and writing their members, and cs_call calling their methods and constructors.
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

    [Fact(Timeout = CSharpTestTimeoutMs)]
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

    private static async Task<JsonNode> RunAsync(RuntimeTools tools, string body, CancellationToken cancellation)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation);
        return JsonNode.Parse(json)!["value"]!;
    }
}
