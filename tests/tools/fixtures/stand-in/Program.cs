using System.Diagnostics;
using System.Globalization;

// --list-tools exits 0 (the installers' start check); --sweep-agents prints a line and exits with STANDIN_SWEEP_STATUS
// (0 when unset); --start <exe> <flag> runs <exe> <flag> and exits with its code, as a launcher does; <flag> alone waits
// while that file exists, standing in for a running server.
if (args is ["--list-tools"])
{
    return 0;
}

if (args is ["--sweep-agents"])
{
    Console.WriteLine("stand-in: swept the agent files");
    return int.Parse(Environment.GetEnvironmentVariable("STANDIN_SWEEP_STATUS") ?? "0", CultureInfo.InvariantCulture);
}

if (args is ["--start", string server, string flag])
{
    using var child = Process.Start(server, [flag]);
    child.WaitForExit();
    return child.ExitCode;
}

if (args is [string waitFlag])
{
    DateTime hangGuard = DateTime.UtcNow.AddMinutes(10);
    while (File.Exists(waitFlag) && DateTime.UtcNow < hangGuard)
    {
        Thread.Sleep(100);
    }

    return 0;
}

Console.Error.WriteLine("stand-in: unexpected arguments: " + string.Join(' ', args));
return 2;
