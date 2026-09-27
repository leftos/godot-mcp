using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;

namespace GodotMcp.Server.CSharp;

/// <summary>The helper's answer to one request, and whether this call was the one that loaded the extension into the game.</summary>
internal sealed record CSharpReply(JsonNode? Result, bool LoadedNow);

/// <summary>
/// The server's half of the bridge's <c>dotnet</c> command: whether the project may be asked at all
/// (<see cref="Refusal"/>), the copy of the helper the game loads (<see cref="HelperCache"/>), and the helper's reply.
/// </summary>
internal sealed class CSharpBridge(HelperCache cache, Func<string?> findExtension)
{
    /// <summary>
    /// Why the project cannot answer a C# tool, or null when it can: no C# assembly, a C# build the prep skipped, an
    /// assembly that was never built, or a server that ships no helper.
    /// </summary>
    public static string? Refusal(string projectDir, string? extensionPath)
    {
        CsprojLookup lookup = PrepScan.FindCsproj(projectDir);

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
    /// long the server waits for the reply; <paramref name="pendingTimeoutMs"/>, when given, is how long the bridge polls a call
    /// the helper answers as pending (the bridge's own default otherwise).
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
        JsonObject parameters = new() { ["extension"] = copied, ["request"] = requestJson };
        if (pendingTimeoutMs is { } pending)
        {
            parameters["timeoutMs"] = pending;
        }

        JsonNode? result =
            await session.SendAsync("dotnet", parameters, TimeSpan.FromMilliseconds(timeoutMs), cancellation)
            ?? throw new InvalidOperationException("The C# helper's reply is missing: the bridge answered no result for 'dotnet'.");
        return ParseReply(result);
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
