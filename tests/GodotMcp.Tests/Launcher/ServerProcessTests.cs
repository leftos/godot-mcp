using GodotMcp.Launcher;

namespace GodotMcp.Tests.Launcher;

/// <summary>
/// ServerProcess: the launcher's wait loop returns the exit code of the server it started, which is how a Ctrl+C the server
/// answered with 130 reaches the shell (the launcher's own handler keeps only the wait alive; the event itself cannot be raised
/// from a test host).
/// </summary>
public sealed class ServerProcessTests
{
    [Fact]
    public void TheWaitReturnsTheExitCodeOfTheProcessItStarted()
    {
        string command = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

        int status = ServerProcess.Run(command, CommandLine.Build(command, ["/d", "/c", "exit 3"]));

        Assert.Equal(3, status);
    }
}
