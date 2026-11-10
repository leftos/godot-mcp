using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.Server.Tools;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// What the prep needs from its caller: the project folder, where to log, the names of the sessions whose game runs on
/// the folder, asked only when an import is due (a launch leaves itself out; a restart can leave its own old game out too),
/// and what to do just before an import runs, once no running session refuses it: stop the folder's warm headless host,
/// whose UID cache and class list the import rewrites.
/// </summary>
internal sealed record PrepContext(string ProjectDir, ILogger Logger, Func<IReadOnlyList<string>> RunningSessions, Action BeforeImport)
{
    /// <summary>The full paths of files the request loads; one that <see cref="PrepScan.AssetNeedsImport"/> makes the import due.</summary>
    public IReadOnlyList<string> ImportAssets { get; init; } = [];

    /// <summary>
    /// What a refused import suggests besides stopping the sessions, starting ", or": run_project's options.prepare by default.
    /// </summary>
    public string ImportSkipHint { get; init; } = ", or pass options.prepare: \"never\" to launch without importing";

    /// <summary>
    /// Whether the prep builds when no build's diagnostics are saved yet (<see cref="SavedBuild"/>), however fresh the assembly:
    /// as a rebuild, since an up-to-date build compiles nothing and so logs no warnings. validate's .cs targets ask for it.
    /// </summary>
    public bool BuildWhenUnsaved { get; init; }

    /// <summary>What a build stopped at its ceiling leaves undone, as its refusal says: run_project's by default.</summary>
    public string KilledBuildOutcome { get; init; } = "the game was not started";

    /// <summary>
    /// Whether an import whose Godot crashed at boot runs once more: a registry's prep passes its own
    /// <see cref="SessionRegistry.RetryBootCrashOnce"/>.
    /// </summary>
    public required bool RetryBootCrashOnce { get; init; }
}

/// <summary>
/// Makes a fresh checkout runnable before a launch: builds the C# assembly when it is missing or stale, then runs a Godot
/// import when imported files are missing, a source asset changed since it was imported, or its import settings changed since the
/// server last saw them. The caller serialises prep per folder.
/// </summary>
internal static partial class ProjectPrep
{
    /// <summary>The configuration the prep builds and lists Compile items in, which the build's refusals name.</summary>
    public const string Configuration = "Debug";

    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(300);

    // Set by a dotnet test or build run above the server; they would point the nested build at another MSBuild.
    private static readonly string[] InheritedMsBuildVariables = ["MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH"];

    /// <summary>The server's own folder in a project, already git-ignored under <c>.godot/</c>.</summary>
    public static string LogFolder(string projectDir) => Path.Combine(projectDir, ".godot", "godot-mcp");

    /// <summary>The C# build's log, <c>.godot/godot-mcp/build.log</c>, which each build overwrites.</summary>
    public static string BuildLog(string projectDir) => Path.Combine(LogFolder(projectDir), "build.log");

    /// <exception cref="SessionException">The build failed or hit its ceiling, or an import is due and cannot run.</exception>
    public static async Task<PrepResult> RunAsync(PrepContext context, CancellationToken cancellationToken) =>
        (await RunCoreAsync(context, reportRedBuild: false, cancellationToken)).Result;

    /// <summary>
    /// The prep for a headless run: a build that fails is reported as build <c>failed</c> with its compiler errors instead of
    /// thrown, and the import still runs.
    /// </summary>
    /// <exception cref="SessionException">The build hit its ceiling, or an import is due and cannot run.</exception>
    public static Task<PrepOutcome> RunReportingBuildAsync(PrepContext context, CancellationToken cancellationToken) =>
        RunCoreAsync(context, reportRedBuild: true, cancellationToken);

    /// <summary>
    /// The build alone, for an open editor (editor build): the prep's build step, with its <c>dotnet build</c>, log, saved
    /// diagnostics and stamp, and no import, which would refuse or disturb the editor open on the folder. A build that fails
    /// is returned as <c>failed</c> with its compiler errors instead of thrown.
    /// </summary>
    /// <exception cref="SessionException">The build hit its ceiling, or the prep wrapper stopped it.</exception>
    public static async Task<BuildOutcome> RunBuildAsync(PrepContext context, CancellationToken cancellationToken)
    {
        Log.PrepStarted(context.Logger, context.ProjectDir);
        try
        {
            Lazy<PrepWrapper?> wrapper = new(() => PrepWrapper.Read(context.ProjectDir));
            ProjectFiles files = PrepScan.Scan(context.ProjectDir, context.Logger);
            PrepStep build = await BuildAsync(context, files, wrapper, reportRedBuild: true, cancellationToken);
            Log.PrepFinished(context.Logger, context.ProjectDir, build.State, "not run");
            return new BuildOutcome(build.State, build.Errors, build.Gate);
        }
        catch (SessionException e)
        {
            Log.PrepFailed(context.Logger, e, context.ProjectDir);
            throw;
        }
        catch (OperationCanceledException e)
        {
            Log.PrepCancelled(context.Logger, e, context.ProjectDir);
            throw;
        }
    }

