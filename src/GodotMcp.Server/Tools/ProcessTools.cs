using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>run_process: starts a program a project's godot-mcp.json declares, as a session beside its games.</summary>
[McpServerToolType]
internal sealed class ProcessTools(SessionRegistry sessions)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "run_process", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Starts a process a godot-mcp.json \"processes\" section declares: a game server or a mock backend a drive needs beside "
            + "its games. It keeps running after the call, its stdout and stderr are captured for get_debug_output and wait_for "
            + "with an output condition, and stop_project with its session ends it. The result's processId is the process "
            + "started, and its log is the file its output is also written to. A process profile's prep, when it declares one, "
            + "runs through the project's prepWrapper before the process starts and again on restart_project; the result's prep "
            + "holds that step's command, exit, ms and log. A prep that fails refuses the start."
    )]
    public async Task<string> RunProcessAsync(
        [Description("The folder that holds the project's project.godot.")] string projectPath,
        [Description("A process declared in the project's godot-mcp.json processes section.")] string process,
        [Description("Arguments appended to the process's command.")] string[]? args = null,
        [Description(
            "{session, ready}; session names the new session (left out, the process's name fitted to the name rule), and ready "
                + "overrides the profile's ready line and its timeout. A profile's prep is not an option: it runs before the "
                + "start and again on a restart."
        )]
            ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ProcessStartResult result = await RunAsync(() => StartAsync(projectPath, process, args ?? [], options, cancellationToken));
        return JsonSerializer.Serialize(result, Json);
    }

    private async Task<ProcessStartResult> StartAsync(
        string projectPath,
        string process,
        string[] args,
        ProcessRunOptions? options,
        CancellationToken cancellationToken
    )
    {
        ProjectProfile profile = LoadProfile(projectPath);
        ProcessProfile declared = ProcessOf(profile, process);
        string[] command = CommandFor(declared, profile.ProjectDir, args);
        ReadyProfile? ready = ReadyFor(declared, options?.Ready);
        string? session = options?.Session;
        GodotSession reserved = await sessions.ReserveProcessAsync(session ?? process, profile.ProjectDir, defaultName: session is null);
        try
        {
            Func<CancellationToken, Task<ProcessPrepResult>>? prep = PrepRunner(profile.ProjectDir, reserved.Name, declared);
            ProcessPrepResult? prepared = prep is null ? null : await prep(cancellationToken);
            (int processId, string log, ReadyResult? met) = await reserved.StartProcess(process, declared, command, ready, cancellationToken, prep);
            return new ProcessStartResult(reserved.Name, reserved.ProjectDir, process, processId, command, log) { Ready = met, Prep = prepared };
        }
        catch
        {
            sessions.Forget(reserved);
            reserved.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The prep a run_process runs before its start, and a restart_project runs again: under the folder's prep lock, so one
    /// prep at a time builds in it; null when the profile declares no prep.
    /// </summary>
    private Func<CancellationToken, Task<ProcessPrepResult>>? PrepRunner(string projectDir, string session, ProcessProfile declared)
    {
        if (declared.Prep is not { } prep)
        {
            return null;
        }

        return async cancellationToken =>
        {
            SemaphoreSlim folderLock = sessions.PrepLock(projectDir);
            await folderLock.WaitAsync(cancellationToken);
            try
            {
                return await ProjectPrep.RunProcessPrepAsync(
                    projectDir,
                    session,
                    prep,
                    declared.Cwd ?? projectDir,
                    declared.Env,
                    sessions.Logger,
                    cancellationToken
                );
            }
            finally
            {
                folderLock.Release();
            }
        };
    }

    /// <summary>The project's godot-mcp.json, refused when the folder has none: run_process starts only declared processes.</summary>
    /// <exception cref="McpException">The path is empty, or the folder has no godot-mcp.json.</exception>
    internal static ProjectProfile LoadProfile(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new McpException("projectPath is empty. Pass the folder that holds the project's project.godot.");
        }

        string projectDir = ProjectPaths.Normalise(projectPath);
        var profile = ProjectProfile.Load(projectDir);
        return profile.Exists
            ? profile
            : throw new McpException(
                $"No godot-mcp.json beside project.godot in {projectDir}: run_process starts only the processes its "
                    + "\"processes\" section declares."
            );
    }

    /// <summary>The process a profile declares, refused with every one it declares when the name is not among them.</summary>
    /// <exception cref="McpException">The profile declares no such process.</exception>
    internal static ProcessProfile ProcessOf(ProjectProfile profile, string name) =>
        profile.Processes.TryGetValue(name, out ProcessProfile? declared)
            ? declared
            : throw new McpException($"{profile.FilePath} has no process '{name}'; it declares: {Declarations(profile)}.");

    /// <summary>A process's key fitted to the name rule as a folder name is, the session a call without one gets.</summary>
    internal static string DefaultSession(string process) => SessionRegistry.FitName(process);

    /// <summary>
    /// The argv a process starts with: its command's program, resolved as prepWrapper's is, then the command's arguments and
    /// then <paramref name="args"/>.
    /// </summary>
    internal static string[] CommandFor(ProcessProfile profile, string projectDir, IReadOnlyList<string>? args) =>
        [PrepWrapper.ResolveProgram(profile.Command[0], projectDir), .. profile.Command.Skip(1), .. (args ?? [])];

    /// <summary>
    /// The ready line a call waits on: its own options replacing the profile's ready whole, or the profile's when it passes
    /// none. A call's own ready must name a non-empty pattern, a valid regular expression and a timeout from 1 to 600000 ms.
    /// </summary>
    /// <exception cref="McpException">The call's ready output is missing or not a valid regular expression, or its timeout is out of
    /// range.</exception>
    internal static ReadyProfile? ReadyFor(ProcessProfile declared, ReadyOptions? options)
    {
        if (options is null)
        {
            return declared.Ready;
        }

        string output = options.Output is { Length: > 0 } given
            ? given
            : throw new McpException("options.ready.output is missing: pass the regular expression a printed line is matched against.");
        Regex regex;
        try
        {
            regex = ScratchProfile.Compile(output);
        }
        catch (ArgumentException e)
        {
            throw new McpException($"options.ready.output is not a valid .NET regular expression: {e.Message}. Fix or remove it.");
        }

        int timeoutMs = options.TimeoutMs ?? ReadyProfile.DefaultTimeoutMs;
        return ReadyProfile.IsTimeout(timeoutMs)
            ? new ReadyProfile(output, timeoutMs) { Regex = regex }
            : throw new McpException($"options.ready.timeoutMs must be a whole number from 1 to {ReadyProfile.MaxTimeoutMs}; got {timeoutMs}.");
    }

    /// <summary>Every declared process as "name (description)", or the name alone without a description, in file order.</summary>
    private static string Declarations(ProjectProfile profile) =>
        profile.Processes.Count == 0
            ? "none"
            : string.Join(
                ", ",
                profile.Processes.Select(entry => entry.Value.Description is { } description ? $"{entry.Key} ({description})" : entry.Key)
            );

    private static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new McpException($"The process could not be started: {e.Message}", e);
        }
    }
}

