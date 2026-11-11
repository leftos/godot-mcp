using System.Globalization;
using System.Runtime.InteropServices;

namespace GodotMcp.Server.Session;

/// <summary>
/// The machine's free memory, read once before a scene's game launches: Windows' <c>GlobalMemoryStatusEx</c> (its
/// <c>ullAvailPhys</c>, and <c>ullTotalPhys</c> for the total), Linux's <c>/proc/meminfo</c> (<c>MemAvailable</c>, and
/// <c>MemTotal</c> for the total), and nothing on another system. It never throws: a read that fails is logged and given as
/// nothing, so the scene launches and the figure is simply absent from its result.
/// </summary>
internal static partial class FreeMemory
{
    private const string MemoryInfo = "/proc/meminfo";
    private const long Kilobyte = 1024;
    private const long Megabyte = 1048576;

    /// <summary>The machine's available physical memory in whole megabytes rounded down; null when it cannot be read.</summary>
    internal static int? AvailableMb() => Megabytes(Bytes(available: true));

    /// <summary>The machine's total physical memory in whole megabytes rounded down; null when it cannot be read.</summary>
    internal static int? TotalMb() => Megabytes(Bytes(available: false));

    private static int? Megabytes(long? bytes) => bytes is { } read ? (int)(read / Megabyte) : null;

    private static long? Bytes(bool available)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsBytes(available);
        }

        return OperatingSystem.IsLinux() ? LinuxBytes(available ? "MemAvailable" : "MemTotal") : null;
    }

    private static long? WindowsBytes(bool available)
    {
        MemoryStatus status = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            int error = Marshal.GetLastPInvokeError();
            Log.FreeMemoryReadFailed(Log.Ambient, error);
            return null;
        }

        return (long)(available ? status.AvailablePhysical : status.TotalPhysical);
    }

    private static long? LinuxBytes(string key)
    {
        try
        {
            foreach (string line in File.ReadLines(MemoryInfo))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (
                    parts.Length >= 2
                    && parts[0] == key + ":"
                    && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long kilobytes)
                )
                {
                    return kilobytes * Kilobyte;
                }
            }

            Log.MemoryInfoEntryMissing(Log.Ambient, MemoryInfo, key);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.MemoryInfoReadFailed(Log.Ambient, e, MemoryInfo);
        }

        return null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    /// <summary><c>MEMORYSTATUSEX</c>: the machine's memory load, and its physical, page file and virtual memory in bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
