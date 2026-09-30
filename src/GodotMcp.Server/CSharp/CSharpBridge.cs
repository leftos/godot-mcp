using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;

namespace GodotMcp.Server.CSharp;

/// <summary>The helper's answer to one request, and whether this call was the one that loaded the extension into the game.</summary>
internal sealed record CSharpReply(JsonNode? Result, bool LoadedNow);

/// <summary>
/// What get_game_state tells the bridge about a project's C#: the path of the helper's copy that reads its C# nodes, or
/// why the helper cannot run in a project that has C#; neither in a project without.
/// </summary>
internal sealed record StateHelper(string? Extension, string? Error)
{
    /// <summary>A project with no C#.</summary>
    public static StateHelper None { get; } = new(null, null);
}

/// <summary>
/// The server's half of the bridge's <c>dotnet</c> command: whether the project may be asked at all
/// (<see cref="Refusal"/>), the copy of the helper the game loads (<see cref="HelperCache"/>), and the helper's reply.
/// </summary>
internal sealed class CSharpBridge(HelperCache cache, Func<string?> findExtension)
{
    /// <summary>The folder beside the extension that holds the helper's managed dlls (run.ps1's <c>dotnet</c> lays it out).</summary>
    private const string HelperFolderName = "helper";

    /// <summary>How long the bridge polls a pending call when the tool gives no timeoutMs: the bridge's own DEFAULT_TIMEOUT_MS.</summary>
    internal const int DefaultPendingTimeoutMs = 10_000;

    private readonly ConcurrentDictionary<string, PreparedCopy> _copies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Why the project cannot answer a C# tool, or null when it can: no C# assembly, a C# build the prep skipped, an
    /// assembly that was never built, or a server that ships no helper.
    /// </summary>
    public static string? Refusal(string projectDir, string? extensionPath) => Refusal(projectDir, PrepScan.FindCsproj(projectDir), extensionPath);

    private static string? Refusal(string projectDir, CsprojLookup lookup, string? extensionPath)
    {
        // The note covers both a named assembly whose csproj is missing and several csprojs to choose from.
        if (lookup.Note is not null)
        {
            return lookup.Note;
        }

        if (lookup.Kind == CsprojKind.None)
        {
            return "This project has no C# assembly, so the C# tools cannot reach it.";
        }

        string assembly = PrepScan.AssemblyPath(projectDir, lookup.AssemblyName!);
        if (!File.Exists(assembly))
        {
            return $"The project's C# assembly is not built ({assembly}): build the project, then run it with run_project or restart_project.";
        }

        return extensionPath is null
            ? "The C# helper is not built: run 'pwsh run.ps1 dotnet' in the godot-mcp checkout, or install a published server."
            : null;
    }

    /// <summary>The helper's reply to one request, as the game's <c>dotnet</c> answer carries it.</summary>
    /// <exception cref="InvalidOperationException">The helper refused the request, or its reply is not JSON.</exception>
    public static CSharpReply ParseReply(JsonNode bridgeResult)
    {
        JsonObject envelope = Parse(ReplyText(bridgeResult));
        return Succeeded(envelope)
            ? new CSharpReply(envelope["result"], LoadedNow(bridgeResult))
            : throw new InvalidOperationException($"The C# helper refused the request: {ErrorMessage(envelope)}");
    }

    private static string ReplyText(JsonNode bridgeResult) =>
        bridgeResult["reply"]?.GetValue<string>() ?? throw new InvalidOperationException("The C# helper's reply carries no 'reply' string.");

    private static bool LoadedNow(JsonNode bridgeResult) => bridgeResult["loadedNow"]?.GetValue<bool>() ?? false;

    private static bool Succeeded(JsonObject envelope) => envelope["ok"] is JsonValue ok && ok.TryGetValue(out bool succeeded) && succeeded;

    private static string ErrorMessage(JsonObject envelope) =>
        envelope["error"] is JsonValue message && message.TryGetValue(out string? text) ? text : "no message";

