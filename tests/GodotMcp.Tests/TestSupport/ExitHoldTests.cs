using System.Diagnostics;
using GodotMcp.Tests.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.TestSupport;

public sealed class ExitHoldTests
{
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
}
