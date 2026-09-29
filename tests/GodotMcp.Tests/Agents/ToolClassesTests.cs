using System.Text.Json;
using GodotMcp.Server.Agents;
using ModelContextProtocol.Server;

namespace GodotMcp.Tests.Agents;

/// <summary>ToolClasses: every served tool has one class, read from its attributes save for the overrides, and --list-tools prints them.</summary>
public sealed class ToolClassesTests
{
    [Fact]
    public void EveryServedToolHasExactlyOneKnownClass()
    {
        List<string> served = [.. ToolClasses.ServedTools().Select(t => t.Tool.Name!).Order(StringComparer.Ordinal)];

        Assert.NotEmpty(served);
        Assert.Equal(served, ToolClasses.ByTool.Keys);
        Assert.All(ToolClasses.ByTool.Values, c => Assert.Contains(c, ToolClasses.All));
    }

    [Theory]
    [InlineData("hover", ToolClasses.Drive)]
    [InlineData("scroll", ToolClasses.Drive)]
    [InlineData("click", ToolClasses.Drive)]
    [InlineData("run_project", ToolClasses.Drive)]
    [InlineData("get_scene_tree", ToolClasses.Read)]
    [InlineData("take_screenshot", ToolClasses.Read)]
    [InlineData("capture_frames", ToolClasses.Read)]
    [InlineData("validate", ToolClasses.Read)]
    [InlineData("add_node", ToolClasses.EditScene)]
    [InlineData("delete_nodes", ToolClasses.EditScene)]
    [InlineData("batch_scene_operations", ToolClasses.EditScene)]
    [InlineData("run_script", ToolClasses.EditLive)]
    [InlineData("call_method", ToolClasses.EditLive)]
    [InlineData("batch_drive", ToolClasses.EditLive)]
    public void AToolGetsTheClassItsAttributesGive(string tool, string expected) => Assert.Equal(expected, ToolClasses.ByTool[tool]);

    [Theory]
    [InlineData("stop_project", ToolClasses.Drive)]
    [InlineData("restart_project", ToolClasses.Drive)]
    [InlineData("set_property", ToolClasses.EditLive)]
    [InlineData("cs_get", ToolClasses.Read)]
    public void AnOverriddenToolGetsItsOverride(string tool, string expected)
    {
        Assert.Equal(expected, ToolClasses.Overrides[tool].Class);
        Assert.Equal(expected, ToolClasses.ByTool[tool]);
    }

    [Fact]
    public void EveryOverrideNamesAServedToolWhoseAttributesGiveAnotherClass()
    {
        var byAttributes = ToolClasses
            .ServedTools()
            .ToDictionary(t => t.Tool.Name!, t => ToolClasses.FromAttributes(t.Tool, t.ToolType), StringComparer.Ordinal);

        Assert.Equal(4, ToolClasses.Overrides.Count);
        Assert.All(
            ToolClasses.Overrides,
            o =>
            {
                Assert.True(byAttributes.ContainsKey(o.Key), $"{o.Key} is not a served tool");
                Assert.NotEqual(o.Value.Class, byAttributes[o.Key]);
                Assert.False(string.IsNullOrWhiteSpace(o.Value.Reason));
            }
        );
    }

    [Fact]
    public void FromAttributesReadsReadOnlyThenHeadlessThenDestructive()
    {
        Assert.Equal(
            ToolClasses.Read,
            ToolClasses.FromAttributes(new McpServerToolAttribute { ReadOnly = true, Destructive = true }, typeof(object))
        );
        Assert.Equal(
            ToolClasses.EditLive,
            ToolClasses.FromAttributes(new McpServerToolAttribute { ReadOnly = false, Destructive = true }, typeof(object))
        );
        Assert.Equal(
            ToolClasses.Drive,
            ToolClasses.FromAttributes(new McpServerToolAttribute { ReadOnly = false, Destructive = false }, typeof(object))
        );
    }

    [Fact]
    public void ListToolsPrintsEveryToolAndItsClassSortedByName()
    {
        StringWriter output = new();
        StringWriter error = new();

        int? status = ServerCommands.Run(["--list-tools"], output, error, new CommandEnvironment("unused", null));

        Assert.Equal(0, status);
        Assert.Equal("", error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        List<(string Name, string Class)> tools = [];
        foreach (JsonElement tool in document.RootElement.EnumerateArray())
        {
            Assert.Equal(["name", "class"], tool.EnumerateObject().Select(p => p.Name));
            tools.Add((tool.GetProperty("name").GetString()!, tool.GetProperty("class").GetString()!));
        }

        Assert.Equal(ToolClasses.ByTool.Select(p => (p.Key, p.Value)), tools);
        Assert.Equal(tools.Select(t => t.Name).Order(StringComparer.Ordinal), tools.Select(t => t.Name));
        Assert.Contains(("hover", ToolClasses.Drive), tools);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-option")]
    public void ArgumentsNamingNoOptionLeaveTheServerToStart(string line) =>
        Assert.Null(ServerCommands.Run(Words(line), new StringWriter(), new StringWriter(), new CommandEnvironment("unused", null)));

    [Theory]
    [InlineData("--list-tools extra")]
    [InlineData("--sweep-agents --root")]
    [InlineData("--sweep-agents --bogus")]
    [InlineData("--some-host-option")]
    public void UnreadableArgumentsAreRefusedWithStatus2(string line)
    {
        StringWriter error = new();

        int? status = ServerCommands.Run(Words(line), new StringWriter(), error, new CommandEnvironment("unused", null));

        Assert.Equal(2, status);
        Assert.Contains("usage: godot-mcp", error.ToString(), StringComparison.Ordinal);
    }

    private static string[] Words(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
