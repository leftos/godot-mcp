using System.ComponentModel;
using System.Diagnostics;

namespace GodotMcp.Server.Session;

/// <summary>
/// How the holders view tells whether an owner other than this server still runs, and which godot-mcp version a process runs.
/// </summary>
internal sealed record OwnerProbe(Func<OverrideOwner, bool> IsAlive, Func<int, string?> ServerVersionOf)
{
    /// <summary>The machine's processes: <see cref="OverrideOwner.IsAlive"/> and <see cref="FolderHolders.ServerVersionOf"/>.</summary>
    public static OwnerProbe Machine { get; } = new(static owner => owner.IsAlive(), FolderHolders.ServerVersionOf);
}

/// <summary>
/// The read-only view of who holds a project folder's override.cfg and armed.json: every entry of each file's owners line, and
/// whether stop_project with the folder would remove a file, judged by the judge it removes with
/// (<see cref="OverrideFile.KeepReason(string, Func{OverrideOwner, bool})"/>,
/// <see cref="ArmFile.KeepReason(string, Func{OverrideOwner, bool})"/>) without deleting. "Would remove" means the owners judge a
/// file stale; a handle another process holds open at stop time, or a busy owners list, still keeps it. It reads each file
/// through the judge's fully shared open and takes neither the owners list's hold nor the folder's prep lock, so a busy folder
/// never blocks it: its answer is a snapshot. It never throws: a file it cannot read is reported as <see cref="Unreadable"/>,
/// the error logged to stderr.
/// </summary>
internal static class FolderHolders
{
    public const string Absent = "absent";
    public const string ProjectsOwn = "project's own";
    public const string Marked = "godot-mcp";
    public const string InUse = "in use";
    public const string Unreadable = "unreadable";

    public const string NothingToRemove = "nothing to remove: the folder holds neither override.cfg nor armed.json";

    /// <summary>Who holds the folder's files, and whether stop_project would remove one.</summary>
    /// <param name="projectDir">The project folder, normalised.</param>
    /// <param name="sessions">This server's live sessions on the folder, the ones stop_project would stop there.</param>
    /// <param name="armed">Whether this server armed the folder.</param>
    /// <param name="probe">Whether an owner still runs, and its server version.</param>
    public static FolderHoldersView Inspect(string projectDir, IReadOnlyList<string> sessions, bool armed, OwnerProbe probe)
    {
        Judged overrideCfg = Judge(OverrideFile.FileName, OverrideFile.PathIn(projectDir), text => JudgeOverride(text, probe));
        Judged armedJson = Judge(ArmFile.FileName, ArmFile.PathIn(projectDir), text => JudgeArm(text, probe));
        (bool wouldRemove, string? reason) = Verdict(overrideCfg, armedJson, sessions);
        return new FolderHoldersView(
            projectDir,
            overrideCfg.State,
            armedJson.State,
            [.. overrideCfg.Owners, .. armedJson.Owners],
            sessions,
            armed,
            wouldRemove
        )
        {
            Reason = reason,
        };
    }

    /// <summary>How a stop left the folder: read from its files as they are now, with the machine's processes.</summary>
    public static FolderState After(string projectDir) => StateOf(Inspect(projectDir, [], false, OwnerProbe.Machine));

    /// <summary>
    /// The folder as stop_project reports it: clean when neither file is there, else the owners that read as running ones.
    /// </summary>
    public static FolderState StateOf(FolderHoldersView view) =>
        new(
            view.OverrideCfg == Absent && view.ArmedJson == Absent,
            [
                .. view
                    .Owners.Where(owner => owner is { Readable: true, Alive: true, Pid: not null })
                    .Select(owner => new FolderHolder(owner.File, owner.Pid!.Value, owner.StartedAt, owner.ServerVersion, owner.ThisServer)),
            ]
        );

