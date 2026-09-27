using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// One headless operation: the normalised project folder, the operation's name and parameters, whether to prepare first, and
/// how long the Godot run may take before it is stopped.
/// </summary>
internal sealed record HeadlessRequest(string ProjectDir, string Operation, JsonObject Parameters, bool Prepare, TimeSpan Ceiling)
{
    /// <summary>
    /// The full paths of files the operation loads, which the prep imports first when they need it (<see cref="PrepContext.ImportAssets"/>).
    /// </summary>
    public IReadOnlyList<string> ImportAssets { get; init; } = [];

    /// <summary>What a refused import suggests besides stopping the sessions (<see cref="PrepContext.ImportSkipHint"/>); none by default.</summary>
    public string ImportSkipHint { get; init; } = string.Empty;

    /// <summary>
    /// Whether the result carries the last saved C# build (<see cref="HeadlessResult.LastBuild"/>), which the prep then makes
    /// when none is saved yet (<see cref="PrepContext.BuildWhenUnsaved"/>): validate's .cs targets.
    /// </summary>
    public bool ReportsBuild { get; init; }
}

/// <summary>
/// What a headless operation returned, every error and warning Godot logged while it ran (<c>{type, message, file, line}</c>),
/// the prep, and the compiler errors of a C# build that failed.
/// </summary>
internal sealed record HeadlessResult(JsonNode? Result, JsonArray EngineErrors, PrepResult Prep, CompilerErrorList? BuildErrors)
{
    /// <summary>The last saved C# build, read after the prep when the request asked for it (<see cref="HeadlessRequest.ReportsBuild"/>).</summary>
    public SavedBuild? LastBuild { get; init; }
}

/// <summary>
/// Runs <c>headless/operations.gd</c> in <c>godot --headless --script</c> on a project folder, under the folder's prep lock
/// through the prep and the run. The request and the result cross as JSON files under <c>.godot/godot-mcp/headless/</c>.
/// A <c>--script</c> run reads <c>override.cfg</c> (4.7.2 <c>main.cpp</c> L2107), so it is refused while a session is live
/// on the folder, and a marked file a crashed session left is removed first. The operation's parameters reach the script
/// with the prep's C# build state added as <c>build</c>.
/// </summary>
internal static class HeadlessRunner
{
    private const int LogTailLines = 20;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <exception cref="SessionException">
    /// A session is live on the folder, Godot or the script was not found, the prep failed, the run passed its ceiling or wrote
    /// no result, or the operation refused.
    /// </exception>
    public static async Task<HeadlessResult> RunAsync(SessionRegistry registry, HeadlessRequest request, CancellationToken cancellationToken)
    {
        string projectDir = request.ProjectDir;
        SemaphoreSlim folderLock = registry.PrepLock(projectDir);
        await folderLock.WaitAsync(cancellationToken);
        try
        {
            ClearFolder(registry, projectDir);
            string godot = Installation.FindGodot();
            string script = Installation.FindHeadlessScript();
            PrepContext context = new(projectDir, registry.Logger, () => registry.RunningSessionNames(projectDir, except: null))
            {
                ImportAssets = request.ImportAssets,
                ImportSkipHint = request.ImportSkipHint,
                BuildWhenUnsaved = request.ReportsBuild,
            };
            PrepOutcome prep = request.Prepare
                ? await ProjectPrep.RunReportingBuildAsync(context, cancellationToken)
                : new PrepOutcome(PrepResult.Skipped, null);
            JsonObject reply = await RunGodotAsync(new GodotCall(godot, script, request, registry, prep.Result.Build), cancellationToken);
            JsonArray engineErrors = reply["engineErrors"]?.DeepClone() as JsonArray ?? [];
            return new HeadlessResult(reply["result"]?.DeepClone(), engineErrors, prep.Result, prep.BuildErrors)
            {
                LastBuild = request.ReportsBuild ? SavedBuild.Load(projectDir) : null,
            };
        }
        finally
        {
            folderLock.Release();
        }
    }

    /// <exception cref="SessionException">A session is live on the folder.</exception>
    private static void ClearFolder(SessionRegistry registry, string projectDir)
    {
        IReadOnlyList<string> live = registry.LiveSessionNames(projectDir);
        if (live.Count > 0)
        {
            throw new SessionException(
                $"a headless run is refused while session(s) {string.Join(", ", live)} run on {projectDir}: a --script run would load the "
                    + "bridge from its override.cfg. stop_project or detach_project them first."
            );
        }

        OverrideFile.Remove(projectDir);
    }

    private static async Task<JsonObject> RunGodotAsync(GodotCall call, CancellationToken cancellationToken)
    {
        string projectDir = call.Request.ProjectDir;
        string folder = Path.Combine(ProjectPrep.LogFolder(projectDir), "headless");
        Directory.CreateDirectory(folder);
        string id = Guid.NewGuid().ToString("N");
        string requestPath = Path.Combine(folder, id + ".request.json");
        string resultPath = Path.Combine(folder, id + ".result.json");
        string log = Path.Combine(ProjectPrep.LogFolder(projectDir), "headless.log");
        try
        {
            JsonObject parameters = call.Request.Parameters.DeepClone().AsObject();
            parameters["build"] = call.Build;
            JsonObject body = new()
            {
                ["op"] = call.Request.Operation,
                ["params"] = parameters,
                ["result"] = ForGodot(resultPath),
            };
            await File.WriteAllTextAsync(requestPath, body.ToJsonString(), Utf8NoBom, cancellationToken);
            string[] arguments = ["--headless", "--path", projectDir, "--script", ForGodot(call.Script), "--", ForGodot(requestPath)];
            ToolProcessResult ran = await StartAsync(
                new ToolProcessRequest(call.Godot, arguments, projectDir, log, call.Request.Ceiling),
                call,
                cancellationToken
            );
            return ReadResult(call.Request, ran, resultPath, log);
        }
        finally
        {
            DeleteLogged(requestPath, call.Registry.Logger);
            DeleteLogged(resultPath, call.Registry.Logger);
        }
    }

