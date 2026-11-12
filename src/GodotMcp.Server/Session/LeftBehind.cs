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
    /// The game's child processes still running, each as <see cref="Describe"/> writes it (<see cref="FindProcesses"/>).
    /// </summary>
    /// <param name="game">The game's process, with its handle still open so its start time stays readable.</param>
    /// <param name="logger">Where a failed read is logged.</param>
    public static IReadOnlyList<string> Find(Process game, ILogger logger) => [.. FindProcesses(game, logger).Select(Describe)];

    /// <summary>
    /// The game's child processes still running, with their ids and image names. Call it before the console wrapper is ended,
    /// whose job ends them. A list that cannot be read is logged at Debug and comes back empty.
    /// </summary>
    /// <param name="game">The game's process, with its handle still open so its start time stays readable.</param>
    /// <param name="logger">Where a failed read is logged.</param>
    public static IReadOnlyList<ListedProcess> FindProcesses(Process game, ILogger logger) => ListChildren(game, logger).Children;

    /// <summary>
    /// The game's child processes still running, as <see cref="FindProcesses"/> lists them, with the time read just before the
    /// listing was taken: a process that started after it is not one the listing saw.
    /// </summary>
    /// <param name="game">The game's process, with its handle still open so its start time stays readable.</param>
    /// <param name="logger">Where a failed read is logged.</param>
    public static ChildListing ListChildren(Process game, ILogger logger)
    {
        if (!OperatingSystem.IsWindows() || StartTimeOf(game, logger) is not { } gameStart)
        {
            return new ChildListing([], DateTime.UtcNow);
        }

        DateTime listedAt = DateTime.UtcNow;
        return new ChildListing([.. ChildrenOf(Snapshot(logger), game.Id).Where(child => StartedSince(child.Id, gameStart, logger))], listedAt);
    }

    /// <summary>
    /// Opens a handle on each process of <paramref name="listing"/> that still runs and started between <paramref name="game"/>'s
    /// start and the listing, its creation time read through the handle itself. A listed child that exited since the listing may
    /// have had its id taken by another process, which started after the listing and so is never opened; a child of the game
    /// started in the same instant as the listing may be missed with it. One that cannot be opened is logged at Debug and left
    /// out. The caller disposes every process returned; none is left open when this throws.
    /// </summary>
    /// <param name="game">The game's process, with its handle still open so its start time stays readable.</param>
    /// <param name="listing">The game's children, as <see cref="ListChildren"/> listed them.</param>
    /// <param name="logger">Where a failed open is logged.</param>
    public static List<Process> Open(Process game, ChildListing listing, ILogger logger)
    {
        List<Process> opened = [];
        if (listing.Children.Count == 0 || StartTimeOf(game, logger) is not { } gameStart)
        {
            return opened;
        }

        try
        {
            foreach (ListedProcess child in listing.Children)
            {
                if (OpenStartedBetween(child.Id, gameStart, listing.ListedAt, logger) is { } process)
                {
                    opened.Add(process);
                }
            }
        }
        catch
        {
            foreach (Process process in opened)
            {
                process.Dispose();
            }

            throw;
        }

        return opened;
    }

    /// <summary>The listed processes whose parent is <paramref name="parentId"/>.</summary>
    internal static IEnumerable<ListedProcess> ChildrenOf(IEnumerable<ListedProcess> processes, int parentId) =>
        processes.Where(process => process.ParentId == parentId);

    /// <summary>How leftRunning names a process: its image name and its id, as "ping.exe (pid 1234)".</summary>
    internal static string Describe(ListedProcess process) => $"{process.Name} (pid {process.Id})";

    /// <summary>The game's creation time, in UTC; null, logged at Debug, when it cannot be read.</summary>
    private static DateTime? StartTimeOf(Process game, ILogger logger)
    {
        try
        {
            return game.StartTime.ToUniversalTime();
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            Log.LeftRunningUnreadable(logger, e, game.Id);
            return null;
        }
    }

    /// <summary>
    /// The process with <paramref name="processId"/>, its handle opened and kept on it before its creation time is read, when it
    /// started at or after <paramref name="since"/> and no later than <paramref name="until"/>, and has not exited; null otherwise.
    /// </summary>
    private static Process? OpenStartedBetween(int processId, DateTime since, DateTime until, ILogger logger)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            // Reading Handle opens the process handle and keeps it on the object, so every later read and wait is of this process.
            _ = process.Handle;
            DateTime started = process.StartTime.ToUniversalTime();
            if (started < since || started > until || process.HasExited)
            {
                return null;
            }

            Process opened = process;
            process = null;
            return opened;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Gone since the listing, or not ours to open: either way not a process the stop can wait for.
            Log.LeftRunningUnreadable(logger, e, processId);
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static bool StartedSince(int processId, DateTime since, ILogger logger)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime() >= since;
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

    /// <summary>
    /// The game's children a listing found, and the time, in UTC, read just before it was taken: every start time it is compared
    /// with is read in UTC too, so a clock change between them cannot move a process across it.
    /// </summary>
    internal readonly record struct ChildListing(IReadOnlyList<ListedProcess> Children, DateTime ListedAt);

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
