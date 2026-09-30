using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// run_csharp: a C# method body compiled on the server against the runtime the game runs on, the game's own build and the
/// helper's globals class, then loaded and awaited inside the game by the helper's <c>run</c> op, answering as cs_call does.
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const string RunCSharpToolName = "run_csharp";
    internal const int MaxCompileErrorLines = 20;

    /// <summary>Each session's game runtime folder, with the game process it was asked of.</summary>
    private static readonly ConditionalWeakTable<GodotSession, GameRuntime> GameRuntimes = [];

    /// <summary>The helper's class a snippet derives from (GodotMcp.Dotnet's <c>SnippetGlobals</c>).</summary>
    private const string SnippetGlobalsType = "GodotMcp.Dotnet.SnippetGlobals";

    [McpServerTool(Name = RunCSharpToolName, ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Compiles C# and runs it inside the running game against the game's own assemblies, returning {value, type} as "
            + "cs_call does: type is the value's runtime full name, and a value longer than 20000 characters comes back as "
            + "{valuePreview, valueLength}. code is a method body: statements, or one expression whose value is returned; "
            + "await works, and a body that runs off its end returns null. In scope: Tree, Root, Node(path) and Node<T>(path), "
            + "Handle(id) and Handle<T>(id) for a kept object, Get/Set by member path and Call by method name on an object, "
            + "private and internal members included (their Type forms reach statics), and Keep(value), which answers a "
            + "handle usable as {handle} in a later C# call, and Cancellation, the token this call's timeout "
            + "cancels. Node reads a path from /root, finds a bare name's first node under /root, or takes %Name for a node "
            + "saved with a unique name (alone, looked up in every scene; or after its owner's path). The snippet names the "
            + "game's internal types and members directly, as code inside the game's "
            + "assemblies would; private ones only through Get, Set and Call. No type declarations; lambdas, local functions and anonymous "
            + "types work. The usings are System, System.Linq, System.Collections.Generic, System.Threading.Tasks, Godot "
            + "and the game's root namespace, plus options.usings. A compile error fails before the game is asked, with one "
            + "snippet(line,col): error line each. The snippet is awaited up to options.timeoutMs; past it the call fails and "
            + "Cancellation is cancelled, so a snippet that passes it on or checks it can stop; one that ignores it, or loops "
            + "on the main thread without yielding, keeps running. options.keep also returns a handle to the value. Runs game code: a "
            + "thrown exception fails with its type, message and a stack naming the snippet's lines. Refused when the game "
            + "runs an older build of its assemblies than the one on disk; restart_project loads the new one."
    )]
    public async Task<string> RunCSharpAsync(
        [Description("A C# method body: statements (return gives the value), or one expression whose value is returned.")] string code,
        [Description(
            "{usings, timeoutMs, keep, maxDepth}: namespaces added to the default usings, how long the snippet is awaited, 1 to "
                + "120000 ms, load-adjusted (10000 by default), whether a handle to the value comes back too (false by default), and how many "
                + "levels of nested objects are written, 1 to 32 (8 by default)."
        )]
            RunCSharpOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        _ = CheckName(code, "code", "Pass a method body: statements, or one expression whose value is returned.");
        string[] usings = CheckUsings(options?.Usings);
        int maxDepth = CheckGetDepth(options?.MaxDepth);
        int timeoutMs = CheckCallTimeout(options?.TimeoutMs);
        GodotSession game = Find(session);
        string framework = await GameRuntimeDirectoryAsync(RunCSharpToolName, game, session, cancellationToken);
        JsonObject request = BuildRunRequest(game.ProjectDir, framework, code, usings);
        request["maxDepth"] = maxDepth;
        request["keep"] = options?.Keep ?? false;
        (CSharpReply reply, IReadOnlyList<ErrorEntry> errors) = await SendCSharpAsync(
            RunCSharpToolName,
            request,
            timeoutMs,
            session,
            cancellationToken
        );
        return ErrorReport.AddTo(GetResult(reply), errors).ToJsonString();
    }

    /// <summary>The namespaces options.usings adds, none when left out.</summary>
    /// <exception cref="McpException">An element is null or blank.</exception>
    private static string[] CheckUsings(string[]? usings)
    {
        string[] given = usings ?? [];
        for (int i = 0; i < given.Length; i++)
        {
            // JSON can carry a null element whatever the array's declared type.
            if (string.IsNullOrWhiteSpace(given[i]))
            {
                throw new McpException($"usings[{i}] is empty");
            }
        }

        return given;
    }

    /// <summary>
    /// The folder of the runtime the session's game runs on, which the helper's ping reports; asked once per game process,
    /// since a running game's runtime cannot change and a restart starts a new process.
    /// </summary>
    /// <exception cref="McpException">The helper cannot be asked, or its ping names no runtime folder; <paramref name="tool"/> fails.</exception>
    private async Task<string> GameRuntimeDirectoryAsync(string tool, GodotSession game, string? session, CancellationToken cancellationToken)
    {
        if (GameRuntimes.TryGetValue(game, out GameRuntime? known) && known.ProcessId == game.GameProcessId)
        {
            return known.Directory;
        }

        JsonObject ping = new() { ["op"] = "ping" };
        (CSharpReply reply, _) = await SendCSharpAsync(tool, ping, null, session, cancellationToken);
        string directory =
            reply.Result?["runtimeDirectory"]?.GetValue<string>()
            ?? throw new McpException(
                $"{tool} failed: the C# helper in the game does not report its runtime folder; rebuild it with "
                    + "'pwsh run.ps1 dotnet', then restart_project."
            );
        GameRuntimes.AddOrUpdate(game, new GameRuntime(game.GameProcessId, directory));
        return directory;
    }

    /// <summary>
    /// The helper's <c>run</c> request for <paramref name="code"/> compiled against the game's runtime in
    /// <paramref name="framework"/> and the project's build: the assembly and its PDB as base64, the game assembly's simple
    /// name, and the MVID of each game-folder and helper dll the helper checks before loading.
    /// </summary>
    /// <exception cref="McpException">
    /// The project cannot be asked, its build cannot be read, a using is no namespace name, or the snippet does not compile.
    /// </exception>
    private JsonObject BuildRunRequest(string projectDir, string framework, string code, string[] usings)
    {
        SnippetReferences references = FindSnippetReferences(RunCSharpToolName, projectDir, framework);
        List<string> allUsings = [.. usings, "Godot"];
        if (references.RootNamespace is { } root)
        {
            allUsings.Add(root);
        }

        SnippetCompilation compiled = SnippetCompiler.Compile(new SnippetRequest(code, SnippetGlobalsType, framework, references.Paths, allUsings));
        if (compiled.Assembly is null)
        {
            throw new McpException(CompileFailure(compiled.Errors));
        }

        return new JsonObject
        {
            ["op"] = "run",
            ["assembly"] = Convert.ToBase64String(compiled.Assembly),
            ["pdb"] = Convert.ToBase64String(compiled.Pdb!),
            ["game"] = references.Game,
            ["expect"] = ExpectedMvids(references),
        };
    }

    /// <summary>The helper's <c>expect</c>: each game-folder and helper dll's simple name mapped to its MVID on disk.</summary>
    private static JsonObject ExpectedMvids(SnippetReferences references) =>
        new([.. references.Expect.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value)))]);

    /// <summary>What the snippet compiles against: the game's build folder and the helper copy the game loads.</summary>
    /// <exception cref="McpException">
    /// The project cannot be asked, or its build or the helper's copy cannot be read; <paramref name="tool"/> fails.
    /// </exception>
    private SnippetReferences FindSnippetReferences(string tool, string projectDir, string framework)
    {
        try
        {
            return SnippetReferences.Find(projectDir, csharp.PrepareHelperFolder(projectDir), framework);
        }
        catch (Exception e)
            when (e is InvalidOperationException or IOException or UnauthorizedAccessException or BadImageFormatException or XmlException)
        {
            throw new McpException($"{tool} failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// The refusal for a snippet that does not compile: one <c>snippet(line,col): error ID: message</c> line per error, an
    /// error outside the snippet placed at <c>(wrapper)</c>, at most <see cref="MaxCompileErrorLines"/> of them.
    /// </summary>
    internal static string CompileFailure(IReadOnlyList<SnippetDiagnostic> errors)
    {
        StringBuilder text = new($"{RunCSharpToolName} failed: the snippet does not compile:");
        foreach (SnippetDiagnostic error in errors.Take(MaxCompileErrorLines))
        {
            string place = error.Line == 0 ? "(wrapper)" : string.Create(CultureInfo.InvariantCulture, $"snippet({error.Line},{error.Column})");
            string id = error.Id.Length > 0 ? " " + error.Id : string.Empty;
            text.Append('\n').Append(place).Append(": error").Append(id).Append(": ").Append(error.Message);
        }

        if (errors.Count > MaxCompileErrorLines)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n… and {errors.Count - MaxCompileErrorLines} more");
        }

        return text.ToString();
    }

    /// <summary>A game runtime folder, and the game process that reported it.</summary>
    private sealed record GameRuntime(int? ProcessId, string Directory);
}
