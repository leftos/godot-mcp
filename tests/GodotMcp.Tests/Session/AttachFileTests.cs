using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

public sealed class AttachFileTests : IDisposable
{
    private readonly TempDirectory _project = new();

    public void Dispose() => _project.Dispose();

    [Fact]
    public void WriteCreatesTheFolderAndHoldsThePortAndToken()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"));

        JsonNode content = JsonNode.Parse(File.ReadAllText(_project.Combine(".godot", "godot-mcp", "attach.json")))!;
        Assert.Equal((51234, "ABCDEF"), (content["port"]!.GetValue<int>(), content["token"]!.GetValue<string>()));
    }

    [Fact]
    public void WriteReplacesAnOlderFile()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(1111, "OLD"));

        AttachFile.Write(_project.Path, new BridgeEndpoint(2222, "NEW"));

        JsonNode content = JsonNode.Parse(File.ReadAllText(AttachFile.PathIn(_project.Path)))!;
        Assert.Equal((2222, "NEW"), (content["port"]!.GetValue<int>(), content["token"]!.GetValue<string>()));
    }

    [Fact]
    public void RemoveDeletesTheFile()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"));

        Assert.True(AttachFile.Remove(_project.Path));
        Assert.False(File.Exists(AttachFile.PathIn(_project.Path)));
    }

    [Fact]
    public void RemoveWithoutAFileDoesNothing() => Assert.False(AttachFile.Remove(_project.Path));
}
