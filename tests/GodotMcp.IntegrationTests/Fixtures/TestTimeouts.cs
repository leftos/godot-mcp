namespace GodotMcp.IntegrationTests.Fixtures;

internal static class TestTimeouts
{
    /// <summary>
    /// The xUnit timeout of a test that launches its own game: a hang guard, which a passing run never reaches, not a bound it
    /// must beat. xUnit counts wall time, while the server counts every wait (the launch handshake, each call, a scratch
    /// scene's ceiling) in load-adjusted time, which on a loaded machine may run to its backstop of 5 times the budget in wall
    /// time: a launch alone may take up to its 600 s backstop (SessionHarness's 2-minute handshake), and a scratch folder plays
    /// 17 games. So it is sized in minutes past that slow path;
    /// in a gated run the gate's STALLED or ceiling line catches a hang as well (docs/DEVELOPMENT.md).
    /// </summary>
    public const int OwnLaunchMs = 900_000;
}
