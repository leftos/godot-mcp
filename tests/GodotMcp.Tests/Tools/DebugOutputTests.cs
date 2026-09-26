using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>get_debug_output's argument checks, without a game.</summary>
public sealed class DebugOutputTests : IDisposable
{
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void BeforeZeroIsRefused()
    {
        ProjectTools tools = new(new SessionRegistry(_listener, NullLogger<GodotSession>.Instance));

        McpException refused = Assert.Throws<McpException>(() => tools.GetDebugOutput(10, 0));

        Assert.Contains("before must be a line number", refused.Message, StringComparison.Ordinal);
    }
}
