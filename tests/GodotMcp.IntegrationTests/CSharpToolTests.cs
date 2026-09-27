using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The C# helper against a running game: loaded once into the CsProbe's own GodotSharp and answering its ping, a GDScript
/// project refused before the bridge is asked, and cs_members listing the CsProbe's own types.
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
        _shared.Bridge.SendAsync(sessions.Resolve(null), request, PingTimeoutMs, cancellation);

    private static string[] Helpers(JsonNode extensions) =>
        [.. extensions.AsArray().Select(path => path!.GetValue<string>()).Where(path => path.EndsWith(ExtensionFileName, StringComparison.Ordinal))];

    private static async Task<JsonNode> RunAsync(RuntimeTools tools, string body, CancellationToken cancellation)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation);
        return JsonNode.Parse(json)!["value"]!;
    }
}
