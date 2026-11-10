using System.Reflection;
using GodotMcp.IntegrationTests.Fixtures;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// Every test's xUnit <c>Timeout</c> is a hang guard a passing run never reaches, never a bound it must beat: an itest that
/// launches its own game, or restarts or resets one, spends a launch handshake and each call's own allowance of minutes on a
/// loaded machine, all of which xUnit counts in plain wall time (docs/DEVELOPMENT.md, Footguns). This reads the assembly's own
/// attributes, so a new test class that sets a shorter Timeout fails here with its class, method and timeout named.
/// </summary>
public sealed class HangGuardTests
{
    [Fact]
    public void EveryTestOnASharedOrOwnGameHasAHangGuard()
    {
        string[] shortBounded = [.. ShortTimeouts()];
        Assert.True(
            shortBounded.Length == 0,
            $"These tests set an xUnit Timeout below the {TestTimeouts.OwnLaunchMs} ms hang guard a launch, restart or reset can "
                + $"outlast: {string.Join(", ", shortBounded)}"
        );
    }

    /// <summary>Every test method whose own Timeout is below <see cref="TestTimeouts.OwnLaunchMs"/>, as "Class.Method (ms)".</summary>
    private static IEnumerable<string> ShortTimeouts() =>
        from type in typeof(HangGuardTests).Assembly.GetTypes()
        from method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        let timeout = TimeoutOf(method)
        where timeout is > 0 and < TestTimeouts.OwnLaunchMs
        select $"{type.Name}.{method.Name} ({timeout} ms)";

    /// <summary>A method's xUnit timeout in milliseconds, or 0 when it sets none; <see cref="TheoryAttribute"/> derives from it.</summary>
    private static int TimeoutOf(MethodInfo method) =>
        method.GetCustomAttributes<FactAttribute>(inherit: true).Select(fact => fact.Timeout).DefaultIfEmpty(0).Max();
}