    private static async Task<PrepOutcome> RunCoreAsync(PrepContext context, bool reportRedBuild, CancellationToken cancellationToken)
    {
        Log.PrepStarted(context.Logger, context.ProjectDir);
        try
        {
            // Read only when a step runs a process, so a prep that runs none never reads godot-mcp.json.
            Lazy<PrepWrapper?> wrapper = new(() => PrepWrapper.Read(context.ProjectDir));
            ProjectFiles files = PrepScan.Scan(context.ProjectDir, context.Logger);
            var basis = ImportBasis.Of(context.ProjectDir, files, context.Logger);
            PrepStep build = await BuildAsync(context, files, wrapper, reportRedBuild, cancellationToken);
            PrepStep import = await ImportAsync(context, files, basis, wrapper, cancellationToken);
            ImportFingerprints.Save(context.ProjectDir, PassFor(import, context, files, basis), import.State, basis.Fingerprints, context.Logger);
            string? wrapperNote = wrapper.IsValueCreated ? wrapper.Value?.Note : null;
            string[] notes = [.. new[] { build.Note, import.Note, wrapperNote }.OfType<string>()];
            PrepResult result = new()
            {
                Build = build.State,
                BuildMs = build.Milliseconds,
                BuildLog = build.Log,
                BuildGate = build.Gate,
                Import = import.State,
                ImportMs = import.Milliseconds,
                ImportLog = import.Log,
                ImportGate = import.Gate,
                FilesWritten = import.FilesWritten,
                FilesRemoved = import.FilesRemoved,
                Note = notes.Length == 0 ? null : string.Join(' ', notes),
            };
            Log.PrepFinished(context.Logger, context.ProjectDir, result.Build, result.Import);
            return new PrepOutcome(result, build.Errors);
        }
        catch (SessionException e)
        {
            Log.PrepFailed(context.Logger, e, context.ProjectDir);
            throw;
        }
        catch (OperationCanceledException e)
        {
            Log.PrepCancelled(context.Logger, e, context.ProjectDir);
            throw;
        }
    }

    private static async Task<PrepStep> BuildAsync(
        PrepContext context,
        ProjectFiles files,
        Lazy<PrepWrapper?> wrapper,
        bool reportRedBuild,
        CancellationToken cancellationToken
    )
    {
        string projectDir = context.ProjectDir;
        CsprojLookup lookup = PrepScan.FindCsproj(projectDir);
        if (lookup.Kind != CsprojKind.Found)
        {
            return new PrepStep(lookup.Kind == CsprojKind.None ? "no-csproj" : "skipped", null, lookup.Note);
        }

        BuildNeed need = BuildNeeded(context, files, lookup.AssemblyName!);
        if (need == BuildNeed.None)
        {
            return new PrepStep("up-to-date", null, null);
        }

        string csproj = lookup.ProjectFile!;
        string log = BuildLog(projectDir);
        string diagnosticsLog = BuildDiagnosticsLog(projectDir);
        PrepWrapper? wrap = wrapper.Value;
        // Both are this build's own: a run whose command never ran leaves no earlier build's log or diagnostics to be read.
        DeleteLogged(log, context.Logger);
        DeleteLogged(diagnosticsLog, context.Logger);
        ToolProcessResult built = await RunStepAsync(
            BuildRequest(Installation.FindDotnet(), csproj, log, diagnosticsLog, need == BuildNeed.Rebuild),
            "dotnet",
            wrap,
            context.Logger,
            cancellationToken
        );
        IReadOnlyList<BuildDiagnostic> diagnostics = SaveDiagnostics(projectDir, built, log, diagnosticsLog, wrap);
        long milliseconds = (long)built.Elapsed.TotalMilliseconds;
        string? gate = GateVerdictOf(wrap, log, context.Logger);
        if (reportRedBuild && StateOf(built, wrap) == "failed")
        {
            string note = $"The C# build of {csproj} failed (dotnet exited {built.ExitCode}); its log: {log}";
            return new PrepStep("failed", milliseconds, note)
            {
                Errors = CompilerErrors.Errors(diagnostics),
                Log = log,
                Gate = gate,
            };
        }

        CheckFinished(built, csproj, log, context.KilledBuildOutcome);
        CheckBuild(built, csproj, log, diagnostics, wrap);
        PrepScan.WriteStamp(PrepScan.AssemblyPath(projectDir, lookup.AssemblyName!), PrepScan.StampPath(projectDir), projectDir, files.BuildInputs);
        return new PrepStep("built", milliseconds, null) { Log = log, Gate = gate };
    }

    /// <summary>
    /// A rebuild when the context asks for saved diagnostics and none are saved, else none when the assembly is current
    /// (<see cref="PrepScan.BuildIsCurrent"/>), else a build.
    /// </summary>
    private static BuildNeed BuildNeeded(PrepContext context, ProjectFiles files, string assemblyName)
    {
        string projectDir = context.ProjectDir;
        if (context.BuildWhenUnsaved && !File.Exists(SavedBuild.PathIn(projectDir)))
        {
            return BuildNeed.Rebuild;
        }

        return PrepScan.BuildIsCurrent(projectDir, assemblyName, files.BuildInputs, context.BuildWhenUnsaved, context.Logger)
            ? BuildNeed.None
            : BuildNeed.Build;
    }

    /// <summary>Saves the build's errors and warnings as the project's <see cref="SavedBuild"/>, and returns them.</summary>
    private static IReadOnlyList<BuildDiagnostic> SaveDiagnostics(
        string projectDir,
        ToolProcessResult built,
        string log,
        string diagnosticsLog,
        PrepWrapper? wrapper
    )
    {
        IReadOnlyList<BuildDiagnostic> diagnostics = BuildDiagnostics(
            File.Exists(diagnosticsLog) ? ReadLog(diagnosticsLog) : null,
            ReadLog(log),
            built.ExitCode != 0 || built.WasKilled
        );
        SavedBuild.Save(projectDir, new SavedBuild(DateTime.UtcNow, StateOf(built, wrapper), diagnostics));
        return diagnostics;
    }