    /// <summary>Deletes a request or result file; a failure is logged, so it never replaces the run's own outcome.</summary>
    private static void DeleteLogged(string path, ILogger logger)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.HeadlessFileDeleteFailed(logger, e, path);
        }
    }

    /// <exception cref="SessionException">Godot could not be started.</exception>
    private static async Task<ToolProcessResult> StartAsync(ToolProcessRequest process, GodotCall call, CancellationToken cancellationToken)
    {
        try
        {
            return await ToolProcess.RunAsync(process, call.Registry.Logger, cancellationToken);
        }
        catch (Win32Exception e)
        {
            throw new SessionException($"Godot could not be started from {call.Godot}: {e.Message}.", e);
        }
    }

    /// <exception cref="SessionException">The run passed its ceiling, wrote no result or an unreadable one, or the operation refused.</exception>
    private static JsonObject ReadResult(HeadlessRequest request, ToolProcessResult ran, string resultPath, string log)
    {
        string what = $"The headless {request.Operation} run on {request.ProjectDir}";
        if (ran.KilledByCeiling)
        {
            throw new SessionException(
                $"{what} passed {request.Ceiling.TotalSeconds:0} s, so it was stopped with its whole process tree. Its log: {log}"
            );
        }

        if (!File.Exists(resultPath))
        {
            string tail = string.Join('\n', File.ReadLines(log).TakeLast(LogTailLines));
            throw new SessionException($"{what} wrote no result (Godot exited {ran.ExitCode}). The last lines of its log, {log}:\n{tail}");
        }

        JsonObject reply = ParseResult(File.ReadAllText(resultPath), what);
        if (reply["ok"]?.GetValueKind() == JsonValueKind.True)
        {
            return reply;
        }

        throw new SessionException(FailureMessage(request.Operation, reply));
    }

    /// <summary>
    /// The message of an operation that refused: its error, then under "Godot logged:" one line for each error (not warning)
    /// Godot logged while it ran, <c>message (file:line)</c>, at most <see cref="ErrorReport.MaxPerResult"/> of them with each
    /// message cut to <see cref="ErrorReport.MaxMessageLength"/> characters, and "(n more)" for the rest.
    /// </summary>
    internal static string FailureMessage(string operation, JsonObject reply)
    {
        string error = reply["error"]?.GetValueKind() == JsonValueKind.String ? reply["error"]!.GetValue<string>() : "it gave no reason";
        string message = $"{operation} failed: {error}";
        JsonArray logged = reply["engineErrors"] as JsonArray ?? [];
        List<JsonObject> errors = [.. logged.OfType<JsonObject>().Where(entry => Text(entry, "type") == "error")];
        if (errors.Count == 0)
        {
            return message;
        }

        StringBuilder text = new(message);
        text.Append("\nGodot logged:");
        foreach (JsonObject entry in errors.Take(ErrorReport.MaxPerResult))
        {
            string cut = OutputBuffer.Cut(Text(entry, "message"), ErrorReport.MaxMessageLength);
            text.Append('\n').Append(cut).Append(" (").Append(Text(entry, "file")).Append(':').Append(Line(entry)).Append(')');
        }

        if (errors.Count > ErrorReport.MaxPerResult)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n({errors.Count - ErrorReport.MaxPerResult} more)");
        }

        return text.ToString();
    }

    private static string Text(JsonObject entry, string name) =>
        entry[name]?.GetValueKind() == JsonValueKind.String ? entry[name]!.GetValue<string>() : string.Empty;

    /// <summary>The entry's line as an integer; GDScript may write it as a float.</summary>
    private static string Line(JsonObject entry)
    {
        JsonNode? value = entry["line"];
        bool isNumber = value?.GetValueKind() == JsonValueKind.Number;
        return isNumber && double.TryParse(value!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double line)
            ? line.ToString("0", CultureInfo.InvariantCulture)
            : "0";
    }

    /// <exception cref="SessionException">The text is not a JSON object.</exception>
    private static JsonObject ParseResult(string text, string what)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new SessionException($"{what} wrote a result that is not a JSON object: {text}");
        }
        catch (JsonException e)
        {
            throw new SessionException($"{what} wrote a result that is not JSON: {e.Message}", e);
        }
    }

    private static string ForGodot(string path) => Path.GetFullPath(path).Replace('\\', '/');

    /// <summary>
    /// The Godot executable, the operations script, the request, the registry whose logger the run uses, and the prep's C# build
    /// state (<see cref="PrepResult.Build"/>).
    /// </summary>
    private sealed record GodotCall(string Godot, string Script, HeadlessRequest Request, SessionRegistry Registry, string Build);
}
