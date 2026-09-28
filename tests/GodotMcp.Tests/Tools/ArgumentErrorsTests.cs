using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// The messages a call whose arguments do not bind gets, from the real tools' input schemas as the server builds them with
/// <see cref="ToolJson.Options"/>.
/// </summary>
public sealed class ArgumentErrorsTests : IAsyncDisposable
{
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly ServiceProvider _services;

    public ArgumentErrorsTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(_sessions);
        services.AddSingleton(TestCSharp.Unused());
        services.AddMcpServer().WithToolsFromAssembly(typeof(RuntimeTools).Assembly, ToolJson.Options);
        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _sessions.Dispose();
        _listener.Dispose();
    }

    [Fact]
    public void AStringOptionGivenABoolIsNamed()
    {
        JsonObject arguments = new()
        {
            ["projectPath"] = "x",
            ["options"] = new JsonObject { ["prepare"] = true },
        };

        string? message = Describe("run_project", arguments, new JsonException("bind failed"));

        Assert.NotNull(message);
        Assert.StartsWith("options.prepare takes a string, not true. prepare: auto (the default)", message, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RunOptions>("""{"prepare":true}""", ToolJson.Options));
    }

    [Fact]
    public void AnUnknownOptionKeyIsNamedWithTheKnownOnes()
    {
        JsonObject arguments = new()
        {
            ["projectPath"] = "x",
            ["options"] = new JsonObject { ["prepar"] = "auto" },
        };

        string? message = Describe("run_project", arguments, new JsonException("bind failed"));

        Assert.NotNull(message);
        Assert.StartsWith("options has no 'prepar'; it takes: quiet, ", message, StringComparison.Ordinal);
        Assert.Contains(", prepare, ", message, StringComparison.Ordinal);
        Assert.EndsWith(".", message, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RunOptions>("""{"prepar":"auto"}""", ToolJson.Options));
    }

    [Fact]
    public void AnUnknownTopLevelArgumentIsNamed()
    {
        JsonObject arguments = new() { ["projectPath"] = "x", ["optoins"] = new JsonObject() };
        JsonElement schema = SchemaOf("run_project");

        string? described = Describe("run_project", arguments, new JsonException("bind failed"));
        string? refused = ArgumentErrors.UnknownArgument("run_project", schema, arguments.Select(pair => pair.Key));

        Assert.NotNull(refused);
        Assert.StartsWith("run_project has no argument 'optoins'; it takes: projectPath, ", refused, StringComparison.Ordinal);
        Assert.Contains(", options", refused, StringComparison.Ordinal);
        Assert.Equal(refused, described);
        Assert.Null(ArgumentErrors.UnknownArgument("run_project", schema, ["projectPath", "options"]));
    }

    [Fact]
    public void AMissingRequiredArgumentIsNamed()
    {
        string? message = Describe("run_project", [], new ArgumentException("missing", "arguments"));

        Assert.Equal("run_project needs the argument 'projectPath'.", message);
    }

    [Fact]
    public void AnArrayItemIsNamedByIndex()
    {
        JsonObject arguments = new()
        {
            ["projectPath"] = "x",
            ["scenePath"] = "main.tscn",
            ["updates"] = new JsonArray(
                new JsonObject
                {
                    ["nodePath"] = ".",
                    ["property"] = "visible",
                    ["value"] = true,
                },
                new JsonObject
                {
                    ["nodePath"] = 5,
                    ["property"] = "visible",
                    ["value"] = false,
                }
            ),
        };

        string? message = Describe("set_node_properties", arguments, new JsonException("bind failed"));

        Assert.NotNull(message);
        Assert.StartsWith("updates[1].nodePath takes a string, not 5. nodePath: The node's path", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFreeFormValueTakesAnyKeys()
    {
        JsonObject properties = new() { ["anything"] = 1, ["other"] = "x" };
        JsonObject arguments = new()
        {
            ["projectPath"] = "x",
            ["scenePath"] = "main.tscn",
            ["nodeType"] = "Node2D",
            ["nodeName"] = "Box",
            ["options"] = new JsonObject { ["properties"] = properties },
        };

        AddNodeOptions? bound = JsonSerializer.Deserialize<AddNodeOptions>(arguments["options"]!.ToJsonString(), ToolJson.Options);

        Assert.NotNull(bound?.Properties);
        Assert.Equal(["anything", "other"], bound.Properties.Keys);
        Assert.Null(ArgumentErrors.UnknownArgument("add_node", SchemaOf("add_node"), arguments.Select(pair => pair.Key)));
        Assert.StartsWith(
            "add_node's arguments do not bind: ",
            Describe("add_node", arguments, new JsonException("bind failed")),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ALongGivenValueIsCut()
    {
        JsonObject arguments = new() { ["projectPath"] = "x", ["options"] = new string('a', 200) };

        string? message = Describe("run_project", arguments, new JsonException("bind failed"));

        Assert.NotNull(message);
        Assert.StartsWith("options takes an object, not \"aaa", message, StringComparison.Ordinal);
        int start = message.IndexOf(", not ", StringComparison.Ordinal) + ", not ".Length;
        int end = message.IndexOf('…', start);
        Assert.True(end > start, message);
        string given = message[start..(end + 1)];
        Assert.True(given.Length <= 61, given);
    }

    [Fact]
    public void ATheToolsOwnArgumentExceptionPassesThrough()
    {
        JsonObject arguments = new()
        {
            ["projectPath"] = "x",
            ["options"] = new JsonObject { ["prepare"] = true },
        };

        string? message = Describe("run_project", arguments, new ArgumentException("the tool's own", "count"));

        Assert.Null(message);
    }

    private string? Describe(string tool, JsonObject arguments, Exception exception) =>
        ArgumentErrors.Describe(
            tool,
            SchemaOf(tool),
            arguments.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value)),
            exception
        );

    private JsonElement SchemaOf(string tool)
    {
        McpServerPrimitiveCollection<McpServerTool> tools = _services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        Assert.True(tools.TryGetPrimitive(tool, out McpServerTool? found), tool);
        return found.ProtocolTool.InputSchema;
    }
}