    /// <summary>
    /// The build's diagnostics: the file logger's, or the build log's where the file logger cannot hold them. MSBuild prints
    /// what happens before its loggers start (an MSB1009 project that does not exist, a logger that cannot open its file) only
    /// in the console log, and a routed build's diagnostics file stays on the builder, so a failed build whose file holds no
    /// error is read from the log instead.
    /// </summary>
    internal static IReadOnlyList<BuildDiagnostic> BuildDiagnostics(string? fileLoggerText, string buildLogText, bool failed)
    {
        IReadOnlyList<BuildDiagnostic> fromFileLogger = fileLoggerText is null ? [] : CompilerErrors.ParseDiagnostics(fileLoggerText);
        if (fileLoggerText is null || (failed && !fromFileLogger.Any(diagnostic => diagnostic.Severity == BuildDiagnostic.Error)))
        {
            return CompilerErrors.ParseDiagnostics(buildLogText);
        }

        return fromFileLogger;
    }

    /// <summary>A log's text; empty when a wrapper that owns the log never wrote it.</summary>
    private static string ReadLog(string log) => File.Exists(log) ? File.ReadAllText(log) : string.Empty;

    /// <summary>
    /// The prep wrapper's verdict from a step's capture log (<see cref="PrepWrapper.CaptureLog"/>): the gate's closing verdict
    /// line, or the kill line of a run the gate's watchdog killed (<see cref="PrepWrapper.GateVerdict"/>). Null when the step
    /// ran through no wrapper, the wrapper left no capture log, or the log holds no verdict. The log is read while another
    /// process may still hold it, and one that cannot be read is logged and left out.
    /// </summary>
    private static string? GateVerdictOf(PrepWrapper? wrapper, string stepLog, ILogger logger)
    {
        if (wrapper is null)
        {
            return null;
        }

        string captureLog = wrapper.CaptureLog(stepLog);
        if (!File.Exists(captureLog))
        {
            return null;
        }

        try
        {
            using FileStream stream = new(captureLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream);
            return PrepWrapper.GateVerdict(reader.ReadToEnd());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.PrepVerdictReadFailed(logger, e, captureLog);
            return null;
        }
    }

    private static string StateOf(ToolProcessResult built, PrepWrapper? wrapper)
    {
        if (built.WasKilled || (wrapper is not null && PrepWrapper.Stopped(built)))
        {
            return "stopped";
        }

        return built.ExitCode == 0 ? "built" : "failed";
    }

    // --no-incremental rebuilds every file, so each one's warnings reach the log (learn.microsoft.com/dotnet/core/tools/dotnet-build).
    // MSBuild's own file logger writes the diagnostics, so a prep wrapper that rewrites the console output cannot reach them; its
    // switches use the /name:value form, which a PowerShell prep wrapper keeps whole where it splits -name:value at the colon.
    private static ToolProcessRequest BuildRequest(string dotnet, string csproj, string log, string diagnostics, bool rebuild) =>
        DotnetRequest(
            dotnet,
            [
                "build",
                csproj,
                "-c",
                Configuration,
                "-p:GodotTargetPlatform=windows",
                "-p:UseSharedCompilation=false",
                "-fl",
                $"/flp:LogFile={diagnostics};Verbosity=quiet",
                .. rebuild ? ["--no-incremental"] : Array.Empty<string>(),
            ],
            csproj,
            log
        );

    /// <summary>A dotnet command on the csproj, in its folder, without the server's MSBuild environment.</summary>
    private static ToolProcessRequest DotnetRequest(string dotnet, string[] arguments, string csproj, string log) =>
        new(dotnet, arguments, Path.GetDirectoryName(csproj)!, log, Ceiling)
        {
            // No reused MSBuild nodes and no shared compiler server, so nothing the build starts outlives it.
            SetVariables = new Dictionary<string, string> { ["MSBUILDDISABLENODEREUSE"] = "1" },
            RemovedVariables = InheritedMsBuildVariables,
        };

    /// <summary>
    /// The full paths of the csproj's <c>Compile</c> items, from MSBuild's evaluation (<c>dotnet msbuild -getItem:Compile</c>,
    /// MSBuild 17.8 and later: learn.microsoft.com/visualstudio/msbuild/evaluate-items-and-properties), with the build's
    /// configuration and environment. dotnet runs through the project's prep wrapper when it names one, as the build does, in a
    /// <see cref="PrepWrapper.LightSlot"/> since the evaluation takes about a second on one core; the ceiling pauses while a
    /// gate waits for that slot, and the wall backstop bounds the whole step. MSBuild writes the item list to a JSON file of
    /// its own (<c>/getResultOutputFile</c>), which is read; the log is read instead when MSBuild wrote no result file. Each
    /// evaluation has a log, a result file and a wrapper log of its own (<see cref="CompileItemsLog"/>), so evaluations on
    /// one folder at once never share them; all are deleted once read, and kept when the evaluation failed.
    /// </summary>
    /// <exception cref="SessionException">
    /// dotnet or the wrapper could not be started, the step hit its ceiling or backstop, the wrapper stopped it, it failed, or
    /// it wrote no item list.
    /// </exception>
    public static async Task<IReadOnlyList<string>> CompileItemsAsync(
        string projectDir,
        string csproj,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        string log = CompileItemsLog(projectDir);
        string result = Path.ChangeExtension(log, ".json");
        string[] arguments =
        [
            "msbuild",
            csproj,
            "/getItem:Compile",
            $"/getResultOutputFile:{result}",
            $"-p:Configuration={Configuration}",
            "-p:GodotTargetPlatform=windows",
            "-nologo",
        ];
        ToolProcessRequest request = DotnetRequest(Installation.FindDotnet(), arguments, csproj, log);
        var wrapper = PrepWrapper.Read(projectDir);
        ToolProcessResult listed = wrapper is null
            ? await RunToolAsync(request, "dotnet", logger, cancellationToken)
            : await RunToolAsync(
                wrapper.Wrap(request, PrepWrapper.LightSlot),
                $"The prepWrapper of {ProjectProfile.FileName}",
                logger,
                cancellationToken
            );
        string failure = $"dotnet msbuild -getItem:Compile on {csproj}";
        CheckListed(listed, failure, log, wrapper, csproj);
        string output = File.Exists(result) ? ReadLog(result) : ReadLog(log);
        IReadOnlyList<string> items;
        try
        {
            items = ReadCompileItems(output) ?? throw new SessionException($"{failure} wrote no item list. Its log: {log}");
        }
        catch (JsonException e)
        {
            throw new SessionException($"{failure} wrote an item list that is not JSON ({e.Message}). Its log: {log}", e);
        }

        DeleteLogged(log, logger);
        DeleteLogged(result, logger);
        if (wrapper is { OwnsLog: true })
        {
            DeleteLogged(PrepWrapper.WrapperLog(log), logger);
        }

        return items;
    }

