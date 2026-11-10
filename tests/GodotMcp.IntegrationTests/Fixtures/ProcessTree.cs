using System.ComponentModel;
using System.Diagnostics;
using GodotMcp.Server.Session;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>
/// Stops a process the test started together with every process under it and waits until each is gone. A console wrapper
/// such as Godot_console.exe exits on its kill while its Godot.exe child still runs and holds the project folder, so a wait
/// on the wrapper's handle alone returns too early; and a kill only starts a process's end (TerminateProcess "initiates
/// termination and returns immediately"), so each process is gone only once a wait on its own handle returns.
/// </summary>
internal static class ProcessTree
{
    /// <summary>
    /// Kills <paramref name="root"/> and its descendants and waits on each one's handle, each wait bounded by
    /// <paramref name="hangGuard"/>, a guard a passing run never reaches. <paramref name="root"/> stays its caller's to dispose.
    /// </summary>
    /// <returns>The pids waited on, root first.</returns>
    /// <exception cref="InvalidOperationException">A process of the tree still runs after <paramref name="hangGuard"/>.</exception>
    internal static async Task<IReadOnlyList<int>> KillAndWaitAsync(Process root, TimeSpan hangGuard)
    {
        // A child spawned between the snapshot and the kill is killed by the tree kill but not waited on; Godot_console.exe
        // spawns its Godot.exe only at startup, long before a test stops it.
        List<Process> descendants = OpenDescendants(root);
        try
        {
            List<(Process Process, string Name)> tree = [(root, NameOf(root)), .. descendants.Select(child => (child, NameOf(child)))];
            foreach ((Process process, _) in tree)
            {
                Kill(process);
            }

            foreach ((Process process, string name) in tree)
            {
                await WaitUntilGoneAsync(process, name, root.Id, hangGuard);
            }

            return [.. tree.Select(entry => entry.Process.Id)];
        }
        finally
        {
            foreach (Process child in descendants)
            {
                child.Dispose();
            }
        }
    }

    /// <summary>
    /// Every living descendant of <paramref name="root"/> in a Toolhelp snapshot, each opened with its handle read at once so its
    /// pid cannot be reused, as <see cref="Walk"/> keeps them.
    /// </summary>
    private static List<Process> OpenDescendants(Process root)
    {
        DateTime rootStart;
        try
        {
            rootStart = root.StartTime;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return [];
        }

        // Every process opened during the walk; the kept ones leave it, and whatever is left, a rejected row's or every one
        // when the walk throws partway, is disposed.
        Dictionary<int, Process> opened = [];
        try
        {
            List<int> kept = Walk(root.Id, rootStart, CaptureWindows.Processes(), pid => OpenAndReadStart(pid, opened));
            List<Process> descendants = [.. kept.Select(pid => opened[pid])];
            foreach (int pid in kept)
            {
                opened.Remove(pid);
            }

            return descendants;
        }
        finally
        {
            foreach (Process process in opened.Values)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// The pids under <paramref name="rootId"/> in <paramref name="rows"/>, parents before children. A row is kept, and its own
    /// children walked, only when <paramref name="startOf"/> gives its start time and that is no earlier than the start of the
    /// process it was found under, as .NET's own tree kill checks: Toolhelp's parent id can name a reused pid. A row
    /// <paramref name="startOf"/> gives no start for is gone and holds nothing; its children's own check protects them.
    /// </summary>
    internal static List<int> Walk(int rootId, DateTime rootStart, IReadOnlyList<CaptureWindows.ProcessRow> rows, Func<int, DateTime?> startOf)
    {
        List<int> kept = [];
        HashSet<int> seen = [rootId];
        Queue<(int Id, DateTime Start)> parents = new([(rootId, rootStart)]);
        while (parents.TryDequeue(out (int Id, DateTime Start) parent))
        {
            foreach (CaptureWindows.ProcessRow row in rows.Where(row => row.ParentId == parent.Id && seen.Add(row.Id)))
            {
                if (startOf(row.Id) is { } start && start >= parent.Start)
                {
                    kept.Add(row.Id);
                    parents.Enqueue((row.Id, start));
                }
            }
        }

        return kept;
    }

    /// <summary>
    /// Opens <paramref name="pid"/>, reading its handle at once so the pid cannot be reused, adds it to
    /// <paramref name="opened"/> and returns its start time; null when it is gone or cannot be opened.
    /// </summary>
    private static DateTime? OpenAndReadStart(int pid, Dictionary<int, Process> opened)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            _ = process.Handle;
            DateTime start = process.StartTime;
            opened.Add(pid, process);
            process = null;
            return start;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Gone, or not openable, between the snapshot and the open: it holds nothing.
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string NameOf(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            return "(exited)";
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // No process is associated with the object any more.
        }
        catch (AggregateException)
        {
            // A process of its tree could not be killed, often one that is already ending; the wait below fails as hung if
            // one truly survives.
        }
        catch (Win32Exception)
        {
            // The kill was refused, often because the process is already ending; the wait below fails as hung if it survives.
        }
    }

    private static async Task WaitUntilGoneAsync(Process process, string name, int rootId, TimeSpan hangGuard)
    {
        int id = process.Id;
        if (!await ProcessExit.WaitUntilGoneAsync(process, hangGuard))
        {
            throw new InvalidOperationException(
                $"the process {name} (pid {id}) in the tree of pid {rootId} still runs {hangGuard.TotalSeconds:0} s after it was killed: hung"
            );
        }
    }
}
