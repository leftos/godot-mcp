using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace GodotMcp.Server.Session;

/// <summary>
/// A server process with live sessions using a project folder's <c>override.cfg</c>: its process id and its start time in
/// UTC ticks, which tells it from a later process given the same id. Written as <c>&lt;pid&gt;@&lt;ticks&gt;</c>.
/// </summary>
internal readonly record struct OverrideOwner(int ProcessId, long StartTicks)
{
    /// <summary>This server process.</summary>
    public static OverrideOwner Current { get; } = OfCurrentProcess();

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{ProcessId}@{StartTicks}");

    /// <summary>Reads <c>&lt;pid&gt;@&lt;ticks&gt;</c>; false for anything else.</summary>
    public static bool TryParse(string text, out OverrideOwner owner)
    {
        owner = default;
        string[] parts = text.Trim().Split('@');
        if (
            parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int processId)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long startTicks)
        )
        {
            return false;
        }

        owner = new OverrideOwner(processId, startTicks);
        return true;
    }

    /// <summary>
    /// Whether the owner still runs: a process with its id exists and started at its recorded time. A process whose start
    /// time cannot be read (access denied) counts as running, so the file it may be using is kept.
    /// </summary>
    public bool IsAlive()
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            return StartTicksOf(process) is not { } ticks || ticks == StartTicks;
        }
        catch (ArgumentException)
        {
            // No process has the id.
            return false;
        }
        catch (InvalidOperationException)
        {
            // The process exited between the lookup and the read of its start time.
            return false;
        }
    }

    /// <summary>The process's start time in UTC ticks, or null when it cannot be read.</summary>
    private static long? StartTicksOf(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static OverrideOwner OfCurrentProcess()
    {
        using var self = Process.GetCurrentProcess();
        return new OverrideOwner(Environment.ProcessId, self.StartTime.ToUniversalTime().Ticks);
    }
}