    /// <exception cref="SessionException">The listing hit its ceiling or backstop, the prep wrapper stopped it, or it failed.</exception>
    private static void CheckListed(ToolProcessResult listed, string failure, string log, PrepWrapper? wrapper, string csproj)
    {
        if (listed.WasKilled)
        {
            throw new SessionException(KilledStepMessage(failure, listed, "the Compile items are unknown", log));
        }

        if (wrapper is not null && PrepWrapper.Stopped(listed))
        {
            throw new SessionException(wrapper.StoppedNote($"the Compile items listing of {csproj}", log));
        }

        if (listed.ExitCode != 0)
        {
            throw new SessionException($"{failure} failed (exited {listed.ExitCode}), so the Compile items are unknown. Its log: {log}");
        }
    }

    /// <summary>A log path of its own for one Compile-items evaluation: <c>.godot/godot-mcp/compile-items-&lt;guid&gt;.log</c>.</summary>
    internal static string CompileItemsLog(string projectDir) => Path.Combine(LogFolder(projectDir), $"compile-items-{Guid.NewGuid():N}.log");

    /// <summary>
    /// The build's diagnostics, in a file MSBuild's own file logger writes: <c>.godot/godot-mcp/build.diagnostics.log</c>. It is
    /// a file of its own because the build log is the one a prep wrapper rewrites (a gate prefixes every line), which would
    /// land in each diagnostic's file path.
    /// </summary>
    internal static string BuildDiagnosticsLog(string projectDir) => Path.Combine(LogFolder(projectDir), "build.diagnostics.log");

    /// <summary>Deletes a read log; a failure is logged, so it never replaces the call's own outcome.</summary>
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

    /// <summary>The <c>Items.Compile[].FullPath</c> values of <c>-getItem</c>'s JSON, or null when the output holds none.</summary>
    /// <exception cref="JsonException">The text from the first <c>{</c> to the last <c>}</c> is not JSON.</exception>
    internal static IReadOnlyList<string>? ReadCompileItems(string output)
    {
        int start = output.IndexOf('{', StringComparison.Ordinal);
        int end = output.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return null;
        }

