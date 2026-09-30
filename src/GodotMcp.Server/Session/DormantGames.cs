using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GodotMcp.Server.Session;

/// <summary>A game on an armed folder whose bridge waits to be joined: its process id and when its bridge started.</summary>
internal sealed record DormantGame(int Pid, DateTimeOffset StartedAt);

/// <summary>
/// What the process boundary says about a process id: whether a process has it, and when that process started (null when the
/// start time cannot be read, as for another user's process).
/// </summary>
internal readonly record struct ProcessStart(bool Exists, DateTimeOffset? StartTime);

/// <summary>
/// The games waiting on an armed folder, each announced by its dormant bridge as <c>{pid, startedUnixMs}</c> in
/// <c>&lt;project&gt;/.godot/godot-mcp/dormant/&lt;pid&gt;.json</c>, and the one-use join file that wakes one:
/// <c>join-&lt;pid&gt;.json</c> beside <c>attach.json</c>, in its shape. An entry whose process has exited, or whose process
/// started more than <see cref="StartTolerance"/> after its bridge did (the id was reused), is deleted when listed.
/// </summary>
/// <param name="probe">Reads a process's existence and start time: <see cref="ProbeProcess"/>, or a test's fake.</param>
/// <param name="errors">Where an unreadable entry is reported: stderr in the server.</param>
internal sealed class DormantGames(Func<int, ProcessStart> probe, TextWriter errors)
{
    /// <summary>How much later than its bridge's start a process may seem to start and still be the game that wrote the entry.</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The real process boundary, reporting to stderr.</summary>
    public static DormantGames Default { get; } = new(ProbeProcess, Console.Error);

    public static string FolderIn(string projectDir) => Path.Combine(projectDir, ".godot", "godot-mcp", "dormant");

    public static string JoinPathIn(string projectDir, int pid) =>
        Path.Combine(projectDir, ".godot", "godot-mcp", string.Create(CultureInfo.InvariantCulture, $"join-{pid}.json"));

    /// <summary>
    /// The folder's dormant games, ordered by process id. A dead or reused entry is deleted and left out; an entry that cannot
    /// be read is reported and left out, but kept, since its bridge may be writing it.
    /// </summary>
    public IReadOnlyList<DormantGame> List(string projectDir)
    {
        string folder = FolderIn(projectDir);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        List<DormantGame> games = [];
        foreach (string file in Directory.GetFiles(folder, "*.json"))
        {
            if (ReadEntry(file) is { } game && KeepOrPrune(file, game))
            {
                games.Add(game);
            }
        }

        return [.. games.OrderBy(game => game.Pid)];
    }

    /// <summary>
    /// Writes the join file that wakes the dormant game <paramref name="pid"/>, replacing an older one, atomically: the game
    /// never finds it half-written.
    /// </summary>
    public static void WriteJoinFile(string projectDir, int pid, BridgeEndpoint endpoint, ArmSettings settings)
    {
        string path = JoinPathIn(projectDir, pid);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Written whole under another name, then moved into place: the bridge deletes a join file it cannot parse.
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, AttachFile.EndpointJson(endpoint, settings), Utf8NoBom);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Deletes the join file of <paramref name="pid"/>; returns whether there was one.</summary>
    public static bool RemoveJoinFile(string projectDir, int pid)
    {
        string path = JoinPathIn(projectDir, pid);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>Whether a process has the id, and its start time when it can be read.</summary>
    public static ProcessStart ProbeProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new ProcessStart(true, StartTimeOf(process));
        }
        catch (ArgumentException)
        {
            // No process has the id.
            return new ProcessStart(false, null);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the lookup and the read of its start time.
            return new ProcessStart(false, null);
        }
    }

    private static DateTimeOffset? StartTimeOf(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The entry the file holds; null, reported, when it cannot be read or is not an entry, and null when it is gone.</summary>
    private DormantGame? ReadEntry(string file)
    {
        try
        {
            return ParseEntry(file, File.ReadAllText(file));
        }
        catch (FileNotFoundException)
        {
            // The bridge deleted it after the listing: it joined, exited or went idle.
            return null;
        }
        catch (Exception e) when (IsUnreadable(e))
        {
            errors.WriteLine($"godot-mcp: skipping the dormant game entry {file}: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>What reading an entry throws when the file cannot be read or does not hold an entry.</summary>
    private static bool IsUnreadable(Exception e) =>
        e
            is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidOperationException
                or FormatException
                or ArithmeticException
                or ArgumentException;

    /// <summary>The entry in <paramref name="text"/>, whose pid must be the one its file is named after.</summary>
    /// <exception cref="FormatException">The text is not <c>{pid, startedUnixMs}</c> with integral numbers, or names another pid.</exception>
    private static DormantGame ParseEntry(string file, string text)
    {
        JsonObject content = JsonNode.Parse(text) as JsonObject ?? throw new FormatException("the entry is not a JSON object");
        int pid = checked((int)Integral(content["pid"], "pid"));
        long startedUnixMs = Integral(content["startedUnixMs"], "startedUnixMs");
        string named = Path.GetFileNameWithoutExtension(file);
        if (named != pid.ToString(CultureInfo.InvariantCulture))
        {
            throw new FormatException($"the entry names pid {pid} but its file is {named}.json");
        }

        return new DormantGame(pid, DateTimeOffset.FromUnixTimeMilliseconds(startedUnixMs));
    }

    /// <summary>A whole number, which GDScript's JSON may write as a float such as <c>1234.0</c>.</summary>
    private static long Integral(JsonNode? node, string key)
    {
        if (node is JsonValue value && value.TryGetValue(out double number) && number == Math.Floor(number) && Math.Abs(number) < long.MaxValue)
        {
            return (long)number;
        }

        throw new FormatException($"{key} is not a whole number");
    }

    /// <summary>Whether the entry's game still runs; a dead or reused entry's file is deleted.</summary>
    private bool KeepOrPrune(string file, DormantGame game)
    {
        ProcessStart start = probe(game.Pid);
        bool reused = start.StartTime is { } started && started > game.StartedAt + StartTolerance;
        if (start.Exists && !reused)
        {
            return true;
        }

        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errors.WriteLine($"godot-mcp: removing the stale dormant game entry {file} failed: {e.GetType().Name}: {e.Message}");
        }

        return false;
    }
}
