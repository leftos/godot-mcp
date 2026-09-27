using System.Diagnostics;
using System.Runtime.InteropServices;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>The real Win32 debugger check, on a child process with no debugger and with this test attached to it as one.</summary>
public sealed class DebuggerPresenceTests
{
    private const string WindowsOnly = "CheckRemoteDebuggerPresent is Windows only; elsewhere the check is always false.";

    // A cmd.exe whose stdin is a pipe the test holds open waits on it, so it runs until the test kills it.
    [Fact]
    public void TheWin32CheckFindsNoDebuggerOnAChildProcessOrOneItCannotOpen()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using Process child = StartChild();
        try
        {
            Assert.False(DebuggerPresence.IsAttached(child.Id, NullLogger.Instance));
            Assert.False(DebuggerPresence.IsAttached(0, NullLogger.Instance));
        }
        finally
        {
            child.Kill();
            child.WaitForExit();
        }
    }

    // DebugActiveProcessStop must run on the thread that attached, so this test never awaits.
    [Fact]
    public void TheWin32CheckFindsTheTestAttachedAsTheChildsDebugger()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using Process child = StartChild();
        Assert.True(DebugActiveProcess((uint)child.Id), $"DebugActiveProcess failed with Win32 error {Marshal.GetLastPInvokeError()}");
        try
        {
            Assert.True(DebuggerPresence.IsAttached(child.Id, NullLogger.Instance));
        }
        finally
        {
            DebugActiveProcessStop((uint)child.Id);
            child.Kill();
            child.WaitForExit();
        }
    }

    private static Process StartChild() =>
        Process.Start(
            new ProcessStartInfo("cmd.exe")
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        )!;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DebugActiveProcess(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DebugActiveProcessStop(uint processId);
}