        var items = JsonNode.Parse(output[start..(end + 1)])?["Items"]?["Compile"] as JsonArray;
        return items?.OfType<JsonObject>().Select(item => item["FullPath"]?.GetValue<string>()).OfType<string>().ToList();
    }

    /// <summary>Refuses a build that hit its ceiling, saying what it left undone (<see cref="PrepContext.KilledBuildOutcome"/>).</summary>
    /// <exception cref="SessionException">The build hit its ceiling.</exception>
    private static void CheckFinished(ToolProcessResult built, string csproj, string log, string outcome)
    {
        if (built.WasKilled)
        {
            throw new SessionException(KilledStepMessage($"The C# build of {csproj}", built, outcome, log));
        }
    }

    /// <summary>
    /// The refusal of a prep step its ceiling or backstop killed: what did not finish and why, what that left undone, the
    /// wrapper's last waiting line when it still waited for a slot (<see cref="ToolProcessResult.LastWait"/>), and the log.
    /// </summary>
    /// <param name="step">The step as the sentence opens: "The Godot import of X:\game".</param>
    /// <param name="killed">The killed step's result.</param>
    /// <param name="outcome">What the kill left undone: "the game was not started".</param>
    /// <param name="log">The step's log.</param>
    internal static string KilledStepMessage(string step, ToolProcessResult killed, string outcome, string log)
    {
        string wait = killed.LastWait is null ? string.Empty : $" It was still waiting for a slot: \"{killed.LastWait}\".";
        return $"{step} did not finish {killed.KillPhrase}, so it was stopped with its whole process tree and {outcome}.{wait} Its log: {log}";
    }

    /// <exception cref="SessionException">The build was stopped by the prep wrapper, or failed.</exception>
    private static void CheckBuild(
        ToolProcessResult built,
        string csproj,
        string log,
        IReadOnlyList<BuildDiagnostic> diagnostics,
        PrepWrapper? wrapper
    )
    {
        if (wrapper is not null && PrepWrapper.Stopped(built))
        {
            throw new SessionException(wrapper.StoppedNote("the build", log));
        }

        if (built.ExitCode == 0)
        {
            return;
        }

        string quoted = CompilerErrors.Errors(diagnostics).Quote();
        throw new SessionException(
            $"The {Configuration} C# build of {csproj} failed (dotnet exited {built.ExitCode}), so the game was not started. "
                + $"Fix the errors, or pass options.prepare: \"never\" to launch without building:\n{quoted}\nFull log: {log}"
        );
    }

    private static async Task<PrepStep> ImportAsync(
        PrepContext context,
        ProjectFiles files,
        ImportBasis basis,
        Lazy<PrepWrapper?> wrapper,
        CancellationToken cancellationToken
    )
    {
        if (
            !PrepScan.ImportNeeded(context.ProjectDir, files, basis.MissingSidecars, basis.Fingerprints, context.Logger)
            && !context.ImportAssets.Any(asset => PrepScan.AssetNeedsImport(context.ProjectDir, asset, basis.Fingerprints))
        )
        {
            return new PrepStep("not-needed", null, null);
        }

        IReadOnlyList<string> missingSidecars = basis.MissingSidecars;
        IReadOnlyList<string> others = context.RunningSessions();
        if (others.Count > 0)
        {
            string why = missingSidecars.Count > 0 ? $" ({PrepScan.ResourcePathOf(context.ProjectDir, missingSidecars[0])} has no .import file)" : "";
            throw new SessionException(
                $"the project at {context.ProjectDir} needs a Godot import{why}, but session(s) {string.Join(", ", others)} are running on it; "
                    + $"stop them first{context.ImportSkipHint}."
            );
        }

        context.BeforeImport();
        IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> before = PrepScan.Sidecars(context.ProjectDir);
        IReadOnlySet<string>? classesBefore = PrepScan.IsFormatClassStale(context.ProjectDir, files)
            ? PrepScan.CachedFormatClasses(context.ProjectDir, files)
            : null;
        string godot = Installation.FindGodot();
        PrepWrapper? wrap = wrapper.Value;
        string log = Path.Combine(LogFolder(context.ProjectDir), "import.log");
        try
        {
            ToolProcessResult first = await RunImportAsync(
                ImportRequest(godot, context.ProjectDir, log, append: false),
                context,
                wrap,
                cancellationToken
            );
            string? reason = SecondPassReason(log, classesBefore, context.ProjectDir, files);
            PrepStep step = reason is null
                ? new PrepStep("done", (long)first.Elapsed.TotalMilliseconds, null) { Log = log, Gate = GateVerdictOf(wrap, log, context.Logger) }
                : await SecondImportAsync(godot, context, wrap, first, reason, cancellationToken);
            return WithMissingSidecarNotes(WithSidecarChanges(step, context.ProjectDir, before), context.ProjectDir, missingSidecars);
        }
        catch (SessionException e)
        {
            string? stopped = SidecarStoppedNote(context.ProjectDir, before);
            if (stopped is null)
            {
                throw;
            }

            throw new SessionException(e.Message + " " + stopped, e);
        }
    }

    /// <summary>
    /// The step, with its notes on the sources that made the import run for having no <c>.import</c> file appended to any note
    /// it already has: the ones it imported, or a <c>.blend</c> it left without one, which the record keeps
    /// (<see cref="MissingSidecarNote"/>); every other one still without one (<see cref="StillMissingNote"/>); and why Godot
    /// dropped the import when Blender's path is not set (<see cref="BlenderPathNote"/>).
    /// </summary>
    private static PrepStep WithMissingSidecarNotes(PrepStep step, string projectDir, IReadOnlyList<string> sources)
    {
        string[] notes = [.. new[] { step.Note, MissingSidecarNotes(projectDir, sources, step.Log) }.OfType<string>()];
        return step with { Note = notes.Length == 0 ? null : string.Join(' ', notes) };
    }

    /// <summary>
    /// The notes on the sources that made a finished import run for having no <c>.import</c> file, joined, or null when there are
    /// none: the ones it imported, or a <c>.blend</c> it left without one, which the record keeps (<see cref="MissingSidecarNote"/>),
    /// without the <c>.blend</c> ones when Godot dropped the import for Blender's path, which <see cref="BlenderPathNote"/> covers;
    /// every other one still without one (<see cref="StillMissingNote"/>); and that note.
    /// </summary>
    /// <param name="log">The import's log, which <see cref="StillMissingNote"/> points to and the Blender line is looked for in.</param>
    internal static string? MissingSidecarNotes(string projectDir, IReadOnlyList<string> sources, string? log)
    {
        List<string> left = [.. sources.Where(source => !ImportFingerprints.RecordsSource(source) && !File.Exists(source + PrepScan.ImportSuffix))];
        string? blender = BlenderPathNote(projectDir, log);
        IEnumerable<string> imported = sources.Except(left).Where(source => blender is null || !ImportFingerprints.RecordsSource(source));
        string[] notes =
        [
            .. new[] { MissingSidecarNote(projectDir, [.. imported]), StillMissingNote(projectDir, left, log), blender }.OfType<string>(),
        ];
        return notes.Length == 0 ? null : string.Join(' ', notes);
    }

    /// <summary>
    /// The note naming the first source that made an import run for having no <c>.import</c> file, and how many others did:
    /// <c>imported because res://block.glb had no .import file (and 2 more).</c>, without the count when it was the only one;
    /// null when no source did.
    /// </summary>
    internal static string? MissingSidecarNote(string projectDir, IReadOnlyList<string> sources) =>
        sources.Count == 0 ? null : $"imported because {PrepScan.ResourcePathOf(projectDir, sources[0])} had no .import file{More(sources)}.";

    /// <summary>
    /// The note naming the first source the import left without an <c>.import</c> file that the record does not keep, so it
    /// stays due, and how many others: <c>res://fresh.png still has no .import file after the import (and 1 more); see
    /// &lt;log&gt;.</c>; null when there is none.
    /// </summary>
    internal static string? StillMissingNote(string projectDir, IReadOnlyList<string> sources, string? log) =>
        sources.Count == 0
            ? null
            : $"{PrepScan.ResourcePathOf(projectDir, sources[0])} still has no .import file after the import{More(sources)}; see {log}.";

    /// <summary>" (and n more)" for the sources after the first, or "" when there is only one.</summary>
    private static string More(IReadOnlyList<string> sources) => sources.Count > 1 ? $" (and {sources.Count - 1} more)" : string.Empty;

    /// <summary>
    /// The note saying Godot dropped the import because Blender's path is not set, when the import's log holds Godot's line for
    /// it (4.7.2 <c>modules/gltf/editor/editor_scene_importer_blend.cpp</c> L552): with a <c>.blend</c> to import and no
    /// Blender, Godot drops the whole batch (<c>editor/file_system/editor_file_system.cpp</c> L1049-1054). It names the record
    /// (<see cref="ImportFingerprints.PathIn"/>), whose <c>.blend</c> entries keep such a file from making the next run import.
    /// Null otherwise.
    /// </summary>
    internal static string? BlenderPathNote(string projectDir, string? log) =>
        BlenderDropped(log)
            ? "Godot dropped the import because Blender's path is not set: set [filesystem] import/blender/enabled=false in project.godot, "
                + "or set Blender's path in the editor settings and then open the project in the editor once, "
                + $"or delete {ImportFingerprints.PathIn(projectDir)} so the next run imports it."
            : null;

    /// <summary>
    /// Whether the import's log holds Godot's line for a batch it dropped because Blender's path is not set (<see cref="BlenderPathNote"/>).
    /// </summary>
    internal static bool BlenderDropped(string? log) => log is not null && File.Exists(log) && File.ReadLines(log).Any(IsBlenderLine);

    /// <summary>Whether a line of the import's log is Godot's line for Blender's path not being set.</summary>
    private static bool IsBlenderLine(string line) => line.Contains("Blender path is invalid or not set", StringComparison.Ordinal);

    /// <summary>The step, with the .uid and .import files the import it ran created, changed or deleted.</summary>
    private static PrepStep WithSidecarChanges(
        PrepStep step,
        string projectDir,
        IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> before
    )
    {
        (IReadOnlyList<string>? written, IReadOnlyList<string>? removed) = PrepScan.SidecarChanges(projectDir, before, PrepScan.Sidecars(projectDir));
        return step with { FilesWritten = written, FilesRemoved = removed };
    }

    /// <summary>
    /// The sentence a failed import's message ends with, naming the .uid and .import files it wrote or removed before it
    /// stopped, or null when it changed none.
    /// </summary>
    private static string? SidecarStoppedNote(string projectDir, IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> before)
    {
        (IReadOnlyList<string>? written, IReadOnlyList<string>? removed) = PrepScan.SidecarChanges(projectDir, before, PrepScan.Sidecars(projectDir));
        List<string> parts = [];
        if (written is not null)
        {
            parts.Add($"wrote {string.Join(", ", written)}");
        }

        if (removed is not null)
        {
            parts.Add($"removed {string.Join(", ", removed)}");
        }

        return parts.Count == 0 ? null : $"Before it stopped, the import {string.Join("; ", parts)}.";
    }

    /// <summary>
    /// Why the import must run once more, or null: its first pass logged errors (<c>ERROR:</c> lines, bare or after a gate's stamp)
    /// other than Godot's line for Blender's path not being set (<see cref="BlenderPathNote"/>), which no second pass gets past,
    /// or it registered a <c>[GlobalClass]</c> loader or saver the class cache did not name before it
    /// (<paramref name="classesBefore"/>, null when no class was due), whose loader the pass's own scan ran without, so the
    /// files it reads were left out of the uid cache.
    /// </summary>
    internal static string? SecondPassReason(string log, IReadOnlySet<string>? classesBefore, string projectDir, ProjectFiles files)
    {
        if (File.Exists(log) && File.ReadLines(log).Any(line => IsImportError(line) && !IsBlenderLine(line)))
        {
            return "its first pass logged errors";
        }

        return classesBefore is not null && RegisteredNewClasses(classesBefore, PrepScan.CachedFormatClasses(projectDir, files))
            ? "its first pass registered the project's [GlobalClass] loaders or savers, which only a later pass's scan uses"
            : null;
    }

    [GeneratedRegex(@"^(?:\S+ (?:out|err) )?ERROR:", RegexOptions.CultureInvariant)]
    private static partial Regex ImportErrorPattern();

    /// <summary>
    /// Whether a line of the import's log is an engine error: <c>ERROR:</c> at its start, or after the
    /// <c>&lt;stamp&gt; out|err </c> a gate puts before each line it logs.
    /// </summary>
    internal static bool IsImportError(string line) => ImportErrorPattern().IsMatch(line);

    /// <summary>Whether the class cache names a <c>[GlobalClass]</c> after an import that it did not name before it.</summary>
    internal static bool RegisteredNewClasses(IReadOnlySet<string> before, IReadOnlySet<string> after) => !after.IsSubsetOf(before);

    /// <summary>
    /// What the record is written from: after an import the prep ran, the project's sidecars as they are now, since that import
    /// writes a sidecar for every asset it imports, including one the scan before it never saw, and the prep's sources without
    /// a sidecar that still have none, marked when Godot dropped the batch for Blender's path (<see cref="BlenderDropped"/>);
    /// otherwise the scan's sidecars alone.
    /// </summary>
    private static ImportPass PassFor(PrepStep import, PrepContext context, ProjectFiles files, ImportBasis basis) =>
        import.State == "done"
            ? new ImportPass(
                PrepScan.ImportFiles(context.ProjectDir, context.Logger),
                [.. basis.WithoutSidecars.Where(source => !File.Exists(source + PrepScan.ImportSuffix))]
            )
            {
                Dropped = BlenderDropped(import.Log),
            }
            : new ImportPass(files.ImportFiles, []);

    /// <summary>
    /// What the prep's import decision rests on, read once per prep: the record (<see cref="ImportFingerprints"/>), the
    /// scanned sources with no sidecar (<see cref="PrepScan.SourcesWithoutSidecars"/>), and of those the ones that make an
    /// import due (<see cref="PrepScan.MissingSidecarSources"/>), so each recorded <c>.blend</c> is hashed once.
    /// </summary>
    private sealed record ImportBasis(
        IReadOnlyDictionary<string, string> Fingerprints,
        IReadOnlyList<string> WithoutSidecars,
        IReadOnlyList<string> MissingSidecars
    )
    {
        /// <summary>The basis of a prep of <paramref name="projectDir"/>, from its scan.</summary>
        public static ImportBasis Of(string projectDir, ProjectFiles files, ILogger logger)
        {
            IReadOnlyDictionary<string, string> fingerprints = ImportFingerprints.Read(projectDir, logger);
            IReadOnlyList<string> withoutSidecars = PrepScan.SourcesWithoutSidecars(projectDir, files);
            return new ImportBasis(fingerprints, withoutSidecars, PrepScan.MissingSidecarSources(projectDir, withoutSidecars, fingerprints));
        }
    }

    /// <summary>
    /// Runs the import again, for <paramref name="reason"/> (<see cref="SecondPassReason"/>): a cold first pass logs errors
    /// for resources it meets before their own import and still exits 0, and a first pass that registers a
    /// <c>[GlobalClass]</c> loader scanned without it. Into the first pass's log, or, when the prep wrapper writes the log,
    /// into <c>import.2.log</c>.
    /// </summary>
    private static async Task<PrepStep> SecondImportAsync(
        string godot,
        PrepContext context,
        PrepWrapper? wrapper,
        ToolProcessResult first,
        string reason,
        CancellationToken cancellationToken
    )
    {
        string folder = LogFolder(context.ProjectDir);
        string log = Path.Combine(folder, "import.log");
        bool ownLog = wrapper is { OwnsLog: true };
        string secondLog = ownLog ? Path.Combine(folder, "import.2.log") : log;
        ToolProcessRequest request = ImportRequest(godot, context.ProjectDir, secondLog, append: !ownLog);
        ToolProcessResult second = await RunImportAsync(request, context, wrapper, cancellationToken);
        long firstMs = (long)first.Elapsed.TotalMilliseconds;
        long secondMs = (long)second.Elapsed.TotalMilliseconds;
        string where = ownLog ? $"their logs are {log} and {secondLog}" : $"both are in {log}";
        string note = $"The import ran twice, because {reason}: {firstMs} ms, then {secondMs} ms; {where}.";
        return new PrepStep("done", firstMs + secondMs, note)
        {
            Log = log,
            // The second pass is the one the import ended with, so its verdict is the import's.
            Gate = GateVerdictOf(wrapper, secondLog, context.Logger),
        };
    }

    // --import opens the editor headless, waits for its first scan and quits; it never reads override.cfg.
    private static ToolProcessRequest ImportRequest(string godot, string projectDir, string log, bool append) =>
        new(godot, ["--headless", "--path", projectDir, "--import"], projectDir, log, Ceiling) { AppendToLog = append };

    /// <exception cref="SessionException">The import hit its ceiling, was stopped by the prep wrapper, or exited with an error code.</exception>
    private static async Task<ToolProcessResult> RunImportAsync(
        ToolProcessRequest request,
        PrepContext context,
        PrepWrapper? wrapper,
        CancellationToken cancellationToken
    )
    {
        string projectDir = context.ProjectDir;
        string log = request.LogPath;
        ToolProcessResult imported = await RunStepAsync(request, "Godot", wrapper, context.Logger, cancellationToken);
        if (context.RetryBootCrashOnce && GodotSession.CrashedAtBoot(imported))
        {
            // The crash precedes Main::setup, so the import never began; the run again is the import's only one.
            cancellationToken.ThrowIfCancellationRequested();
            GodotSession.LogBootCrashRetried(context.Logger, $"the Godot import of {projectDir}");
            imported = await RunStepAsync(request, "Godot", wrapper, context.Logger, cancellationToken);
        }

        if (imported.WasKilled)
        {
            throw new SessionException(KilledStepMessage($"The Godot import of {projectDir}", imported, "the game was not started", log));
        }

        if (wrapper is not null && PrepWrapper.Stopped(imported))
        {
            throw new SessionException(wrapper.StoppedNote("the import", log));
        }

        return imported.ExitCode == 0
            ? imported
            : throw new SessionException($"The Godot import of {projectDir} exited {imported.ExitCode}, so the game was not started. Its log: {log}");
    }

    /// <summary>
    /// A process's <c>prep</c>: the command run once, through the project's prepWrapper when it names one, before the process
    /// starts, with the build's ceiling and a log of its own beside the process's. Wrapped or not, it runs in the process's own
    /// folder and with the variables the profile sets for the process.
    /// </summary>
    /// <param name="projectDir">The folder holding the godot-mcp.json: the prep's log folder and its wrapper's working directory.</param>
    /// <param name="session">The process session the prep runs before, as its log and its refusals name it.</param>
    /// <param name="command">The profile's <c>prep</c>; its program is resolved as the process's own is.</param>
    /// <param name="cwd">The folder the process runs in, which its prep runs in too.</param>
    /// <param name="env">The variables the profile sets for the process, which its prep runs with too.</param>
    /// <param name="logger">Where the prep's process is logged.</param>
    /// <param name="cancellationToken">Ends the prep when the call was cancelled.</param>
    /// <exception cref="SessionException">
    /// The prep could not be started, was stopped, exited non-zero, or the prep wrapper stopped it.
    /// </exception>
    internal static async Task<ProcessPrepResult> RunProcessPrepAsync(
        string projectDir,
        string session,
        IReadOnlyList<string> command,
        string cwd,
        IReadOnlyDictionary<string, string> env,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        string log = Path.Combine(LogFolder(projectDir), $"process-{session}.prep.log");
        IReadOnlyList<string> argv = [PrepWrapper.ResolveProgram(command[0], projectDir), .. command.Skip(1)];
        ToolProcessRequest request = new(argv[0], [.. argv.Skip(1)], cwd, log, Ceiling) { SetVariables = env };
        var wrapper = PrepWrapper.Read(projectDir);
        // The wrapper runs from the project folder, so the prep's own command gets that folder back: a build's relative paths
        // and a bare script name need the process's cwd, wrapped or not.
        ToolProcessRequest step = wrapper is null ? request : wrapper.Wrap(request, PrepWrapper.HeavySlot) with { WorkingDirectory = cwd };
        string tool = wrapper is null ? $"the prep of process {session}" : $"The prepWrapper of {ProjectProfile.FileName}";
        ToolProcessResult ran = await RunToolAsync(step, tool, logger, cancellationToken);
        if (ran.WasKilled)
        {
            throw new SessionException(KilledStepMessage($"The prep of process {session}", ran, "the process was not started", log));
        }

        if (wrapper is not null && PrepWrapper.Stopped(ran))
        {
            throw new SessionException(wrapper.StoppedNote($"the prep of process {session}", log));
        }

        if (ran.ExitCode != 0)
        {
            throw new SessionException($"The prep of process {session} exited {ran.ExitCode}, so it was not started. Its log: {log}");
        }

        return new ProcessPrepResult(argv, ran.ExitCode, (long)ran.Elapsed.TotalMilliseconds, log) { Gate = GateVerdictOf(wrapper, log, logger) };
    }

    /// <summary>Runs a prep process, through the prep wrapper when the project names one.</summary>
    /// <exception cref="SessionException">The process, or the wrapper, could not be started.</exception>
    private static Task<ToolProcessResult> RunStepAsync(
        ToolProcessRequest request,
        string tool,
        PrepWrapper? wrapper,
        ILogger logger,
        CancellationToken cancellationToken
    ) =>
        wrapper is null
            ? RunToolAsync(request, tool, logger, cancellationToken)
            : RunToolAsync(wrapper.Wrap(request, PrepWrapper.HeavySlot), $"The prepWrapper of {ProjectProfile.FileName}", logger, cancellationToken);

    /// <exception cref="SessionException">The tool could not be started.</exception>
    private static async Task<ToolProcessResult> RunToolAsync(
        ToolProcessRequest request,
        string tool,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await ToolProcess.RunAsync(request, logger, cancellationToken);
        }
        catch (Win32Exception e)
        {
            throw new SessionException($"{tool} could not be started from {request.FileName}: {e.Message}.", e);
        }
    }

    /// <summary>Whether the prep builds: not at all, incrementally, or as a rebuild of every file.</summary>
    private enum BuildNeed
    {
        None,
        Build,
        Rebuild,
    }

    /// <summary>
    /// One prep step's outcome: its state for the result, how long it ran when it ran, the log the process it ran wrote, a
    /// note, a failed build's errors, and the prep wrapper's verdict when it ran through one.
    /// </summary>
    private sealed record PrepStep(string State, long? Milliseconds, string? Note)
    {
        public CompilerErrorList? Errors { get; init; }

        /// <summary>The step's log file; left out when the step ran no process.</summary>
        public string? Log { get; init; }

        /// <summary>The wrapper's verdict from the step's capture log; null when it ran through no wrapper or left none.</summary>
        public string? Gate { get; init; }

        /// <summary>The .uid and .import files the step's import created or changed, as res:// paths; null when it changed none.</summary>
        public IReadOnlyList<string>? FilesWritten { get; init; }

        /// <summary>The .uid and .import files the step's import deleted, as res:// paths; null when it deleted none.</summary>
        public IReadOnlyList<string>? FilesRemoved { get; init; }
    }
}

/// <summary>What a headless run's prep did, and the compiler errors of a build that failed (its build is then <c>failed</c>).</summary>
internal sealed record PrepOutcome(PrepResult Result, CompilerErrorList? BuildErrors);

/// <summary>
/// What a build alone did (<see cref="ProjectPrep.RunBuildAsync"/>): <see cref="Build"/> is up-to-date, built, failed, no-csproj
/// or skipped, <see cref="Errors"/> holds a failed build's compiler errors, and <see cref="Gate"/> the prep wrapper's verdict line
/// when it ran through one and left it.
/// </summary>
internal sealed record BuildOutcome(string Build, CompilerErrorList? Errors, string? Gate);