    /// <summary>
    /// The godot-mcp version a process runs, best effort: the name of the <c>versions\&lt;name&gt;</c> folder of the install
    /// layout its executable runs from (<c>tools/InstallLayout.psm1</c>, <c>New-VersionFolder</c>); null when the process is gone,
    /// its executable cannot be read, or it does not run from such a folder.
    /// </summary>
    public static string? ServerVersionOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return VersionFolderOf(process.MainModule?.FileName);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The name of the folder directly under a <c>versions</c> folder that holds <paramref name="executable"/>; null when none does.
    /// </summary>
    internal static string? VersionFolderOf(string? executable)
    {
        for (string? folder = Path.GetDirectoryName(executable); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            if (string.Equals(Path.GetFileName(Path.GetDirectoryName(folder)), "versions", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileName(folder);
            }
        }

        return null;
    }

    /// <summary>
    /// Why stop_project with the folder would not clear its leftovers: it would stop the one live session there, or refuse
    /// between several.
    /// </summary>
    internal static string SessionReason(IReadOnlyList<string> sessions) =>
        sessions.Count == 1
            ? $"stop_project would stop this server's session {sessions[0]} on the folder, which releases its override.cfg, instead of "
                + "clearing leftovers"
            : $"stop_project would refuse: several of this server's sessions run on the folder ({string.Join(", ", sessions)}); stop one "
                + "by name";

    /// <summary>
    /// One file judged: read through the judge's shared open, then <paramref name="judgeText"/> on its text. A file another
    /// handle refuses to share is in use, and so is an empty one: godot-mcp never leaves one, but a read without the owners
    /// list's hold can land between a write's truncation and its text. A file whose read fails is unreadable, logged to stderr.
    /// </summary>
    private static Judged Judge(string file, string path, Func<string, Judged> judgeText)
    {
        SharedRead read;
        try
        {
            read = LeftoverFile.ReadShared(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"godot-mcp: reading {path} for the folder's holders failed: {e.GetType().Name}: {e.Message}");
            return new Judged(Unreadable, [], null) { Clause = $"{file} could not be read ({e.Message})" };
        }

        return read.Text switch
        {
            null when read.Exists => new Judged(InUse, [], new LeftoverRemoval(LeftoverState.InUse)),
            null => new Judged(Absent, [], null),
            "" => new Judged(InUse, [], null) { Clause = $"{file} is empty, as while godot-mcp writes it" },
            { } text => judgeText(text),
        };
    }

    private static Judged JudgeOverride(string text, OwnerProbe probe) =>
        OverrideFile.IsMarked(text)
            ? new Judged(Marked, Owners(OverrideFile.FileName, OverrideFile.OwnerEntries(text), probe), OverrideFile.KeepReason(text, probe.IsAlive))
            : new Judged(ProjectsOwn, [], new LeftoverRemoval(LeftoverState.NotOurs));

    private static Judged JudgeArm(string text, OwnerProbe probe) =>
        new(Marked, Owners(ArmFile.FileName, ArmFile.OwnerEntries(text), probe), ArmFile.KeepReason(text, probe.IsAlive));

    private static List<HolderEntry> Owners(string file, IReadOnlyList<string>? entries, OwnerProbe probe) =>
        [.. (entries ?? []).Select(entry => Entry(file, entry, probe))];

    private static HolderEntry Entry(string file, string entry, OwnerProbe probe)
    {
        if (!OverrideOwner.TryParse(entry, out OverrideOwner owner))
        {
            return new HolderEntry(file, null, null, Alive: true, ThisServer: false, null, Readable: false) { Entry = entry.Trim() };
        }

        bool thisServer = owner == OverrideOwner.Current;
        bool alive = thisServer || probe.IsAlive(owner);
        string? version = alive ? probe.ServerVersionOf(owner.ProcessId) : null;
        return new HolderEntry(file, owner.ProcessId, StartOf(owner.StartTicks), alive, thisServer, version, Readable: true);
    }

    /// <summary>The start time the entry records, in UTC; null for ticks past the calendar's end.</summary>
    private static DateTimeOffset? StartOf(long ticks) => ticks <= DateTimeOffset.MaxValue.UtcTicks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    /// <summary>Whether stop_project with the folder would remove a file, and why it would not, or which file it would keep.</summary>
    private static (bool WouldRemove, string? Reason) Verdict(Judged overrideCfg, Judged armedJson, IReadOnlyList<string> sessions)
    {
        if (sessions.Count > 0)
        {
            return (false, SessionReason(sessions));
        }

        if (!overrideCfg.Present && !armedJson.Present)
        {
            return (false, NothingToRemove);
        }

        string[] kept = [.. new[] { Kept(OverrideFile.FileName, overrideCfg), Kept(ArmFile.FileName, armedJson) }.OfType<string>()];
        return (overrideCfg.Removable || armedJson.Removable, kept.Length == 0 ? null : string.Join("; ", kept));
    }

    private static string? Kept(string file, Judged judged) =>
        (judged.Clause ?? (judged.Keep is { } keep ? OverrideFolders.LeftClause(file, keep) : null)) is { } clause
            ? $"{clause}, so stop_project would keep it"
            : null;

    /// <summary>
    /// A file's state, its owners, and why the judge keeps it (null when it is absent or stale), or the clause saying why it
    /// cannot be judged.
    /// </summary>
    private sealed record Judged(string State, IReadOnlyList<HolderEntry> Owners, LeftoverRemoval? Keep)
    {
        /// <summary>Why a file that could not be judged is kept: it is empty, or its read failed; null when it was judged.</summary>
        public string? Clause { get; init; }

        public bool Present => State != Absent;

        public bool Removable => Present && Keep is null && Clause is null;
    }
}
