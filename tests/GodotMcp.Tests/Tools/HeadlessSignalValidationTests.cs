using System.Text.Json;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>The signal tools' argument checks, which refuse before a headless Godot starts; no Godot runs here.</summary>
public sealed class HeadlessSignalValidationTests
{
    private const string EmptySignal = "signal is empty; name the signal (pressed, body_entered...).";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ConnectSignalRefusesAnEmptySignalName(string signal)
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckSignalName(signal));

        Assert.Equal(EmptySignal, refused.Message);
        Assert.Equal("pressed", HeadlessTools.CheckSignalName(" pressed "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ConnectSignalRefusesATargetWithoutMethod(string? method)
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckConnectTarget(new ConnectTarget(".", method!)));

        Assert.Equal("target.method is empty; name the method the signal calls.", refused.Message);
        JsonElement[] binds = [JsonDocument.Parse("7").RootElement, JsonDocument.Parse("\"gold\"").RootElement];
        Assert.Equal(
            """{"nodePath":"Boss","method":"on_hit","binds":[7,"gold"]}""",
            HeadlessTools.CheckConnectTarget(new ConnectTarget(" Boss ", " on_hit ", binds)).ToJsonString()
        );
    }

    [Theory]
    [InlineData("/root/Level")]
    [InlineData("root/Level")]
    [InlineData("../Other")]
    [InlineData("Boss:position")]
    [InlineData("")]
    public void ConnectSignalRefusesABadTargetNodePath(string nodePath)
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckConnectTarget(new ConnectTarget(nodePath, "on_hit")));

        Assert.Equal(
            $"Node paths are relative to the scene root: use \".\" for the root and \"Boss/Sprite\" for a child, not \"{nodePath}\".",
            refused.Message
        );
    }

    [Fact]
    public void ConnectSignalRefusesANullTarget()
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckConnectTarget(null));

        Assert.Equal("Node paths are relative to the scene root: use \".\" for the root and \"Boss/Sprite\" for a child, not \"\".", refused.Message);
    }

    [Fact]
    public void ConnectSignalSendsNullBindsAsAnEmptyList() =>
        Assert.Equal(
            """{"nodePath":".","method":"on_pressed","binds":[]}""",
            HeadlessTools.CheckConnectTarget(new ConnectTarget(".", "on_pressed", null)).ToJsonString()
        );

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void DisconnectSignalRefusesAnEmptySignalName(string signal)
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckSignalName(signal));

        Assert.Equal(EmptySignal, refused.Message);
        Assert.Equal("""{"nodePath":".","method":"on_pressed"}""", HeadlessTools.CheckTargetMethod(".", "on_pressed").ToJsonString());
    }
}
