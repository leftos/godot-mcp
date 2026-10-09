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

    [Theory]
    [InlineData("{\"pid\":4242}", 4242)]
    [InlineData("{\"pid\":4242.0}", 4242)]
    public void ReadsTheGamesProcessIdFromTheHello(string pid, int expected)
    {
        JsonObject hello = Hello(Token, BridgeProjectPath, pid);

        Assert.Equal(expected, HandshakeExpectation.ReadProcessId(hello));
        Assert.Null(_expected.FindMismatch(hello));
    }

    [Theory]
    [InlineData("{\"pid\":\"4242\"}")]
    [InlineData("{\"pid\":42.5}")]
    [InlineData("{\"pid\":0}")]
    public void AHelloWithoutAUsablePidHasNoProcessIdAndStillMatches(string pid)
    {
        JsonObject hello = Hello(Token, BridgeProjectPath, pid);

        Assert.Null(HandshakeExpectation.ReadProcessId(hello));
        Assert.Null(_expected.FindMismatch(hello));
    }

    [Fact]
    public void AHelloWithoutAPidHasNoProcessId()
    {
        JsonObject hello = Hello(Token, BridgeProjectPath);

        Assert.Null(HandshakeExpectation.ReadProcessId(hello));
        Assert.Null(_expected.FindMismatch(hello));
    }

    [Fact]
    public void ReadsTheWindowSizeFromTheHelloOrNone()
    {
        JsonObject hello = Hello(Token, BridgeProjectPath, "{\"window\":{\"width\":7680.0,\"height\":4320}}");
        JsonObject zero = Hello(Token, BridgeProjectPath, "{\"window\":{\"width\":0,\"height\":4320}}");

        Assert.Equal(new WindowSize(7680, 4320), HandshakeExpectation.ReadWindow(hello));
        Assert.Null(HandshakeExpectation.ReadWindow(Hello(Token, BridgeProjectPath)));
        Assert.Null(HandshakeExpectation.ReadWindow(zero));
        Assert.Null(_expected.FindMismatch(hello));
    }

    // The bridge writes its numbers as JSON text, so the handle arrives as the reader decoded it; a C# literal of the wrong
    // width would fail the type check without ever reaching the value.
    [Theory]
    [InlineData("{\"hwnd\":1311768}", 1_311_768L)]
    [InlineData("{\"hwnd\":1311768.0}", 1_311_768L)]
    [InlineData("{\"hwnd\":8589934594}", 8_589_934_594L)]
    [InlineData("{\"hwnd\":-5}", -5L)]
    [InlineData("{\"hwnd\":-5.0}", -5L)]
    public void ReadsTheWindowHandleFromTheHellosJsonText(string hwnd, long expected)
    {
        JsonObject hello = Hello(Token, BridgeProjectPath, hwnd);

        Assert.Equal(expected, HandshakeExpectation.ReadWindowHandle(hello));
        Assert.Null(_expected.FindMismatch(hello));
    }

    [Theory]
    [InlineData("{\"hwnd\":0}")]
    [InlineData("{\"hwnd\":0.0}")]
    [InlineData("{\"hwnd\":12.5}")]
    [InlineData("{\"hwnd\":\"1311768\"}")]
    public void ReadsNoWindowHandleFromAnythingElse(string hwnd) =>
        Assert.Null(HandshakeExpectation.ReadWindowHandle(Hello(Token, BridgeProjectPath, hwnd)));

    [Fact]
    public void AHelloWithoutAHwndHasNoWindowHandle()
    {
        JsonObject hello = Hello(Token, BridgeProjectPath);

        Assert.Null(HandshakeExpectation.ReadWindowHandle(hello));
        Assert.Null(_expected.FindMismatch(hello));
    }

    /// <summary>The hello's base fields, plus <paramref name="extra"/>'s fields as the bridge writes them: as JSON text.</summary>
    private static JsonObject Hello(string token, string projectPath, string extra)
    {
        JsonObject hello = Hello(token, projectPath);
        foreach ((string name, JsonNode? value) in JsonNode.Parse(extra)!.AsObject())
        {
            hello[name] = value?.DeepClone();
        }

        return hello;
    }

    private static JsonObject Hello(string token, string projectPath) =>
        new()
        {
            ["type"] = "hello",
            ["token"] = token,
            ["projectPath"] = projectPath,
        };
}
