using System.ComponentModel;
using System.Diagnostics;
using GodotMcp.Tests.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.TestSupport;

public sealed class ExitHoldTests
{
    private volatile bool _failTheWait;

    [Fact]
    public async Task AnAttachWhoseChildEndsBeforeTheBreakInFailsWithTheChildsEnd()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        Process child = RegistryHarness.StartStandInGame();
        try
        {
            // Killed while the debugger holds the attach's first event, so the break-in can never be raised.
            Task<ExitHold> attach = ExitHold.AttachAsync(child, RegistryHarness.HangGuard, onFirstEvent: () => child.Kill(entireProcessTree: true));

            InvalidOperationException ended = await Assert.ThrowsAsync<InvalidOperationException>(() => attach);
            Assert.True(child.WaitForExit(RegistryHarness.HangGuard));
            Assert.Contains($"code 0x{(uint)child.ExitCode:X8}", ended.Message, StringComparison.Ordinal);

            Assert.Contains($"pid {child.Id}", ended.Message, StringComparison.Ordinal);
            Assert.Contains("before the debugger's break-in", ended.Message, StringComparison.Ordinal);
        }
        finally
        {
            RegistryHarness.EndStandInGame(child);
        }
    }

    [Fact]
    public async Task AWaitForDebugEventErrorOtherThanItsTimeoutFailsTheAttachNamingIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        Process child = RegistryHarness.StartStandInGame();
        try
        {
            Task<ExitHold> attach = ExitHold.AttachAsync(child, RegistryHarness.HangGuard, waitForEvent: (_, _) => 5);

            Win32Exception failed = await Assert.ThrowsAsync<Win32Exception>(() => attach);
            Assert.Equal(5, failed.NativeErrorCode);
            Assert.Contains("WaitForDebugEvent", failed.Message, StringComparison.Ordinal);
            Assert.Contains($"pid {child.Id}", failed.Message, StringComparison.Ordinal);
            Assert.Contains("Windows error 5", failed.Message, StringComparison.Ordinal);
        }
        finally
        {
            RegistryHarness.EndStandInGame(child);
        }
    }

    [Fact]
    public async Task AWaitForDebugEventTimeoutIsRetriedNotFailed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        Process child = RegistryHarness.StartStandInGame();
        int timeouts = 0;
        try
        {
            ExitHold hold = await ExitHold.AttachAsync(
                child,
                RegistryHarness.HangGuard,
                waitForEvent: (debugEvent, milliseconds) => ++timeouts <= 3 ? 121 : ExitHold.WaitForEvent(debugEvent, milliseconds)
            );

            hold.Dispose();
        }
        finally
        {
            RegistryHarness.EndStandInGame(child);
        }
    }

    [Fact]
    public async Task AFailureAfterTheAttachFaultsKilledToo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        Process child = RegistryHarness.StartStandInGame();
        try
        {
            ExitHold hold = await ExitHold.AttachAsync(
                child,
                RegistryHarness.HangGuard,
                waitForEvent: (debugEvent, milliseconds) => _failTheWait ? 6 : ExitHold.WaitForEvent(debugEvent, milliseconds)
            );
            _failTheWait = true;

            Win32Exception failed = await Assert.ThrowsAsync<Win32Exception>(() =>
                hold.Killed.WaitAsync(RegistryHarness.HangGuard, TestContext.Current.CancellationToken)
            );
            Assert.Equal(6, failed.NativeErrorCode);

            hold.Dispose();
        }
        finally
        {
            RegistryHarness.EndStandInGame(child);
        }
    }

    [Fact]
    public void RequireX64HostRefusesA32BitProcess()
    {
        PlatformNotSupportedException refused = Assert.Throws<PlatformNotSupportedException>(() => ExitHold.RequireX64Host(is64BitProcess: false));

        Assert.Contains("64-bit", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireX64HostAcceptsA64BitProcess() => ExitHold.RequireX64Host(is64BitProcess: true);
}
