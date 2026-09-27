using GodotMcp.Server;

namespace GodotMcp.Tests;

public sealed class ServerVersionTests
{
    [Fact]
    public void IsTheSemanticVersionAndTheShortCommitSha() => Assert.Matches(@"^\d+\.\d+\.\d+(\+[0-9a-f]{7})?$", ServerVersion.Value);
}
