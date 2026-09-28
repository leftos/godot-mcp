using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The headless signal tools (headless/scene_signals.gd): get_node_signals, connect_signal and disconnect_signal, on the
/// persistent connections a scene file saves.
/// </summary>
internal sealed partial class HeadlessTools
{
    internal const string SignalTargetDescriptionText = "The node whose method is called, by its path from the scene's root (\".\" for the root).";

    [McpServerTool(Name = "get_node_signals", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Lists a scene file's node's signals and the connections the scene makes from each, in a headless Godot, without running "
            + "the game; nothing is saved. The scene is instantiated to read it, so its scripts' _init runs (never _ready). Signals "
            + "are the node's class and script signals (a C# script's once the project's assembly is built); connections are the "
            + "persistent ones, those a scene file saves, not those a script makes with connect(). Returns {path, type, signals: "
            + "[{name, args, connections: [{target, method, binds?, inherited?}]}], warning?, errors?}: args are the signal's "
            + "argument names, target the called node's path from the scene's root (null outside the scene), binds the values bound "
            + "to the call, and inherited true for a connection the scene gets from a scene it instances or inherits, which only "
            + "that scene's file can change. warning says the node's C# script signals are missing because the C# build failed, "
            + "or may be missing because prepare \"never\" skipped the build."
            + RefusedNote
    )]
    public async Task<string> GetNodeSignalsAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description("The scene: a res:// path or a path relative to the project folder, ending .tscn or .scn.")] string scenePath,
        [Description(NodePathDescription)] string nodePath,
        [Description("{prepare}: " + PrepareDescription)] HeadlessOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        string node = CheckNodePath(nodePath);
        bool prepare = RunOptions.ParsePrepare(options?.Prepare);
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            JsonObject parameters = new() { ["scene"] = CheckScenePath(projectDir, scenePath), ["nodePath"] = node };
            HeadlessRequest request = new(projectDir, "get_node_signals", parameters, prepare, RunCeiling);
            return HeadlessRunner.RunAsync(sessions, request, cancellationToken);
        });
        return WithErrors(run);
    }

    [McpServerTool(Name = "connect_signal", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Connects a node's signal to a method of a node in a scene file and saves the scene, in a headless Godot, without running "
            + "the game. The connection is persistent, saved as the editor saves one. target.binds are values passed to the method "
            + "after the signal's own arguments, each converted to the type of the parameter it fills. Refused, with nothing saved: "
            + "a missing node, signal or method; the emitting node inside an instanced scene, unless the instance is an editable "
            + "instance (the target may be inside one); a signal's arguments and binds that together do not fit the method's "
            + "parameter count, or a bind that does not convert; and a signal already connected to that method, whatever its binds. "
            + "Returns {from, signal, target, method, uidFilesWritten?, warning?, errors?}: from and target are paths from the scene's root."
            + WriteNote
            + EditNote
    )]
    public async Task<string> ConnectSignalAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description(ScenePathDescription)] string scenePath,
        [Description(NodePathDescription + " The node that emits the signal.")] string nodePath,
        [Description("The signal's name: a class's (pressed, body_entered) or one the node's script declares.")] string signal,
        [Description(
            "{nodePath, method, binds?}: the node whose method is called, by its path from the scene's root; the method's name; "
                + "and values to bind, as JSON."
        )]
            ConnectTarget target,
        CancellationToken cancellationToken = default
    )
    {
        // Refused before the project is read; the builder checks them again.
        _ = CheckNodePath(nodePath);
        _ = CheckSignalName(signal);
        _ = CheckConnectTarget(target);
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            string scene = CheckEditableScenePath(projectDir, scenePath);
            JsonObject parameters = ConnectSignalParameters(nodePath, signal, target);
            parameters["scene"] = scene;
            return RunWriteAsync(projectDir, "connect_signal", parameters, cancellationToken);
        });
        return WithErrors(run);
    }

    /// <summary>connect_signal's request parameters but the scene: <c>{nodePath, signal, target: {nodePath, method, binds}}</c>.</summary>
    /// <exception cref="McpException">
    /// As <see cref="CheckNodePath"/>, then <see cref="CheckSignalName"/>, then <see cref="CheckConnectTarget"/>.
    /// </exception>
    internal static JsonObject ConnectSignalParameters(string nodePath, string signal, ConnectTarget target) =>
        new()
        {
            ["nodePath"] = CheckNodePath(nodePath),
            ["signal"] = CheckSignalName(signal),
            ["target"] = CheckConnectTarget(target),
        };

    [McpServerTool(Name = "disconnect_signal", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Removes a connection from a node's signal to a method in a scene file and saves the scene, in a headless Godot, without "
            + "running the game; the connection is found by its target and method, whatever its binds. Refused, with nothing "
            + "saved: a missing node, a connection the scene does not make, and one it gets from a scene it instances or inherits "
            + "(disconnect it in that scene's file, which the error names). Returns {from, signal, target, method, "
            + "uidFilesWritten?, warning?, errors?}: from "
            + "and target are paths from the scene's root."
            + WriteNote
            + EditNote
    )]
    public async Task<string> DisconnectSignalAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description(ScenePathDescription)] string scenePath,
        [Description(NodePathDescription + " The node that emits the signal.")] string nodePath,
        [Description("The signal's name.")] string signal,
        [Description("{nodePath, method}: the node whose method the connection calls, by its path from the scene's root, and the method's name.")]
            DisconnectTarget target,
        CancellationToken cancellationToken = default
    )
    {
        // Refused before the project is read; the builder checks them again.
        _ = CheckNodePath(nodePath);
        _ = CheckSignalName(signal);
        _ = CheckTargetMethod(target?.NodePath, target?.Method);
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            string scene = CheckEditableScenePath(projectDir, scenePath);
            JsonObject parameters = DisconnectSignalParameters(nodePath, signal, target);
            parameters["scene"] = scene;
            return RunWriteAsync(projectDir, "disconnect_signal", parameters, cancellationToken);
        });
        return WithErrors(run);
    }

    /// <summary>disconnect_signal's request parameters but the scene: <c>{nodePath, signal, target: {nodePath, method}}</c>.</summary>
    /// <exception cref="McpException">
    /// As <see cref="CheckNodePath"/>, then <see cref="CheckSignalName"/>, then <see cref="CheckTargetMethod"/>.
    /// </exception>
    internal static JsonObject DisconnectSignalParameters(string nodePath, string signal, DisconnectTarget? target) =>
        new()
        {
            ["nodePath"] = CheckNodePath(nodePath),
            ["signal"] = CheckSignalName(signal),
            ["target"] = CheckTargetMethod(target?.NodePath, target?.Method),
        };

    /// <summary>A signal's name, trimmed.</summary>
    /// <exception cref="McpException">The name is empty.</exception>
    internal static string CheckSignalName(string signal)
    {
        string name = signal?.Trim() ?? string.Empty;
        return name.Length == 0 ? throw new McpException("signal is empty; name the signal (pressed, body_entered...).") : name;
    }

    /// <summary>connect_signal's target as the request's <c>{nodePath, method, binds}</c>.</summary>
    /// <exception cref="McpException">The target's node path is not relative to the scene root, or it names no method.</exception>
    internal static JsonObject CheckConnectTarget(ConnectTarget? target)
    {
        JsonObject checkedTarget = CheckTargetMethod(target?.NodePath, target?.Method);
        checkedTarget["binds"] = new JsonArray([.. (target?.Binds ?? []).Select(ToNode)]);
        return checkedTarget;
    }

    /// <summary>A signal target's <c>{nodePath, method}</c>, each checked and trimmed.</summary>
    /// <exception cref="McpException">The node path is not relative to the scene root, or the method is empty.</exception>
    internal static JsonObject CheckTargetMethod(string? nodePath, string? method)
    {
        string node = CheckNodePath(nodePath ?? string.Empty);
        string name = method?.Trim() ?? string.Empty;
        return name.Length == 0
            ? throw new McpException("target.method is empty; name the method the signal calls.")
            : new JsonObject { ["nodePath"] = node, ["method"] = name };
    }
}

/// <summary>The method connect_signal connects a signal to.</summary>
internal sealed record ConnectTarget(
    [property: Description(HeadlessTools.SignalTargetDescriptionText)] string NodePath,
    [property: Description("The method's name.")] string Method,
    [property: Description(
        "Values passed to the method after the signal's own arguments, as JSON, converted to the parameters' declared types; an "
            + "untyped parameter takes an integral number as an int."
    )]
        JsonElement[]? Binds = null
);

/// <summary>The method whose connection disconnect_signal removes.</summary>
internal sealed record DisconnectTarget(
    [property: Description(HeadlessTools.SignalTargetDescriptionText)] string NodePath,
    [property: Description("The method's name.")] string Method
);
