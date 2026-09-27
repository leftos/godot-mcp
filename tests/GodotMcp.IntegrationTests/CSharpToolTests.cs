using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The C# helper against a running game: loaded once into the CsProbe's own GodotSharp and answering its ping, and a
/// GDScript project refused before the bridge is asked.
/// </summary>
public sealed class CSharpToolTests(SharedCsProbeSession shared) : IClassFixture<SharedCsProbeSession>
{
    private const int CSharpTestTimeoutMs = 150_000;
    private const int PingTimeoutMs = 30_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string ExtensionFileName = "godot_mcp_dotnet.gdextension";

    // What the helper's Core answers for TypeNames.Format(typeof(List<int>)).
    private const string CoreListOfInt = "List<int>";

    private readonly SharedCsProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions);

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
        RuntimeTools tools = new(harness.Sessions);

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
