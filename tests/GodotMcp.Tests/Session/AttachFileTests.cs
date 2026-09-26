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
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"), false);

        JsonNode content = JsonNode.Parse(File.ReadAllText(_project.Combine(".godot", "godot-mcp", "attach.json")))!;
        Assert.Equal((51234, "ABCDEF"), (content["port"]!.GetValue<int>(), content["token"]!.GetValue<string>()));
    }

    [Fact]
    public void WriteHoldsWhetherToShutOutRealGamepads()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"), false);
        bool byDefault = JsonNode.Parse(File.ReadAllText(AttachFile.PathIn(_project.Path)))!["shutOutRealGamepads"]!.GetValue<bool>();
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"), true);
        bool shutOut = JsonNode.Parse(File.ReadAllText(AttachFile.PathIn(_project.Path)))!["shutOutRealGamepads"]!.GetValue<bool>();

        Assert.Equal((false, true), (byDefault, shutOut));
    }

    [Fact]
    public void WriteReplacesAnOlderFile()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(1111, "OLD"), false);

        AttachFile.Write(_project.Path, new BridgeEndpoint(2222, "NEW"), false);

        JsonNode content = JsonNode.Parse(File.ReadAllText(AttachFile.PathIn(_project.Path)))!;
        Assert.Equal((2222, "NEW"), (content["port"]!.GetValue<int>(), content["token"]!.GetValue<string>()));
    }

    [Fact]
    public void RemoveDeletesTheFile()
    {
        AttachFile.Write(_project.Path, new BridgeEndpoint(51234, "ABCDEF"), false);

        Assert.True(AttachFile.Remove(_project.Path));
        Assert.False(File.Exists(AttachFile.PathIn(_project.Path)));
    }

    [Fact]
    public void RemoveWithoutAFileDoesNothing() => Assert.False(AttachFile.Remove(_project.Path));
}
