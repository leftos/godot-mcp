namespace GodotMcp.IntegrationTests.Fixtures;

internal static class TestTimeouts
{
    /// <summary>
    /// The xUnit timeout of a test that launches its own game: the launch handshake is load-adjusted and may take 75 s of wall
    /// time, and xUnit's timeout is not (docs/DEVELOPMENT.md).
    /// </summary>
    public const int OwnLaunchMs = 180_000;
}
