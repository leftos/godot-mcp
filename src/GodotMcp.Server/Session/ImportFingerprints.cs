using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// The prep's own record of every import sidecar's settings, in <c>.godot/godot-mcp/import-fingerprints.json</c>: the md5 of each
/// <c>.import</c> file's bytes, by the sidecar's path relative to the project. Godot reimports a sidecar whose recorded md5 in the
/// editor's file cache differs from the file's own md5 (4.7.2 <c>editor/file_system/editor_file_system.cpp</c> L673-760), and a
/// run outside the editor cannot read that cache, so the prep keeps its own: a pull that changed only an asset's import settings
/// would otherwise get no reimport.
/// </summary>
internal static partial class ImportFingerprints
{
    private const string FileName = "import-fingerprints.json";

    /// <summary>A record with no md5 in it, for a project nothing has recorded yet.</summary>
    public static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    private static readonly JsonSerializerOptions Written = new() { WriteIndented = true };

    /// <summary>The record's path in a project, inside the server's own folder, which <c>.godot/</c> already git-ignores.</summary>
    public static string PathIn(string projectDir) => Path.Combine(ProjectPrep.LogFolder(projectDir), FileName);

    /// <summary>A sidecar's key in the record: its path relative to the project, with forward slashes.</summary>
    public static string KeyFor(string projectDir, string sidecar) => Path.GetRelativePath(projectDir, sidecar).Replace('\\', '/');

    /// <summary>The md5 of a file's bytes, in the lowercase hex the record holds.</summary>
    [SuppressMessage(
        "Security",
        "CA5351:Do not use broken cryptographic algorithms",
        Justification = "The md5 of a .import file is what Godot itself records for one; nothing here is a security use."
    )]
    public static string Md5Of(string file) => Convert.ToHexStringLower(MD5.HashData(File.ReadAllBytes(file)));

    /// <summary>
    /// The md5 recorded for each sidecar, by its project-relative path with forward slashes; empty when there is no record, and
    /// empty and logged when one cannot be read or parsed, which the next write rewrites.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read(string projectDir, ILogger logger)
    {
        string path = PathIn(projectDir);
        if (!File.Exists(path))
        {
            return None;
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            FingerprintsUnreadable(logger, e, path);
            return None;
        }
    }

    /// <summary>
    /// Saves the record a finished prep leaves, and writes nothing when it would come out the same: after an import the prep ran
    /// (<paramref name="state"/> "done") the md5 of every current sidecar, and after a prep that found none due ("not-needed") the
    /// sidecars <paramref name="record"/> has no md5 for, keeping the ones it has, so a later settings change is caught; a sidecar
    /// that is gone loses its entry either way. Any other state, a failed or skipped import, writes nothing.
    /// </summary>
    /// <param name="record">What <see cref="Read"/> gave the prep, rather than read again.</param>
    public static void Save(
        string projectDir,
        IReadOnlyList<string> sidecars,
        string state,
        IReadOnlyDictionary<string, string> record,
        ILogger logger
    )
    {
        IReadOnlyDictionary<string, string>? next = state switch
        {
            "done" => Of(projectDir, sidecars),
            "not-needed" => Baseline(projectDir, record, sidecars),
            _ => null,
        };
        if (next is null || Same(record, next))
        {
            return;
        }

        Write(projectDir, next, logger);
    }

    /// <summary>The md5s a record's text holds.</summary>
    /// <exception cref="JsonException">The text is not a JSON object of md5 strings.</exception>
    private static Dictionary<string, string> Parse(string text)
    {
        if (JsonNode.Parse(text) is not JsonObject record)
        {
            throw new JsonException("the record is not a JSON object");
        }

        Dictionary<string, string> md5s = [];
        foreach ((string sidecar, JsonNode? md5) in record)
        {
            md5s[sidecar] = md5?.GetValue<string>() is { Length: > 0 } value
                ? value
                : throw new JsonException($"the record's '{sidecar}' is not an md5");
        }

        return md5s;
    }

    /// <summary>The md5 of every sidecar that is there.</summary>
    private static Dictionary<string, string> Of(string projectDir, IReadOnlyList<string> sidecars)
    {
        Dictionary<string, string> md5s = [];
        foreach (string sidecar in sidecars.Where(File.Exists))
        {
            md5s[KeyFor(projectDir, sidecar)] = Md5Of(sidecar);
        }

        return md5s;
    }

    /// <summary>The record with an entry for every sidecar it has none for, and without the entries of sidecars that are gone.</summary>
    private static Dictionary<string, string> Baseline(string projectDir, IReadOnlyDictionary<string, string> record, IReadOnlyList<string> sidecars)
    {
        Dictionary<string, string> next = [];
        foreach (string sidecar in sidecars.Where(File.Exists))
        {
            string key = KeyFor(projectDir, sidecar);
            next[key] = record.GetValueOrDefault(key) ?? Md5Of(sidecar);
        }

        return next;
    }

    /// <summary>Whether two records hold the same md5s, whatever their order.</summary>
    private static bool Same(IReadOnlyDictionary<string, string> one, IReadOnlyDictionary<string, string> other) =>
        one.Count == other.Count && one.All(entry => other.TryGetValue(entry.Key, out string? md5) && md5 == entry.Value);

    /// <summary>Writes the record over the old one through a temp file beside it, creating the folder when it is missing.</summary>
    private static void Write(string projectDir, IReadOnlyDictionary<string, string> record, ILogger logger)
    {
        string path = PathIn(projectDir);
        string folder = Path.GetDirectoryName(path)!;
        string temp = Path.Combine(folder, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(temp, JsonSerializer.Serialize(record, Written));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            FingerprintsUnwritable(logger, e, path);
        }
        finally
        {
            DeleteTemp(temp, logger);
        }
    }

    /// <summary>Deletes the write's temp file, which a failed write left behind; a failure to delete is logged.</summary>
    private static void DeleteTemp(string temp, ILogger logger)
    {
        if (!File.Exists(temp))
        {
            return;
        }

        try
        {
            File.Delete(temp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            FingerprintsTempLeft(logger, e, temp);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Record} is not a record of import fingerprints, so the prep takes no sidecar as recorded; the next write rewrites it."
    )]
    private static partial void FingerprintsUnreadable(ILogger logger, Exception exception, string record);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Record} could not be written, so the record stays as it was.")]
    private static partial void FingerprintsUnwritable(ILogger logger, Exception exception, string record);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Temp} could not be deleted after a failed write of the import fingerprints.")]
    private static partial void FingerprintsTempLeft(ILogger logger, Exception exception, string temp);
}
