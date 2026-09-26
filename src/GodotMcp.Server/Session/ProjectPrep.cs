using System.ComponentModel;
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
}

/// <summary>
/// Makes a fresh checkout runnable before a launch: builds the C# assembly when it is missing or stale, then runs a Godot
/// import when imported files are missing. The caller serialises prep per folder.
/// </summary>
internal static class ProjectPrep
{
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

        string stamp = PrepScan.StampPath(projectDir);
        if (!PrepScan.IsStale(PrepScan.AssemblyPath(projectDir, lookup.AssemblyName!), stamp, files.BuildInputs))
        {
            return new PrepStep("up-to-date", null, null);
        }

        string csproj = lookup.ProjectFile!;
        string log = Path.Combine(LogFolder(projectDir), "build.log");
        ToolProcessResult built = await RunToolAsync(
            BuildRequest(Installation.FindDotnet(), csproj, log),
            "dotnet",
            context.Logger,
            cancellationToken
        );
        long milliseconds = (long)built.Elapsed.TotalMilliseconds;
        if (reportRedBuild && !built.KilledByCeiling && built.ExitCode != 0)
        {
            string note = $"The C# build of {csproj} failed (dotnet exited {built.ExitCode}); its log: {log}";
            return new PrepStep("failed", milliseconds, note) { Errors = CompilerErrors.Parse(File.ReadAllText(log)) };
        }

        CheckBuild(built, csproj, log);
        File.WriteAllText(stamp, string.Empty);
        return new PrepStep("built", milliseconds, null);
    }

    private static ToolProcessRequest BuildRequest(string dotnet, string csproj, string log) =>
        new(
            dotnet,
            ["build", csproj, "-c", "Debug", "-p:GodotTargetPlatform=windows", "-p:UseSharedCompilation=false"],
            Path.GetDirectoryName(csproj)!,
            log,
            Ceiling
        )
        {
            // No reused MSBuild nodes and no shared compiler server, so nothing the build starts outlives it.
            SetVariables = new Dictionary<string, string> { ["MSBUILDDISABLENODEREUSE"] = "1" },
            RemovedVariables = InheritedMsBuildVariables,
        };

    /// <exception cref="SessionException">The build hit its ceiling or failed.</exception>
    private static void CheckBuild(ToolProcessResult built, string csproj, string log)
    {
        if (built.KilledByCeiling)
        {
            throw new SessionException(
                $"The C# build of {csproj} did not finish within {Ceiling.TotalSeconds:0} s, so it was stopped with its whole process tree "
                    + $"and the game was not started. Its log: {log}"
            );
        }

        if (built.ExitCode == 0)
        {
            return;
        }

        CompilerErrorList errors = CompilerErrors.Parse(File.ReadAllText(log));
        string listed = errors.Total == 0 ? "No compiler errors were found in its output." : string.Join('\n', errors.Errors);
        string omitted = errors.Total > errors.Errors.Count ? $"\n(and {errors.Total - errors.Errors.Count} more)" : string.Empty;
        throw new SessionException(
            $"The C# build of {csproj} failed (dotnet exited {built.ExitCode}), so the game was not started. "
                + $"Fix the errors, or pass options.prepare: \"never\" to launch without building:\n{listed}{omitted}\nFull log: {log}"
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
        if (imported.KilledByCeiling)
        {
            throw new SessionException(
                $"The Godot import of {projectDir} did not finish within {Ceiling.TotalSeconds:0} s, so it was stopped with its whole "
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

    /// <summary>One prep step's outcome: its state for the result, how long it ran when it ran, a note, and a failed build's errors.</summary>
    private sealed record PrepStep(string State, long? Milliseconds, string? Note)
    {
        public CompilerErrorList? Errors { get; init; }
    }
}

/// <summary>What a headless run's prep did, and the compiler errors of a build that failed (its build is then <c>failed</c>).</summary>
internal sealed record PrepOutcome(PrepResult Result, CompilerErrorList? BuildErrors);
