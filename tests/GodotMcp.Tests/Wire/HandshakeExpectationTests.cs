using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;

namespace GodotMcp.Tests.Wire;

public sealed class HandshakeExpectationTests
{
    private const string Token = "0123456789ABCDEF";
    private static readonly string ProjectDir = Path.Combine(Path.GetTempPath(), "godot-mcp-handshake", "game");

    // Godot's globalize_path("res://") form: forward slashes and a trailing separator.
    private static readonly string BridgeProjectPath = ProjectDir.Replace('\\', '/') + "/";

    private readonly HandshakeExpectation _expected = new(Token, ProjectDir);

    [Fact]
    public void AcceptsTheHelloOfThisRunInGodotsPathForm() => Assert.Null(_expected.FindMismatch(Hello(Token, BridgeProjectPath)));

    [Fact]
    public void RefusesAWrongToken()
    {
        string? mismatch = _expected.FindMismatch(Hello("FEDCBA9876543210", BridgeProjectPath));

        Assert.Equal("the session token does not match this run", mismatch);
    }

    [Fact]
    public void RefusesAHelloWithoutAToken()
    {
        JsonObject hello = Hello(Token, BridgeProjectPath);
        hello.Remove("token");

        Assert.Equal("the session token does not match this run", _expected.FindMismatch(hello));
    }

    [Fact]
    public void RefusesAnotherProjectPath()
    {
        string other = Path.Combine(Path.GetTempPath(), "godot-mcp-handshake", "other");

        string? mismatch = _expected.FindMismatch(Hello(Token, other));

        Assert.NotNull(mismatch);
        Assert.Contains($"'{other}'", mismatch, StringComparison.Ordinal);
        Assert.Contains($"'{ProjectDir}'", mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAFirstFrameThatIsNotAHello()
    {
        JsonObject request = new() { ["id"] = 1, ["command"] = "ping" };

        Assert.Equal("the first frame is not a hello", _expected.FindMismatch(request));
    }

    [Fact]
    public void RefusesAHelloTypeThatIsNotAString()
    {
        JsonObject hello = Hello(Token, BridgeProjectPath);
        hello["type"] = 1;

        Assert.Equal("the first frame is not a hello", _expected.FindMismatch(hello));
    }

    [Fact]
    public void ReadsTheGamesProcessIdFromTheHello()
    {
        JsonObject hello = Hello(Token, BridgeProjectPath);
        JsonObject asFloat = Hello(Token, BridgeProjectPath);
        hello["pid"] = 4242;
        asFloat["pid"] = 4242.0;

        Assert.Equal(4242, HandshakeExpectation.ReadProcessId(hello));
        Assert.Equal(4242, HandshakeExpectation.ReadProcessId(asFloat));
        Assert.Null(_expected.FindMismatch(hello));
    }

    [Fact]
    public void AHelloWithoutAUsablePidHasNoProcessIdAndStillMatches()
    {
        JsonObject without = Hello(Token, BridgeProjectPath);
        JsonObject text = Hello(Token, BridgeProjectPath);
        JsonObject fraction = Hello(Token, BridgeProjectPath);
        JsonObject zero = Hello(Token, BridgeProjectPath);
        text["pid"] = "4242";
        fraction["pid"] = 42.5;
        zero["pid"] = 0;

        Assert.Null(HandshakeExpectation.ReadProcessId(without));
        Assert.Null(HandshakeExpectation.ReadProcessId(text));
        Assert.Null(HandshakeExpectation.ReadProcessId(fraction));
        Assert.Null(HandshakeExpectation.ReadProcessId(zero));
        Assert.Null(_expected.FindMismatch(without));
    }

    [Fact]
    public void ReadsTheWindowSizeFromTheHelloOrNone()
    {
        JsonObject hello = Hello(Token, BridgeProjectPath);
        JsonObject without = Hello(Token, BridgeProjectPath);
        JsonObject zero = Hello(Token, BridgeProjectPath);
        hello["window"] = new JsonObject { ["width"] = 7680.0, ["height"] = 4320 };
        zero["window"] = new JsonObject { ["width"] = 0, ["height"] = 4320 };

        Assert.Equal(new WindowSize(7680, 4320), HandshakeExpectation.ReadWindow(hello));
        Assert.Null(HandshakeExpectation.ReadWindow(without));
        Assert.Null(HandshakeExpectation.ReadWindow(zero));
        Assert.Null(_expected.FindMismatch(hello));
    }

    private static JsonObject Hello(string token, string projectPath) =>
        new()
        {
            ["type"] = "hello",
            ["token"] = token,
            ["projectPath"] = projectPath,
        };
}
