using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Tests.Tools;

/// <summary>batch_scene_operations' checks of its steps, which refuse the whole batch before a headless Godot starts; no Godot runs here.</summary>
public sealed class HeadlessBatchValidationTests : IDisposable
{
    private const string Nine =
        "delete_nodes, attach_script, duplicate_node, load_sprite, add_node, set_node_properties, connect_signal, disconnect_signal, "
        + "export_mesh_library";

    private readonly TempDirectory _temp = new();
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly HeadlessTools _tools;
    private readonly string _project;

    public HeadlessBatchValidationTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        _tools = new HeadlessTools(_sessions);
        _project = _temp.Combine("game");
        Directory.CreateDirectory(_project);
        File.WriteAllText(Path.Combine(_project, "project.godot"), "config_version=5\n");
        File.WriteAllText(Path.Combine(_project, "level.tscn"), "[gd_scene format=3]\n\n[node name=\"Level\" type=\"Node2D\"]\n");
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _listener.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task NoStepsAndMoreThanAHundredAreRefused()
    {
        SceneBatchStep[] many = [.. Enumerable.Repeat(DeleteBox(), 101)];

        McpException none = await RefusedAsync([]);
        McpException tooMany = await RefusedAsync(many);

        Assert.Equal("steps is empty; a batch needs at least one step.", none.Message);
        Assert.Equal("a batch takes at most 100 steps; split it.", tooMany.Message);
    }

    [Fact]
    public async Task AnUnknownToolIsRefusedListingTheNine()
    {
        McpException refused = await RefusedAsync([DeleteBox(), new SceneBatchStep("get_node_properties", [])]);

        Assert.Equal($"step 1 (get_node_properties): 'get_node_properties' cannot run in a scene batch; tool is one of: {Nine}.", refused.Message);
    }

    [Fact]
    public async Task ProjectPathInsideArgsIsRefused()
    {
        JsonObject args = new() { ["projectPath"] = _project, ["nodePaths"] = new JsonArray("Box") };

        McpException refused = await RefusedAsync([new SceneBatchStep("delete_nodes", args)]);

        Assert.Equal(
            "step 0 (delete_nodes): args.projectPath is not taken: the batch's projectPath and scenePath are every step's.",
            refused.Message
        );
    }

    [Fact]
    public async Task AWrongTypedArgIsRefusedNamingTheKey()
    {
        JsonObject duplicate = new()
        {
            ["nodePath"] = "Box",
            ["options"] = new JsonObject { ["parent"] = 5 },
        };

        McpException refused = await RefusedAsync([new SceneBatchStep("delete_nodes", new JsonObject { ["nodePaths"] = 5 })]);
        McpException nested = await RefusedAsync([new SceneBatchStep("duplicate_node", duplicate)]);

        Assert.Equal(
            "step 0 (delete_nodes): args.nodePaths has the wrong type for delete_nodes; see the tool's schema for what it takes.",
            refused.Message
        );
        Assert.Equal(
            "step 0 (duplicate_node): args.options.parent has the wrong type for duplicate_node; see the tool's schema for what it takes.",
            nested.Message
        );
    }

    [Fact]
    public async Task AnUnknownNestedKeyIsIgnoredAsTheSingleToolIgnoresIt()
    {
        const string withExtra = """{"parent": "Box", "extra": 1}""";
        JsonObject duplicate = new() { ["nodePath"] = "Box", ["options"] = JsonNode.Parse(withExtra) };

        // The SDK binds the single tool's options with these options, which skip a key no property takes.
        DuplicateNodeOptions? single = JsonSerializer.Deserialize<DuplicateNodeOptions>(withExtra, McpJsonUtilities.DefaultOptions);
        McpException refused = await RefusedAsync([new SceneBatchStep("duplicate_node", duplicate), NoPaths()]);

        Assert.Equal("Box", single?.Parent);
        Assert.StartsWith("step 1 (delete_nodes): ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoLibrariesToOneFileAreRefused()
    {
        McpException refused = await RefusedAsync([ExportLibrary("tiles.tres"), DeleteBox(), ExportLibrary("res://Tiles.tres")]);

        Assert.Equal("step 2 (export_mesh_library): outputPath res://Tiles.tres is also step 0's; each library needs its own file.", refused.Message);
    }

    [Fact]
    public async Task AStepsOwnRefusalCarriesTheSingleToolsMessage()
    {
        string noPaths = Assert.Throws<McpException>(() => HeadlessTools.DeleteNodesParameters(_project, [])).Message;
        string badScript = Assert.Throws<McpException>(() => HeadlessTools.AttachScriptParameters(_project, "Box", "player.txt")).Message;
        JsonObject attach = new() { ["nodePath"] = "Box", ["scriptPath"] = "player.txt" };

        McpException emptyDelete = await RefusedAsync([new SceneBatchStep("delete_nodes", new JsonObject { ["nodePaths"] = new JsonArray() })]);
        McpException wrongScript = await RefusedAsync([DeleteBox(), new SceneBatchStep("attach_script", attach)]);

        Assert.Equal($"step 0 (delete_nodes): {noPaths}", emptyDelete.Message);
        Assert.Equal($"step 1 (attach_script): {badScript}", wrongScript.Message);
    }

    [Fact]
    public void EachToolsArgsAreItsSchemaPropertiesButProjectPathAndScenePath()
    {
        foreach (string tool in Nine.Split(", "))
        {
            JsonElement schema = SchemaOf(tool);
            string[] properties = [.. schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Where(IsStepArgument)];
            string[] required = schema.TryGetProperty("required", out JsonElement names)
                ? [.. names.EnumerateArray().Select(name => name.GetString()!).Where(IsStepArgument)]
                : [];

            Assert.Equal(properties.Order(StringComparer.Ordinal), HeadlessTools.SceneStepArgumentNames(tool).Order(StringComparer.Ordinal));
            Assert.Equal(required.Order(StringComparer.Ordinal), HeadlessTools.SceneStepRequiredArguments(tool).Order(StringComparer.Ordinal));
        }
    }

    private static bool IsStepArgument(string name) => name is not ("projectPath" or "scenePath");

    /// <summary>The MCP input schema the SDK builds for the HeadlessTools method named tool.</summary>
    private static JsonElement SchemaOf(string tool)
    {
        MethodInfo method = typeof(HeadlessTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == tool);
        var created = McpServerTool.Create(method, _ => throw new InvalidOperationException("the schema needs no target"), null);
        return created.ProtocolTool.InputSchema;
    }

    private static SceneBatchStep NoPaths() => new("delete_nodes", new JsonObject { ["nodePaths"] = new JsonArray() });

    private static SceneBatchStep ExportLibrary(string outputPath) => new("export_mesh_library", new JsonObject { ["outputPath"] = outputPath });

    private static SceneBatchStep DeleteBox() => new("delete_nodes", new JsonObject { ["nodePaths"] = new JsonArray("Box") });

    private Task<McpException> RefusedAsync(SceneBatchStep[] steps) =>
        Assert.ThrowsAsync<McpException>(() =>
            _tools.BatchSceneOperationsAsync(_project, "level.tscn", steps, TestContext.Current.CancellationToken)
        );
}
