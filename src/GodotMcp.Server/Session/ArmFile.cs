using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GodotMcp.Server.Session;

/// <summary>How an armed folder's games run: whether they park their window and whether they shut the real pads out.</summary>
internal sealed record ArmSettings(bool Quiet, bool ShutOutRealGamepads);

/// <summary>
/// The file that arms a project folder: <c>{quiet, shutOutRealGamepads, owners}</c> at
/// <c>&lt;project&gt;/.godot/godot-mcp/armed.json</c>. While it exists, a game started on the folder with no server to dial
/// keeps its bridge dormant, waiting for a join file, instead of freeing it. Its owners are the server processes that armed the
/// folder, each as <see cref="OverrideOwner"/> writes it, so a server disarming it deletes the file only when no other live
/// server still has it armed, and a file whose owners have all exited (or that lists none) is stale.
/// </summary>
internal static class ArmFile
{
    public static readonly string RelativePath = Path.Combine(".godot", "godot-mcp", "armed.json");

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string PathIn(string projectDir) => Path.Combine(projectDir, RelativePath);

    /// <summary>
    /// Writes the file with <paramref name="settings"/> and this server among its owners, keeping the live owners of the file it
    /// replaces and dropping the ones that have exited; creates <c>.godot/godot-mcp/</c> when it is missing.
    /// </summary>
    public static void Write(string projectDir, ArmSettings settings)
    {
        string path = PathIn(projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        List<OverrideOwner> owners = LiveOthers(Read(projectDir)?.Owners ?? []);
        owners.Add(OverrideOwner.Current);
        WriteFile(path, settings, owners);
    }

    /// <summary>
    /// Takes this server off the file's owners: deletes the file when no live owner is left, else rewrites it with the live
    /// owners left, its settings unchanged.
    /// </summary>
    /// <returns>Whether it deleted the file.</returns>
    public static bool Release(string projectDir)
    {
        if (Read(projectDir) is not { } armed)
        {
            return false;
        }

        List<OverrideOwner> remaining = LiveOthers(armed.Owners);
        string path = PathIn(projectDir);
        if (remaining.Count == 0)
        {
            File.Delete(path);
            return true;
        }

        if (!remaining.SequenceEqual(armed.Owners))
        {
            WriteFile(path, armed.Settings, remaining);
        }

        return false;
    }

    /// <summary>
    /// The folder's file: its settings and owners; null when there is none. A file that is not the expected JSON reads as one
    /// with both settings false and no owners, so it counts as stale.
    /// </summary>
    public static ArmedFile? Read(string projectDir)
    {
        string path = PathIn(projectDir);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Parse(JsonNode.Parse(File.ReadAllText(path)) as JsonObject);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return new ArmedFile(new ArmSettings(false, false), []);
        }
    }

    /// <summary>The file's owners that still run, this server included; empty when there is no file or it is stale.</summary>
    public static IReadOnlyList<OverrideOwner> LiveOwners(string projectDir) =>
        Read(projectDir) is { } armed ? [.. armed.Owners.Where(owner => owner == OverrideOwner.Current || owner.IsAlive()).Distinct()] : [];

    private static ArmedFile Parse(JsonObject? content)
    {
        if (content is null)
        {
            return new ArmedFile(new ArmSettings(false, false), []);
        }

        bool quiet = content["quiet"]?.GetValue<bool>() ?? false;
        bool shutOut = content["shutOutRealGamepads"]?.GetValue<bool>() ?? false;
        return new ArmedFile(new ArmSettings(quiet, shutOut), ParseOwners(content["owners"] as JsonArray));
    }

    /// <summary>The listed owners; an entry that is not an owner is skipped.</summary>
    private static List<OverrideOwner> ParseOwners(JsonArray? listed)
    {
        List<OverrideOwner> owners = [];
        foreach (JsonNode? entry in listed ?? [])
        {
            if (entry is JsonValue value && value.TryGetValue(out string? text) && OverrideOwner.TryParse(text, out OverrideOwner owner))
            {
                owners.Add(owner);
            }
        }

        return owners;
    }

    private static void WriteFile(string path, ArmSettings settings, IEnumerable<OverrideOwner> owners)
    {
        JsonObject content = new()
        {
            ["quiet"] = settings.Quiet,
            ["shutOutRealGamepads"] = settings.ShutOutRealGamepads,
            ["owners"] = new JsonArray([.. owners.Select(owner => (JsonNode)JsonValue.Create(owner.ToString()))]),
        };
        File.WriteAllText(path, content.ToJsonString() + "\n", Utf8NoBom);
    }

    /// <summary>The owners that still run, leaving this server out.</summary>
    private static List<OverrideOwner> LiveOthers(IEnumerable<OverrideOwner> owners) =>
        [.. owners.Where(owner => owner != OverrideOwner.Current && owner.IsAlive()).Distinct()];
}

/// <summary>An armed.json as read: the folder's settings and the servers that armed it.</summary>
internal sealed record ArmedFile(ArmSettings Settings, IReadOnlyList<OverrideOwner> Owners);
