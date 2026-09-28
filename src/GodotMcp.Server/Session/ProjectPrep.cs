using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// What the prep needs from its caller: the project folder, where to log, and the names of the sessions whose game runs on
/// the folder, asked only when an import is due (a launch leaves itself out; a restart can leave its own old game out too).
/// </summary>
internal sealed record PrepContext(string ProjectDir, ILogger Logger, Func<IReadOnlyList<string>> RunningSessions)
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
}

/// <summary>
/// Makes a fresh checkout runnable before a launch: builds the C# assembly when it is missing or stale, then runs a Godot
/// import when imported files are missing. The caller serialises prep per folder.
/// </summary>
internal static class ProjectPrep
{
    /// <summary>The configuration the prep builds and lists Compile items in, which the build's refusals name.</summary>
    public const string Configuration = "Debug";

    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(300);

    // Set by a dotnet test or build run above the server; they would point the nested build at another MSBuild.
    private static readonly string[] InheritedMsBuildVariables = ["MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH"];

    /// <summary>The server's own folder in a project, already git-ignored under <c>.godot/</c>.</summary>
    public static string LogFolder(string projectDir) => Path.Combine(projectDir, ".godot", "godot-mcp");

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

    private static async Task<PrepOutcome> RunCoreAsync(PrepContext context, bool reportRedBuild, CancellationToken cancellationToken)
    {
        Log.PrepStarted(context.Logger, context.ProjectDir);
        try
        {
            ProjectFiles files = PrepScan.Scan(context.ProjectDir, context.Logger);
            PrepStep build = await BuildAsync(context, files, reportRedBuild, cancellationToken);
            PrepStep import = await ImportAsync(context, files, cancellationToken);
            string[] notes = [.. new[] { build.Note, import.Note }.OfType<string>()];
            PrepResult result = new()
            {
                Build = build.State,
                BuildMs = build.Milliseconds,
                Import = import.State,
                ImportMs = import.Milliseconds,
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

    private static async Task<PrepStep> BuildAsync(PrepContext context, ProjectFiles files, bool reportRedBuild, CancellationToken cancellationToken)
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
        string log = Path.Combine(LogFolder(projectDir), "build.log");
        ToolProcessResult built = await RunToolAsync(
            BuildRequest(Installation.FindDotnet(), csproj, log, need == BuildNeed.Rebuild),
            "dotnet",
            context.Logger,
            cancellationToken
        );
        IReadOnlyList<BuildDiagnostic> diagnostics = SaveDiagnostics(projectDir, built, log);
        long milliseconds = (long)built.Elapsed.TotalMilliseconds;
        if (reportRedBuild && StateOf(built) == "failed")
        {
            string note = $"The C# build of {csproj} failed (dotnet exited {built.ExitCode}); its log: {log}";
            return new PrepStep("failed", milliseconds, note) { Errors = CompilerErrors.Errors(diagnostics) };
        }

        CheckBuild(built, csproj, log, diagnostics);
        File.WriteAllText(PrepScan.StampPath(projectDir), string.Empty);
        return new PrepStep("built", milliseconds, null);
    }

    /// <summary>
    /// A rebuild when the context asks for saved diagnostics and none are saved, else a build when the assembly is stale
    /// (<see cref="PrepScan.IsStale"/>), else none.
    /// </summary>
    private static BuildNeed BuildNeeded(PrepContext context, ProjectFiles files, string assemblyName)
    {
        string projectDir = context.ProjectDir;
        if (context.BuildWhenUnsaved && !File.Exists(SavedBuild.PathIn(projectDir)))
        {
            return BuildNeed.Rebuild;
        }

        return PrepScan.IsStale(PrepScan.AssemblyPath(projectDir, assemblyName), PrepScan.StampPath(projectDir), files.BuildInputs)
            ? BuildNeed.Build
            : BuildNeed.None;
    }

    /// <summary>Saves the build's errors and warnings as the project's <see cref="SavedBuild"/>, and returns them.</summary>
    private static IReadOnlyList<BuildDiagnostic> SaveDiagnostics(string projectDir, ToolProcessResult built, string log)
    {
        IReadOnlyList<BuildDiagnostic> diagnostics = CompilerErrors.ParseDiagnostics(File.ReadAllText(log));
        SavedBuild.Save(projectDir, new SavedBuild(DateTime.UtcNow, StateOf(built), diagnostics));
        return diagnostics;
    }

    private static string StateOf(ToolProcessResult built)
    {
        if (built.WasKilled)
        {
            return "stopped";
        }

        return built.ExitCode == 0 ? "built" : "failed";
    }

    // --no-incremental rebuilds every file, so each one's warnings reach the log (learn.microsoft.com/dotnet/core/tools/dotnet-build).
    private static ToolProcessRequest BuildRequest(string dotnet, string csproj, string log, bool rebuild) =>
        DotnetRequest(
            dotnet,
            [
                "build",
                csproj,
                "-c",
                Configuration,
                "-p:GodotTargetPlatform=windows",
                "-p:UseSharedCompilation=false",
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
    /// configuration and environment. Its output goes to a log of its own (<see cref="CompileItemsLog"/>), so evaluations on
    /// one folder at once never share a file; the log is deleted once read, and kept when the evaluation failed.
    /// </summary>
    /// <exception cref="SessionException">dotnet could not be started, hit its ceiling, failed, or wrote no item list.</exception>
    public static async Task<IReadOnlyList<string>> CompileItemsAsync(
        string projectDir,
        string csproj,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        string log = CompileItemsLog(projectDir);
        string[] arguments =
        [
            "msbuild",
            csproj,
            "-getItem:Compile",
            $"-p:Configuration={Configuration}",
            "-p:GodotTargetPlatform=windows",
            "-nologo",
        ];
        ToolProcessResult listed = await RunToolAsync(
            DotnetRequest(Installation.FindDotnet(), arguments, csproj, log),
            "dotnet",
            logger,
            cancellationToken
        );
        string failure = $"dotnet msbuild -getItem:Compile on {csproj}";
        if (listed.WasKilled)
        {
            throw new SessionException($"{failure} did not finish ({listed.KillDetail}), so the Compile items are unknown. Its log: {log}");
        }

        if (listed.ExitCode != 0)
        {
            throw new SessionException($"{failure} failed (exited {listed.ExitCode}), so the Compile items are unknown. Its log: {log}");
        }

        IReadOnlyList<string> items;
        try
        {
            items = ReadCompileItems(File.ReadAllText(log)) ?? throw new SessionException($"{failure} wrote no item list. Its log: {log}");
        }
        catch (JsonException e)
        {
            throw new SessionException($"{failure} wrote an item list that is not JSON ({e.Message}). Its log: {log}", e);
        }

        DeleteLogged(log, logger);
        return items;
    }

    /// <summary>A log path of its own for one Compile-items evaluation: <c>.godot/godot-mcp/compile-items-&lt;guid&gt;.log</c>.</summary>
    internal static string CompileItemsLog(string projectDir) => Path.Combine(LogFolder(projectDir), $"compile-items-{Guid.NewGuid():N}.log");

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

    /// <exception cref="SessionException">The build hit its ceiling or failed.</exception>
    private static void CheckBuild(ToolProcessResult built, string csproj, string log, IReadOnlyList<BuildDiagnostic> diagnostics)
    {
        if (built.WasKilled)
        {
            throw new SessionException(
                $"The C# build of {csproj} did not finish ({built.KillDetail}), so it was stopped with its whole process tree "
                    + $"and the game was not started. Its log: {log}"
            );
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

    private static async Task<PrepStep> ImportAsync(PrepContext context, ProjectFiles files, CancellationToken cancellationToken)
    {
        if (
            !PrepScan.ImportNeeded(context.ProjectDir, files)
            && !context.ImportAssets.Any(asset => PrepScan.AssetNeedsImport(context.ProjectDir, asset))
        )
        {
            return new PrepStep("not-needed", null, null);
        }

        IReadOnlyList<string> others = context.RunningSessions();
        if (others.Count > 0)
        {
            throw new SessionException(
                $"the project at {context.ProjectDir} needs a Godot import, but session(s) {string.Join(", ", others)} are running on it; "
                    + $"stop them first{context.ImportSkipHint}."
            );
        }

        string godot = Installation.FindGodot();
        string log = Path.Combine(LogFolder(context.ProjectDir), "import.log");
        ToolProcessResult first = await RunImportAsync(godot, context, log, append: false, cancellationToken);
        if (!File.ReadLines(log).Any(line => line.StartsWith("ERROR:", StringComparison.Ordinal)))
        {
            return new PrepStep("done", (long)first.Elapsed.TotalMilliseconds, null);
        }

        // A cold first pass logs errors for resources it meets before their own import, and still exits 0.
        ToolProcessResult second = await RunImportAsync(godot, context, log, append: true, cancellationToken);
        long firstMs = (long)first.Elapsed.TotalMilliseconds;
        long secondMs = (long)second.Elapsed.TotalMilliseconds;
        string note = $"The import ran twice, because its first pass logged errors: {firstMs} ms, then {secondMs} ms; both are in {log}.";
        return new PrepStep("done", firstMs + secondMs, note);
    }

    /// <exception cref="SessionException">The import hit its ceiling or exited with an error code.</exception>
    private static async Task<ToolProcessResult> RunImportAsync(
        string godot,
        PrepContext context,
        string log,
        bool append,
        CancellationToken cancellationToken
    )
    {
        string projectDir = context.ProjectDir;
        // --import opens the editor headless, waits for its first scan and quits; it never reads override.cfg.
        ToolProcessRequest request = new(godot, ["--headless", "--path", projectDir, "--import"], projectDir, log, Ceiling) { AppendToLog = append };
        ToolProcessResult imported = await RunToolAsync(request, "Godot", context.Logger, cancellationToken);
        if (imported.WasKilled)
        {
            throw new SessionException(
                $"The Godot import of {projectDir} did not finish ({imported.KillDetail}), so it was stopped with its whole "
                    + $"process tree and the game was not started. Its log: {log}"
            );
        }

        return imported.ExitCode == 0
            ? imported
            : throw new SessionException($"The Godot import of {projectDir} exited {imported.ExitCode}, so the game was not started. Its log: {log}");
    }

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

    /// <summary>One prep step's outcome: its state for the result, how long it ran when it ran, a note, and a failed build's errors.</summary>
    private sealed record PrepStep(string State, long? Milliseconds, string? Note)
    {
        public CompilerErrorList? Errors { get; init; }
    }
}

/// <summary>What a headless run's prep did, and the compiler errors of a build that failed (its build is then <c>failed</c>).</summary>
internal sealed record PrepOutcome(PrepResult Result, CompilerErrorList? BuildErrors);
