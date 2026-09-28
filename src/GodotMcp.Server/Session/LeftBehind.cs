using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// The processes a game that quit left running: on Windows, those a Toolhelp snapshot lists with the game as their parent and
/// that started after it (a process id can be reused, so an older process naming the same parent id is not the game's);
/// elsewhere none.
/// </summary>
internal static partial class LeftBehind
{
    private const uint SnapProcess = 0x00000002;
    private const int NoMoreFiles = 18;
    private const int MaxPath = 260;
    private static readonly nint InvalidHandle = -1;

    /// <summary>
    /// The game's child processes still running, each as <see cref="Describe"/> writes it. Call it before the console
    /// wrapper is ended, whose job ends them. A list that cannot be read is logged at Debug and comes back empty.
    /// </summary>
    /// <param name="game">The game's process, with its handle still open so its start time stays readable.</param>
    /// <param name="logger">Where a failed read is logged.</param>
    public static IReadOnlyList<string> Find(Process game, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        DateTime gameStart;
        try
        {
            gameStart = game.StartTime;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            Log.LeftRunningUnreadable(logger, e, game.Id);
            return [];
        }

        return [.. ChildrenOf(Snapshot(logger), game.Id).Where(child => StartedSince(child.Id, gameStart, logger)).Select(Describe)];
    }

    /// <summary>The listed processes whose parent is <paramref name="parentId"/>.</summary>
    internal static IEnumerable<ListedProcess> ChildrenOf(IEnumerable<ListedProcess> processes, int parentId) =>
        processes.Where(process => process.ParentId == parentId);

    /// <summary>How leftRunning names a process: its image name and its id, as "ping.exe (pid 1234)".</summary>
    internal static string Describe(ListedProcess process) => $"{process.Name} (pid {process.Id})";

    private static bool StartedSince(int processId, DateTime since, ILogger logger)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime >= since;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Gone since the snapshot, or not ours to open: either way not a process the game left running.
            Log.LeftRunningUnreadable(logger, e, processId);
            return false;
        }
    }

    private static List<ListedProcess> Snapshot(ILogger logger)
    {
        nint snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle)
        {
            int error = Marshal.GetLastPInvokeError();
            Log.ProcessListFailed(logger, nameof(CreateToolhelp32Snapshot), error);
            return [];
        }

        try
        {
            List<ListedProcess> processes = [];
            ProcessEntry entry = new() { Size = (uint)Unsafe.SizeOf<ProcessEntry>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                processes.Add(new ListedProcess(unchecked((int)entry.ProcessId), unchecked((int)entry.ParentProcessId), entry.Name));
            }

            int error = Marshal.GetLastPInvokeError();
            if (error != NoMoreFiles)
            {
                Log.ProcessListFailed(logger, nameof(Process32NextW), error);
            }

            return processes;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>One process in a snapshot: its id, its parent's id and its image name.</summary>
    internal readonly record struct ListedProcess(int Id, int ParentId, string Name);

    /// <summary><c>PROCESSENTRY32W</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public ExeFileBuffer ExeFile;

        public readonly string Name
        {
            get
            {
                ReadOnlySpan<char> chars = MemoryMarshal.Cast<ushort, char>((ReadOnlySpan<ushort>)ExeFile);
                int end = chars.IndexOf('\0');
                return new string(end < 0 ? chars : chars[..end]);
            }
        }
    }

    /// <summary><c>PROCESSENTRY32W.szExeFile</c>: <see cref="MaxPath"/> UTF-16 code units.</summary>
    [InlineArray(MaxPath)]
    private struct ExeFileBuffer
    {
        private ushort _first;
    }
}