    /// <summary>
    /// Sends one helper request through the bridge, copying the helper into the cache first. <paramref name="timeoutMs"/> is how
    /// long the server waits for the reply; <paramref name="pendingTimeoutMs"/> (<see cref="DefaultPendingTimeoutMs"/> when null)
    /// is how long the bridge polls a call the helper answers as pending, and the request's release: both in load-adjusted
    /// time, after which the server cancels the call and the bridge forgets it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The project cannot be asked, the helper's copy could not be prepared, the bridge answered nothing, or the helper
    /// refused or answered badly.
    /// </exception>
    public async Task<CSharpReply> SendAsync(
        GodotSession session,
        string requestJson,
        int timeoutMs,
        int? pendingTimeoutMs,
        CancellationToken cancellation
    )
    {
        string? extension = findExtension();
        if (Refusal(session.ProjectDir, extension) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        string copied = PrepareCopy(extension!);
        int pending = pendingTimeoutMs ?? DefaultPendingTimeoutMs;
        JsonObject parameters = new()
        {
            ["extension"] = copied,
            ["request"] = requestJson,
            ["timeoutMs"] = pending,
        };
        var release = TimeSpan.FromMilliseconds(pending);
        JsonNode? result =
            await session.SendAsync("dotnet", parameters, TimeSpan.FromMilliseconds(timeoutMs), cancellation, release)
            ?? throw new InvalidOperationException("The C# helper's reply is missing: the bridge answered no result for 'dotnet'.");
        return ParseReply(result);
    }

    /// <summary>
    /// The helper for a request that reads C# only when the project has some (get_game_state's), never a refusal: nothing
    /// for a project with no csproj; the path of the helper's copy, made when it is not there yet; or, in a project with
    /// C#, why the helper cannot run: what <see cref="Refusal"/> names (several csprojs, an unbuilt assembly, no helper
    /// build) or why the copy could not be made, which is logged too. The copy is found again without hashing the helper
    /// while no file of the helper's build has changed its path, size or write time.
    /// </summary>
    public StateHelper PrepareForState(string projectDir)
    {
        CsprojLookup lookup = PrepScan.FindCsproj(projectDir);
        if (lookup.Kind == CsprojKind.None && lookup.Note is null)
        {
            return StateHelper.None;
        }

        string? extension = findExtension();
        if (Refusal(projectDir, lookup, extension) is { } refusal)
        {
            return new StateHelper(null, refusal);
        }

        try
        {
            return new StateHelper(CachedCopy(extension!), null);
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine($"godot-mcp: get_game_state reads no C# state: {e.Message}");
            return new StateHelper(null, e.Message);
        }
    }

    /// <summary>
    /// The folder of the helper's managed dlls inside the copy the game loads, making the copy when it is not there yet; a
    /// snippet compiles against those dlls.
    /// </summary>
    /// <exception cref="InvalidOperationException">The project cannot be asked, or the helper's copy could not be prepared.</exception>
    public string PrepareHelperFolder(string projectDir)
    {
        string? extension = findExtension();
        if (Refusal(projectDir, extension) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        return Path.Combine(Path.GetDirectoryName(PrepareCopy(extension!))!, HelperFolderName);
    }

    /// <summary>
    /// The copy of the helper the game loads. A file-system error here becomes an <see cref="InvalidOperationException"/>, so
    /// the caller's catch for a dropped socket never reports it as the connection to the game ending.
    /// </summary>
    private string PrepareCopy(string extension)
    {
        try
        {
            return cache.Prepare(Path.GetDirectoryName(extension)!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Preparing the C# helper's copy failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// <see cref="PrepareCopy"/>'s path, kept per helper build folder beside the <see cref="Stamp"/> it was made at, so a
    /// read while the build is unchanged costs a listing of its files instead of a hash of their bytes.
    /// </summary>
    /// <exception cref="InvalidOperationException">The helper's build could not be listed, or its copy could not be prepared.</exception>
    private string CachedCopy(string extension)
    {
        string sourceDir = Path.GetDirectoryName(extension)!;
        string stamp;
        try
        {
            stamp = Stamp(sourceDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Preparing the C# helper's copy failed: {e.Message}", e);
        }

        if (
            _copies.TryGetValue(sourceDir, out PreparedCopy? kept)
            && kept.Stamp == stamp
            && HelperCache.IsComplete(Path.GetDirectoryName(kept.Path)!)
        )
        {
            return kept.Path;
        }

        string prepared = PrepareCopy(extension);
        _copies[sourceDir] = new PreparedCopy(stamp, prepared);
        return prepared;
    }

    /// <summary>Every file under <paramref name="sourceDir"/> with its size and last write time, in ordinal order of its path.</summary>
    private static string Stamp(string sourceDir)
    {
        StringBuilder stamp = new();
        IEnumerable<FileInfo> files = new DirectoryInfo(sourceDir)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .OrderBy(file => file.FullName, StringComparer.Ordinal);
        foreach (FileInfo file in files)
        {
            stamp.Append(CultureInfo.InvariantCulture, $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}\n");
        }

        return stamp.ToString();
    }

    /// <summary>A copy <see cref="CachedCopy"/> keeps: the helper build's stamp it was made at, and the extension's path in it.</summary>
    private sealed record PreparedCopy(string Stamp, string Path);

    private static JsonObject Parse(string reply)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(reply);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"The C# helper's reply is not JSON: {e.Message}", e);
        }

        return parsed as JsonObject ?? throw new InvalidOperationException("The C# helper's reply is not JSON: it is not a JSON object.");
    }
}