/// <summary>run_process's options: the new session's name, or null for the process's name fitted to the name rule, and its ready line.</summary>
internal sealed record ProcessRunOptions(
    [property: Description("The session's name; left out, the session is named after the process, fitted to the name rule as a folder name is.")]
        string? Session = null,
    [property: Description(
        "The line the call waits for before returning, replacing the profile's ready whole; left out, the profile's ready is used."
    )]
        ReadyOptions? Ready = null
);

/// <summary>
/// A ready line a call waits on: the regular expression a stdout or stderr line must match and its timeout, 60000 ms when the
/// call leaves it out.
/// </summary>
internal sealed record ReadyOptions(
    [property: Description(
        "The .NET regular expression a stdout or stderr line is matched against before run_process returns; it replaces the "
            + "profile's ready.output, and ready.timeoutMs then defaults to 60000."
    )]
        string? Output = null,
    [property: Description("How long to wait for the line, in milliseconds of load-adjusted time, 1 to 600000; 60000 when left out.")]
        int? TimeoutMs = null
);

/// <summary>
/// What run_process started: its session and project folder, the process's name as the file declares it, its process id,
/// the argv it started with and the log its output is written to; ready is the line it met when it waited on one, and prep
/// the prep it ran before the start (left out when the profile declares none).
/// </summary>
internal sealed record ProcessStartResult(
    string Session,
    string ProjectPath,
    string Process,
    int ProcessId,
    IReadOnlyList<string> Command,
    string Log,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReadyResult? Ready = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProcessPrepResult? Prep = null
);

/// <summary>The line a ready met: the line cut as get_debug_output cuts it, the stream it came from, and the real elapsed milliseconds.</summary>
internal sealed record ReadyResult(string Line, string Stream, long Ms);
