using System.ComponentModel;
using System.Text.Json;
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
            + "started, and its log is the file its output is also written to."
    )]
    public async Task<string> RunProcessAsync(
        [Description("The folder that holds the project's project.godot.")] string projectPath,
        [Description("A process declared in the project's godot-mcp.json processes section.")] string process,
        [Description("Arguments appended to the process's command.")] string[]? args = null,
        [Description("{session}; when left out, the session is named after the process, fitted to the name rule.")] ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ProcessStartResult result = await RunAsync(() => StartAsync(projectPath, process, args ?? [], options?.Session, cancellationToken));
        return JsonSerializer.Serialize(result, Json);
    }

    private async Task<ProcessStartResult> StartAsync(
        string projectPath,
        string process,
        string[] args,
        string? session,
        CancellationToken cancellationToken
    )
    {
        ProjectProfile profile = LoadProfile(projectPath);
        ProcessProfile declared = ProcessOf(profile, process);
        string[] command = CommandFor(declared, profile.ProjectDir, args);
        GodotSession reserved = await sessions.ReserveProcessAsync(session ?? process, profile.ProjectDir, defaultName: session is null);
        try
        {
            (int processId, string log) = reserved.StartProcess(declared, command, cancellationToken);
            return new ProcessStartResult(reserved.Name, reserved.ProjectDir, process, processId, command, log);
        }
        catch
        {
            sessions.Forget(reserved);
            reserved.Dispose();
            throw;
        }
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

/// <summary>run_process's options: the new session's name, or null for the process's name fitted to the name rule.</summary>
internal sealed record ProcessRunOptions(string? Session = null);

/// <summary>
/// What run_process started: its session and project folder, the process's name as the file declares it, its process id,
/// the argv it started with and the log its output is written to.
/// </summary>
internal sealed record ProcessStartResult(
    string Session,
    string ProjectPath,
    string Process,
    int ProcessId,
    IReadOnlyList<string> Command,
    string Log
);
