using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>What run_project launches once the profile is merged: the request, and the session name (null for the default).</summary>
internal sealed record ProfileLaunch(LaunchRequest Request, string? Session);

/// <summary>
/// The values the top level of godot-mcp.json or one of its presets sets: null, or empty for the argument lists, where it
/// sets none. <see cref="Session"/> and <see cref="Description"/> are only ever set by a preset.
/// </summary>
internal sealed record ProfileValues(
    string? Scene,
    IReadOnlyList<string> UserArgs,
    IReadOnlyList<string> EngineArgs,
    string? Resolution,
    bool? Quiet,
    string? Session,
    string? Description
)
{
    /// <summary>Values that set nothing.</summary>
    public static readonly ProfileValues None = new(null, [], [], null, null, null, null);
}

/// <summary>
/// A project's launch profile, the optional <c>godot-mcp.json</c> beside <c>project.godot</c>: top-level defaults for every
/// run_project on the folder, and named presets that layer over them. The file may carry <c>//</c> and <c>/* */</c> comments and
/// a trailing comma after the last item of an object or array. Parsing is otherwise strict: an unknown key, a value of the
/// wrong type or a malformed resolution refuses the whole file.
/// </summary>
internal sealed partial class ProjectProfile
{
    /// <summary>The profile's file name, in the project folder.</summary>
    public const string FileName = "godot-mcp.json";

    private const string TopLevel = "top level";
    private const string PrepWrapperKey = "prepWrapper";
    private const string PrepKey = "prep";
    private const string ScratchKey = "scratch";
    private const string ProcessesKey = "processes";
    private static readonly string[] TopLevelKeys =
    [
        "scene",
        "userArgs",
        "engineArgs",
        "resolution",
        "quiet",
        "presets",
        PrepWrapperKey,
        ScratchKey,
        ProcessesKey,
    ];
    private static readonly string[] PresetKeys = ["scene", "userArgs", "engineArgs", "resolution", "quiet", "session", "description"];
    private static readonly string[] ScratchKeys = ["folder", "userArgs", "engineArgs", "pace", "ceilings", "setup", "known", "patterns", "parallel"];
    private static readonly string[] ProcessKeys = ["command", "cwd", "env", "ready", "description", PrepKey];
    private static readonly string[] ReadyKeys = ["output", "timeoutMs"];
    private const string StepsKey = "steps";
    private const string TimeScaleKey = "timeScale";
    private static readonly string[] StepPaceKeys = ["seconds", "reason"];
    private static readonly string[] PaceKeys = [.. StepPaceKeys, StepsKey, TimeScaleKey];
    private static readonly string[] PatternKeys = ["pattern", "reason"];

    /// <summary>What a <c>scratch.setup</c> entry may be, for the refusals that say what one is.</summary>
    private const string SetupStepShape = "a setup step is a whole number from 0 up, or a non-empty string naming it";

    private readonly ProfileValues _defaults;
    private readonly IReadOnlyDictionary<string, ProfileValues> _presets;

    private ProjectProfile(
        string projectDir,
        bool exists,
        ProfileValues defaults,
        IReadOnlyDictionary<string, ProfileValues> presets,
        IReadOnlyList<string>? prepWrapper
    )
    {
        ProjectDir = projectDir;
        FilePath = Path.Combine(projectDir, FileName);
        Exists = exists;
        _defaults = defaults;
        _presets = presets;
        PrepWrapper = prepWrapper;
    }

    /// <summary>The project folder the profile belongs to; merged requests launch it.</summary>
    public string ProjectDir { get; }

    /// <summary>Where the profile's file is, or would be.</summary>
    public string FilePath { get; }

    /// <summary>Whether the file exists.</summary>
    public bool Exists { get; }

    /// <summary>
    /// The top-level <c>prepWrapper</c>: the program and arguments the launch prep runs its build, Compile-items listing and
    /// import through; null when the file sets none.
    /// </summary>
    public IReadOnlyList<string>? PrepWrapper { get; }

    /// <summary>The top-level <c>userArgs</c>: the user arguments every run_project on the folder starts with.</summary>
    public IReadOnlyList<string> UserArgs => _defaults.UserArgs;

    /// <summary>The <c>scratch</c> section, how run_scratches finds and judges the project's scratch scenes; null when the file has none.</summary>
    public ScratchProfile? Scratch { get; private init; }

    /// <summary>
    /// The <c>processes</c> section, the programs beside the game that run_process starts by name, keyed by their name in
    /// file order; empty when the file has none.
    /// </summary>
    public IReadOnlyDictionary<string, ProcessProfile> Processes { get; private init; } = new Dictionary<string, ProcessProfile>();

    /// <summary>The profile of a folder with no godot-mcp.json: it sets nothing and has no presets.</summary>
    public static ProjectProfile Empty(string projectDir) =>
        new(projectDir, false, ProfileValues.None, new Dictionary<string, ProfileValues>(), prepWrapper: null);

    /// <summary>Reads and checks the folder's godot-mcp.json.</summary>
    /// <param name="projectDir">The normalised project folder.</param>
    /// <returns>The profile; an empty one when the folder has no godot-mcp.json.</returns>
    /// <exception cref="McpException">The file is not valid JSON or breaks the schema.</exception>
    public static ProjectProfile Load(string projectDir)
    {
        string path = Path.Combine(projectDir, FileName);
        if (!File.Exists(path))
        {
            return Empty(projectDir);
        }

        using JsonDocument document = Parse(File.ReadAllText(path), path);
        Place top = new(path, TopLevel);
        Dictionary<string, JsonElement> keys = Keys(document.RootElement, TopLevelKeys, top);
        ProfileValues defaults = ReadValues(keys, top, session: null, description: null);
        return new ProjectProfile(projectDir, true, defaults, ReadPresets(keys, top), ReadPrepWrapper(keys, top))
        {
            Scratch = ReadScratch(keys, top),
            Processes = ReadProcesses(keys, top, projectDir),
        };
    }

    /// <summary>
    /// Layers the top level, then <paramref name="options"/>' preset, then the explicit arguments: scene, resolution and quiet
    /// are replaced by each later layer, the argument lists append in that order, and the resolution becomes
    /// <c>--resolution WxH</c> ahead of every engine argument, so an explicit one wins.
    /// </summary>
    /// <returns>The request to launch, and the session name: options' session, else the preset's, else null for the default.</returns>
    /// <exception cref="McpException">
    /// The preset is not in the file, or there is no file; or options' prepare is invalid; or options' mute is false on a quiet
    /// run.
    /// </exception>
    public ProfileLaunch Merge(string? scene, IReadOnlyList<string> userArgs, IReadOnlyList<string> engineArgs, RunOptions options)
    {
        ProfileValues preset = PresetFor(options.Preset);
        bool quiet = QuietFor(preset, options.Quiet);
        LaunchRequest request = new(
            ProjectDir,
            scene ?? preset.Scene ?? _defaults.Scene,
            EngineArgsFor(preset, engineArgs),
            [.. _defaults.UserArgs, .. preset.UserArgs, .. userArgs],
            quiet,
            options.ShutOutRealGamepads,
            options.ShouldPrepare()
        )
        {
            Record = options.Record ?? false,
            DropIdle = options.DropIdle ?? false,
            Mute = options.MuteFor(quiet),
            RepickHover = options.RepickHover ?? true,
        };
        return new ProfileLaunch(request, options.Session ?? preset.Session);
    }

    private List<string> EngineArgsFor(ProfileValues preset, IReadOnlyList<string> engineArgs)
    {
        string? resolution = preset.Resolution ?? _defaults.Resolution;
        List<string> arguments = resolution is null ? [] : ["--resolution", resolution];
        arguments.AddRange(_defaults.EngineArgs);
        arguments.AddRange(preset.EngineArgs);
        arguments.AddRange(engineArgs);
        return arguments;
    }

    private bool QuietFor(ProfileValues preset, bool? quiet) => quiet ?? preset.Quiet ?? _defaults.Quiet ?? true;

    private ProfileValues PresetFor(string? name)
    {
        if (name is null)
        {
            return ProfileValues.None;
        }

        if (!Exists)
        {
            throw new McpException(
                $"options.preset \"{name}\" names a preset, but the project has no {FileName}: looked for {FilePath}. Create it "
                    + "beside project.godot with a \"presets\" object, or leave options.preset out."
            );
        }

        return _presets.TryGetValue(name, out ProfileValues? preset) ? preset : throw UnknownPreset(name);
    }

    private McpException UnknownPreset(string name)
    {
        if (_presets.Count == 0)
        {
            return new McpException(
                $"options.preset \"{name}\" is not in {FilePath}, which has no presets. Add it under \"presets\", or leave " + "options.preset out."
            );
        }

        string names = string.Join(
            ", ",
            _presets
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => entry.Value.Description is { } description ? $"{entry.Key} ({description})" : entry.Key)
        );
        return new McpException($"options.preset \"{name}\" is not in {FilePath}; its presets are {names}. Pass one of those.");
    }

    private static JsonDocument Parse(string text, string path)
    {
        JsonDocumentOptions options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        try
        {
            return JsonDocument.Parse(text, options);
        }
        catch (JsonException e)
        {
            long line = (e.LineNumber ?? 0) + 1;
            long column = (e.BytePositionInLine ?? 0) + 1;
            int trailer = e.Message.IndexOf(" LineNumber:", StringComparison.Ordinal);
            string reason = trailer < 0 ? e.Message : e.Message[..trailer];
            throw new McpException($"{path} is not valid JSON (line {line}, column {column}): {reason} Fix the JSON there.", e);
        }
    }

    private static Dictionary<string, ProfileValues> ReadPresets(Dictionary<string, JsonElement> keys, Place top)
    {
        Dictionary<string, ProfileValues> presets = new(StringComparer.Ordinal);
        if (!keys.TryGetValue("presets", out JsonElement all))
        {
            return presets;
        }

        foreach (JsonProperty entry in Expect(all, JsonValueKind.Object, "presets", top).EnumerateObject())
        {
            Place place = new(top.Path, $"preset \"{entry.Name}\"");
            Dictionary<string, JsonElement> presetKeys = Keys(entry.Value, PresetKeys, place);
            ProfileValues preset = ReadValues(presetKeys, place, ReadSession(presetKeys, place), ReadDescription(presetKeys, place));
            if (!presets.TryAdd(entry.Name, preset))
            {
                throw Refused(top, $"the preset \"{entry.Name}\" appears twice in \"presets\"; keep one");
            }
        }

        return presets;
    }

    private static ProfileValues ReadValues(Dictionary<string, JsonElement> keys, Place place, string? session, string? description) =>
        new(
            ReadString(keys, "scene", place),
            ReadStrings(keys, "userArgs", place),
            ReadStrings(keys, "engineArgs", place),
            ReadResolution(keys, place),
            ReadBool(keys, "quiet", place),
            session,
            description
        );

    /// <summary>The object's keys, each checked against <paramref name="allowed"/> and seen once.</summary>
    private static Dictionary<string, JsonElement> Keys(JsonElement element, string[] allowed, Place place)
    {
        Dictionary<string, JsonElement> keys = new(StringComparer.Ordinal);
        foreach (JsonProperty property in ExpectObject(element, place).EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw UnknownKey(property.Name, allowed, place);
            }

            if (!keys.TryAdd(property.Name, property.Value))
            {
                throw Refused(place, $"the key \"{property.Name}\" appears twice; keep one");
            }
        }

        return keys;
    }

    private static McpException UnknownKey(string key, string[] allowed, Place place)
    {
        string hint = key == "session" && !allowed.Contains("session", StringComparer.Ordinal) ? "; session belongs inside a preset" : "";
        return Refused(place, $"unknown key \"{key}\"; the allowed keys are {string.Join(", ", allowed)}{hint}. Remove or rename it");
    }

    private static JsonElement ExpectObject(JsonElement element, Place place) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw Refused(place, $"expected a JSON object ({{ ... }}), not {Describe(element.ValueKind)}");

    private static string? ReadString(Dictionary<string, JsonElement> keys, string key, Place place) =>
        keys.TryGetValue(key, out JsonElement value) ? Expect(value, JsonValueKind.String, key, place).GetString() : null;

    private static bool? ReadBool(Dictionary<string, JsonElement> keys, string key, Place place)
    {
        if (!keys.TryGetValue(key, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw Refused(place, $"\"{key}\" must be true or false, not {Describe(value.ValueKind)}");
    }

    private static List<string> ReadStrings(Dictionary<string, JsonElement> keys, string key, Place place)
    {
        if (!keys.TryGetValue(key, out JsonElement value))
        {
            return [];
        }

        List<string> strings = [];
        foreach (JsonElement item in Expect(value, JsonValueKind.Array, key, place).EnumerateArray())
        {
            strings.Add(
                item.ValueKind == JsonValueKind.String
                    ? item.GetString()!
                    : throw Refused(place, $"\"{key}\" must be an array of strings; it holds {Describe(item.ValueKind)}")
            );
        }

        return strings;
    }

    /// <summary>The top-level <c>prepWrapper</c>, a non-empty array of non-empty strings; null when the key is absent.</summary>
    private static List<string>? ReadPrepWrapper(Dictionary<string, JsonElement> keys, Place place)
    {
        if (!keys.TryGetValue(PrepWrapperKey, out JsonElement value))
        {
            return null;
        }

        const string Shape =
            $"\"{PrepWrapperKey}\" must be a non-empty array of non-empty strings, the program and then its arguments, "
            + "e.g. [\"gate\", \"run\", \"--log\", \"{log}\", \"--\"]";
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Refused(place, $"{Shape}; it is {Describe(value.ValueKind)}");
        }

        List<string> command = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            string element =
                item.ValueKind == JsonValueKind.String ? item.GetString()! : throw Refused(place, $"{Shape}; it holds {Describe(item.ValueKind)}");
            command.Add(element.Length > 0 ? element : throw Refused(place, $"{Shape}; its item {command.Count + 1} is an empty string"));
        }

        return command.Count > 0 ? command : throw Refused(place, $"{Shape}; it is empty");
    }

    /// <summary>The <c>scratch</c> section, checked key by key; null when the key is absent.</summary>
    private static ScratchProfile? ReadScratch(Dictionary<string, JsonElement> keys, Place top)
    {
        if (!keys.TryGetValue(ScratchKey, out JsonElement value))
        {
            return null;
        }

        Place place = new(top.Path, ScratchKey);
        Dictionary<string, JsonElement> scratch = Keys(value, ScratchKeys, place);
        return new ScratchProfile(
            ReadString(scratch, "folder", place),
            scratch.ContainsKey("userArgs") ? ReadStrings(scratch, "userArgs", place) : null,
            scratch.ContainsKey("engineArgs") ? ReadStrings(scratch, "engineArgs", place) : null,
            ReadMap(scratch, "pace", place, (scene, pace) => ReadPace(scene, pace, place)),
            ReadMap(scratch, "ceilings", place, (scene, ceiling) => ReadCeiling(scene, ceiling, place)),
            ReadMap(scratch, "known", place, (scene, reason) => ReadReason(scene, reason, place)),
            scratch.ContainsKey("patterns") ? ReadPatterns(scratch, place) : ScratchProfile.DefaultPatterns
        )
        {
            Parallel = ReadParallel(scratch, place),
            Setup = ReadMap<IReadOnlyList<string>>(scratch, "setup", place, (scene, steps) => ReadSetup(scene, steps, place)),
        };
    }

    /// <summary>The section's <c>parallel</c>, a whole number of scenes at once from 1 to 4; 1 when the key is absent.</summary>
    private static int ReadParallel(Dictionary<string, JsonElement> keys, Place place)
    {
        if (!keys.TryGetValue("parallel", out JsonElement value))
        {
            return ScratchProfile.DefaultParallel;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parallel) && ScratchProfile.IsParallel(parallel)
            ? parallel
            : throw Refused(
                place,
                $"\"parallel\" must be a whole number from 1 to {ScratchProfile.MaxParallel}, not "
                    + (value.ValueKind == JsonValueKind.Number ? value.GetRawText() : Describe(value.ValueKind))
            );
    }

    /// <summary>
    /// The <c>processes</c> section: each process's command, folder, environment, ready line, description and prep command,
    /// keyed by its name in file order; empty when the key is absent.
    /// </summary>
    private static Dictionary<string, ProcessProfile> ReadProcesses(Dictionary<string, JsonElement> keys, Place top, string projectDir)
    {
        Dictionary<string, ProcessProfile> processes = new(StringComparer.Ordinal);
        if (!keys.TryGetValue(ProcessesKey, out JsonElement value))
        {
            return processes;
        }

        foreach (JsonProperty entry in Expect(value, JsonValueKind.Object, ProcessesKey, top).EnumerateObject())
        {
            Place place = new(top.Path, $"{ProcessesKey}.{entry.Name}");
            Dictionary<string, JsonElement> process = Keys(entry.Value, ProcessKeys, place);
            ProcessProfile read = new(
                ReadCommand(process, place, "command"),
                ReadCwd(process, place, projectDir),
                ReadEnvironment(process, place, entry.Name),
                ReadReady(process, place),
                ReadProcessDescription(process, place),
                ReadPrep(process, place)
            );
            if (!processes.TryAdd(entry.Name, read))
            {
                throw Refused(top, $"the process \"{entry.Name}\" appears twice in \"{ProcessesKey}\"; keep one");
            }
        }

        return processes;
    }

    /// <summary>A process's <c>command</c> or <c>prep</c>: a non-empty array of non-empty strings, the program and then its arguments.</summary>
    private static string[] ReadCommand(Dictionary<string, JsonElement> keys, Place place, string key)
    {
        if (!keys.TryGetValue(key, out JsonElement value))
        {
            throw Refused(place, $"\"{key}\" is missing; a process needs the program and then its arguments. Add it");
        }

        string shape = $"\"{key}\" must be a non-empty array of non-empty strings, the program and then its arguments";
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Refused(place, $"{shape}; it is {Describe(value.ValueKind)}");
        }

        List<string> command = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            string element =
                item.ValueKind == JsonValueKind.String ? item.GetString()! : throw Refused(place, $"{shape}; it holds {Describe(item.ValueKind)}");
            command.Add(element.Length > 0 ? element : throw Refused(place, $"{shape}; its item {command.Count + 1} is an empty string"));
        }

        return command.Count > 0 ? [.. command] : throw Refused(place, $"{shape}; it is empty");
    }

    /// <summary>
    /// A process's <c>prep</c>: the command run once before the process starts, in the shape <c>command</c> takes; null when
    /// the key is absent, so a process that declares none behaves as it did before the key existed.
    /// </summary>
    private static string[]? ReadPrep(Dictionary<string, JsonElement> keys, Place place) =>
        keys.ContainsKey(PrepKey) ? ReadCommand(keys, new Place(place.Path, $"{place.Where}.{PrepKey}"), PrepKey) : null;

    /// <summary>
    /// A process's <c>cwd</c>: the folder it runs in, resolved against the project folder; null when the key is absent, which
    /// runs it in the project folder.
    /// </summary>
    private static string? ReadCwd(Dictionary<string, JsonElement> keys, Place place, string projectDir)
    {
        if (!keys.TryGetValue("cwd", out JsonElement value))
        {
            return null;
        }

        string text =
            value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } given
                ? given
                : throw Refused(place, "\"cwd\" must be a non-empty string, the folder the process runs in, relative to the project folder");
        string resolved = Path.GetFullPath(Path.Combine(projectDir, text));
        string folder = ProjectPaths.Normalise(projectDir);
        return Path.IsPathRooted(text) || !IsInsideFolder(folder, resolved)
            ? throw Refused(place, $"cwd \"{text}\" leaves the project folder; it must be the folder or a folder inside it")
            : resolved;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or a folder inside it.</summary>
    internal static bool IsInsideFolder(string folder, string path)
    {
        string relative = Path.GetRelativePath(folder, path);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith("../", StringComparison.Ordinal);
    }

    /// <summary>A process's <c>env</c>: the variables set for it over the server's own environment; empty when absent.</summary>
    private static Dictionary<string, string> ReadEnvironment(Dictionary<string, JsonElement> keys, Place place, string process)
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        if (!keys.TryGetValue("env", out JsonElement value))
        {
            return environment;
        }

        foreach (JsonProperty entry in Expect(value, JsonValueKind.Object, "env", place).EnumerateObject())
        {
            string read =
                entry.Value.ValueKind == JsonValueKind.String
                    ? entry.Value.GetString()!
                    : throw Refused(place, $"\"env\" of \"{process}\" must hold strings; \"{entry.Name}\" is {Describe(entry.Value.ValueKind)}");
            if (!environment.TryAdd(entry.Name, read))
            {
                throw Refused(place, $"the variable \"{entry.Name}\" appears twice in \"env\"; keep one");
            }
        }

        return environment;
    }

    /// <summary>
    /// A process's <c>ready</c>: the line it must print before run_process returns, as a compiled pattern, and the timeout it
    /// has, 1 to 600000 ms, 60000 when the key sets none; null when the key is absent.
    /// </summary>
    private static ReadyProfile? ReadReady(Dictionary<string, JsonElement> keys, Place place)
    {
        if (!keys.TryGetValue("ready", out JsonElement value))
        {
            return null;
        }

        Place ready = new(place.Path, $"{place.Where}.ready");
        Dictionary<string, JsonElement> readyKeys = Keys(value, ReadyKeys, ready);
        if (!readyKeys.TryGetValue("output", out JsonElement output))
        {
            throw Refused(ready, "\"output\" is missing; \"ready\" needs the regular expression a printed line is matched against");
        }

        string text =
            output.ValueKind == JsonValueKind.String && output.GetString() is { Length: > 0 } pattern
                ? pattern
                : throw Refused(ready, "\"output\" must be a non-empty string, the regular expression a printed line is matched against");
        return new ReadyProfile(text, ReadyTimeout(readyKeys, ready)) { Regex = CompileReady(place, text) };
    }

    /// <summary>A ready pattern compiled as written, with <see cref="ScratchProfile.Compile"/>'s options and timeout.</summary>
    private static Regex CompileReady(Place place, string text)
    {
        try
        {
            return ScratchProfile.Compile(text);
        }
        catch (ArgumentException e)
        {
            Place output = new(place.Path, $"{place.Where}.ready.output");
            string message = $"the pattern \"{text}\" in \"ready.output\" is not a valid .NET regular expression: {e.Message} Fix or remove it";
            throw Refused(output, message);
        }
    }

    /// <summary>A ready timeout: a whole number of milliseconds from 1 to 600000; 60000 when the key is absent.</summary>
    private static int ReadyTimeout(Dictionary<string, JsonElement> keys, Place place)
    {
        if (!keys.TryGetValue("timeoutMs", out JsonElement value))
        {
            return ReadyProfile.DefaultTimeoutMs;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int timeoutMs) && ReadyProfile.IsTimeout(timeoutMs)
            ? timeoutMs
            : throw Refused(place, $"\"timeoutMs\" must be a whole number from 1 to {ReadyProfile.MaxTimeoutMs}; got {Shown(value)}");
    }

    /// <summary>A process's description, a non-empty string saying what it is; null when the key is absent.</summary>
    private static string? ReadProcessDescription(Dictionary<string, JsonElement> keys, Place place)
    {
        string? description = ReadString(keys, "description", place);
        return description is null or { Length: > 0 }
            ? description
            : throw Refused(place, "\"description\" must be a non-empty string, what the process is");
    }

    /// <summary>An object of scene names to values, each read by <paramref name="read"/>; empty when the key is absent.</summary>
    private static Dictionary<string, T> ReadMap<T>(Dictionary<string, JsonElement> keys, string key, Place place, Func<string, JsonElement, T> read)
    {
        Dictionary<string, T> map = new(StringComparer.Ordinal);
        if (!keys.TryGetValue(key, out JsonElement value))
        {
            return map;
        }

        foreach (JsonProperty entry in Expect(value, JsonValueKind.Object, key, place).EnumerateObject())
        {
            if (!map.TryAdd(entry.Name, read(entry.Name, entry.Value)))
            {
                throw Refused(place, $"the scene \"{entry.Name}\" appears twice in \"{key}\"; keep one");
            }
        }

        return map;
    }

    /// <summary>
    /// One scene's pace: a bare number of seconds, or an object of the seconds, the reason for them and, optionally, the paces of
    /// some of its steps.
    /// </summary>
    private static ScratchPace ReadPace(string scene, JsonElement value, Place place) =>
        ReadNumberOrObject(value, place, $"\"pace\" of \"{scene}\"", pace => ReadObjectPace(scene, pace, place));

    /// <summary>
    /// A pace written as a bare number of seconds, or as an object <paramref name="readObject"/> reads; anything else is refused
    /// naming <paramref name="label"/>.
    /// </summary>
    private static ScratchPace ReadNumberOrObject(JsonElement value, Place place, string label, Func<JsonElement, ScratchPace> readObject)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return new ScratchPace(Seconds(value, place, label), null);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Refused(
                place,
                $"{label} must be a number of seconds above 0 and at most {ScratchProfile.MaxPaceSeconds}, "
                    + $"or an object {{\"seconds\", \"reason\"}}, not {Describe(value.ValueKind)}"
            );
        }

        return readObject(value);
    }

    /// <summary>
    /// A scene's object pace: "seconds" and "reason", in either order, the reason why the scene needs it, the optional
    /// "steps", the paces of single steps, and the optional "timeScale", which can only be 1 and keeps the scene at normal speed.
    /// </summary>
    private static ScratchPace ReadObjectPace(string scene, JsonElement value, Place place)
    {
        Place pace = new(place.Path, $"scratch, pace of \"{scene}\"");
        Dictionary<string, JsonElement> keys = Keys(value, PaceKeys, pace);
        bool normalSpeed = keys.TryGetValue(TimeScaleKey, out JsonElement scale);
        if (normalSpeed)
        {
            CheckNormalSpeed(scale, pace);
        }

        ScratchPace read = ReadSecondsAndReason(keys, pace) with { NormalSpeed = normalSpeed };
        return keys.TryGetValue(StepsKey, out JsonElement steps) ? read with { Steps = ReadStepPaces(scene, steps, pace) } : read;
    }

    /// <summary>Refuses a pace's "timeScale" other than 1, the only value it takes.</summary>
    private static void CheckNormalSpeed(JsonElement value, Place pace)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double scale) || scale != 1)
        {
            throw Refused(
                pace,
                $"\"{TimeScaleKey}\" is {Shown(value)}; a pace entry's \"timeScale\" can only be 1, which keeps the scene at normal speed. "
                    + "Remove it, or set it to 1"
            );
        }
    }

    /// <summary>
    /// A scene's "steps": step keys, each a step's index or its name, to paces, each a bare number or an object of the seconds and
    /// the reason; which step a key names is only known once the scene has started.
    /// </summary>
    private static Dictionary<string, ScratchPace> ReadStepPaces(string scene, JsonElement value, Place pace)
    {
        Dictionary<string, ScratchPace> steps = new(StringComparer.Ordinal);
        foreach (JsonProperty entry in Expect(value, JsonValueKind.Object, StepsKey, pace).EnumerateObject())
        {
            Place step = new(pace.Path, $"{pace.Where}, step \"{entry.Name}\"");
            ScratchPace read = ReadNumberOrObject(
                entry.Value,
                pace,
                $"\"{StepsKey}\" entry \"{entry.Name}\"",
                stepPace => ReadSecondsAndReason(Keys(stepPace, StepPaceKeys, step), step)
            );
            if (!steps.TryAdd(entry.Name, read))
            {
                throw Refused(pace, $"the step key \"{entry.Name}\" appears twice in \"{StepsKey}\"; keep one");
            }
        }

        return steps;
    }

    /// <summary>An object pace's "seconds" and "reason", both required.</summary>
    private static ScratchPace ReadSecondsAndReason(Dictionary<string, JsonElement> keys, Place pace)
    {
        if (!keys.TryGetValue("seconds", out JsonElement seconds))
        {
            throw Refused(pace, MissingPaceKey("seconds"));
        }

        if (!keys.TryGetValue("reason", out JsonElement reason))
        {
            throw Refused(pace, MissingPaceKey("reason"));
        }

        return new ScratchPace(Seconds(seconds, pace, "\"seconds\""), ReadPaceReason(reason, pace));
    }

    private static string MissingPaceKey(string key) =>
        $"\"{key}\" is missing; an object pace needs both \"seconds\" and \"reason\". Add it, or write the pace as a bare number of "
        + "seconds, which cannot carry a timeScale";

    private static string ReadPaceReason(JsonElement value, Place place) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } reason
            ? reason
            : throw Refused(place, "\"reason\" must be a non-empty string, why the scene needs this pace");

    /// <summary>A JSON number as a pace in seconds; out of range or not a number is refused naming <paramref name="key"/>.</summary>
    private static double Seconds(JsonElement value, Place place, string key) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double seconds) && ScratchProfile.IsPace(seconds)
            ? seconds
            : throw Refused(place, $"{key} must be a number of seconds above 0 and at most {ScratchProfile.MaxPaceSeconds}, not {Shown(value)}");

    private static string Shown(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetRawText() : Describe(value.ValueKind);

    private static string ReadReason(string scene, JsonElement value, Place place) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } reason
            ? reason
            : throw Refused(place, $"\"known\" of \"{scene}\" must be a non-empty string, the reason the scene is known to fail");

    /// <summary>One scene's ceiling: a number of seconds above 0 and at most <see cref="ScratchProfile.MaxCeilingSeconds"/>.</summary>
    private static double ReadCeiling(string scene, JsonElement value, Place place) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double seconds) && ScratchProfile.IsCeiling(seconds)
            ? seconds
            : throw Refused(
                place,
                $"scratch.ceilings.{scene} must be a number of seconds above 0 and at most {ScratchProfile.MaxCeilingSeconds}; "
                    + $"got {Shown(value)}"
            );

    /// <summary>
    /// One scene's declared setup steps: a non-empty array of step keys, each an index written plainly or a step's exact name,
    /// which step each names only known once the scene has started.
    /// </summary>
    private static List<string> ReadSetup(string scene, JsonElement value, Place place)
    {
        string label = $"scratch.setup.{scene}";
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Refused(place, $"{label} must be an array of setup steps, not {Describe(value.ValueKind)}; {SetupStepShape}");
        }

        List<string> steps = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            steps.Add(SetupKey(item, label, steps.Count + 1, place));
        }

        return steps.Count > 0 ? steps : throw Refused(place, $"{label} must name at least one step; it is empty");
    }

    /// <summary>One setup entry as it is kept: an index written plainly, or a step's exact name.</summary>
    private static string SetupKey(JsonElement value, string label, int item, Place place) =>
        value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out double index) && index >= 0 && index <= int.MaxValue && index == Math.Floor(index) => (
                (int)index
            ).ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String when value.GetString() is { Length: > 0 } name => name,
            JsonValueKind.String => throw Refused(place, $"{label} item {item} is an empty string; {SetupStepShape}"),
            _ => throw Refused(place, $"{label} item {item} is {Shown(value)}; {SetupStepShape}"),
        };

    /// <summary>The section's <c>patterns</c>, each compiled as written: case-sensitive, anchored only where it anchors itself.</summary>
    private static List<ScratchPattern> ReadPatterns(Dictionary<string, JsonElement> keys, Place place)
    {
        List<ScratchPattern> patterns = [];
        int item = 0;
        foreach (JsonElement value in Expect(keys["patterns"], JsonValueKind.Array, "patterns", place).EnumerateArray())
        {
            patterns.Add(ReadPattern(value, ++item, place));
        }

        return patterns;
    }

    /// <summary>One <c>patterns</c> item: a bare string, or an object of the pattern and why a line matching it fails a step.</summary>
    private static ScratchPattern ReadPattern(JsonElement value, int item, Place place)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return Pattern(place, value.GetString()!, reason: null);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Refused(
                place,
                $"\"patterns\" must be an array of regular expressions, each a string or an object {{\"pattern\", \"reason\"}}; "
                    + $"item {item} is {Describe(value.ValueKind)}"
            );
        }

        Place slot = new(place.Path, $"scratch, pattern {item}");
        Dictionary<string, JsonElement> keys = Keys(value, PatternKeys, slot);
        foreach (string required in PatternKeys)
        {
            if (!keys.ContainsKey(required))
            {
                throw Refused(slot, MissingPatternKey(required));
            }
        }

        return Pattern(place, ReadString(keys, "pattern", slot)!, ReadPatternReason(keys, slot));
    }

    private static string MissingPatternKey(string key) =>
        $"\"{key}\" is missing; an object pattern needs both \"pattern\" and \"reason\". Add it, or write the pattern as a bare string";

    /// <summary>An object pattern's reason, a non-empty string saying why a line matching the pattern fails a step.</summary>
    private static string ReadPatternReason(Dictionary<string, JsonElement> keys, Place slot)
    {
        JsonElement reason = keys["reason"];
        return reason.ValueKind == JsonValueKind.String && reason.GetString() is { Length: > 0 } text
            ? text
            : throw Refused(slot, "\"reason\" must be a non-empty string, why a line matching it fails a step");
    }

    /// <summary>A pattern compiled as written, with the reason from an object item; null for a bare string.</summary>
    private static ScratchPattern Pattern(Place place, string text, string? reason)
    {
        try
        {
            return new ScratchPattern(ScratchProfile.Compile(text), reason);
        }
        catch (ArgumentException e)
        {
            string message = $"the pattern \"{text}\" in \"patterns\" is not a valid .NET regular expression: {e.Message} Fix or remove it";
            throw Refused(place, message);
        }
    }

    private static string? ReadResolution(Dictionary<string, JsonElement> keys, Place place)
    {
        string? resolution = ReadString(keys, "resolution", place);
        return resolution is null || ResolutionPattern().IsMatch(resolution)
            ? resolution
            : throw Refused(
                place,
                $"\"resolution\" is \"{resolution}\"; it must be WIDTHxHEIGHT in pixels, with a lowercase x and no spaces, " + "e.g. \"1280x720\""
            );
    }

    /// <summary>A preset's description, a non-empty string saying what the preset is for; null when the key is absent.</summary>
    private static string? ReadDescription(Dictionary<string, JsonElement> keys, Place place)
    {
        string? description = ReadString(keys, "description", place);
        return description is null or { Length: > 0 }
            ? description
            : throw Refused(place, "\"description\" must be a non-empty string, what the preset is for");
    }

    /// <summary>A preset's session, checked by the same rule as run_project's options.session.</summary>
    private static string? ReadSession(Dictionary<string, JsonElement> keys, Place place)
    {
        string? session = ReadString(keys, "session", place);
        try
        {
            // Only NameFor's name check applies: the folder it takes is unused when a session is given.
            return session is null ? null : SessionRegistry.NameFor(session, Path.GetDirectoryName(place.Path) ?? place.Path);
        }
        catch (SessionException e)
        {
            throw new McpException($"{place.Path} ({place.Where}): \"session\" is refused: {e.Message} Rename it.", e);
        }
    }

    private static JsonElement Expect(JsonElement value, JsonValueKind kind, string key, Place place) =>
        value.ValueKind == kind ? value : throw Refused(place, $"\"{key}\" must be {Describe(kind)}, not {Describe(value.ValueKind)}");

    private static string Describe(JsonValueKind kind) =>
        kind switch
        {
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "true or false",
            JsonValueKind.Array => "an array",
            JsonValueKind.Object => "an object",
            _ => "null",
        };

    private static McpException Refused(Place place, string problem) => new($"{place.Path} ({place.Where}): {problem}.");

    [GeneratedRegex(@"\A[1-9][0-9]*x[1-9][0-9]*\z")]
    internal static partial Regex ResolutionPattern();

    /// <summary>Where in the file a value is: the file's path, and "top level", "preset \"name\"" or "scratch".</summary>
    private sealed record Place(string Path, string Where);
}

/// <summary>
/// godot-mcp.json's <c>scratch</c> section: the folder whose scenes run_scratches plays when given none, the user arguments a
/// scratch run starts with in place of the top-level ones (null when the section sets none), the engine arguments each scene's
/// game launches with before --, after the runner's own --headless and --time-scale (null when the section sets none), each
/// scene's pace in seconds and why, each scene's own ceiling of load-adjusted time in seconds, each scene's declared setup steps,
/// the scenes known to fail with the reason, the patterns a step's output lines fail it by, and how many scenes play at once.
/// </summary>
internal sealed record ScratchProfile(
    string? Folder,
    IReadOnlyList<string>? UserArgs,
    IReadOnlyList<string>? EngineArgs,
    IReadOnlyDictionary<string, ScratchPace> Pace,
    IReadOnlyDictionary<string, double> Ceilings,
    IReadOnlyDictionary<string, string> Known,
    IReadOnlyList<ScratchPattern> Patterns
)
{
    /// <summary>The longest pace, in seconds: a step's wait is a gameMs wait_for, at most 120000 ms.</summary>
    public const int MaxPaceSeconds = RuntimeTools.MaxWaitGameMs / 1000;

    /// <summary>The longest ceiling a scene can be given, in seconds.</summary>
    public const int MaxCeilingSeconds = 3600;

    /// <summary>How many scenes play at once when neither options.parallel nor the section sets it.</summary>
    public const int DefaultParallel = 1;

    /// <summary>The most scenes that play at once.</summary>
    public const int MaxParallel = 4;

    /// <summary>How many scenes play at once, from 1 to <see cref="MaxParallel"/>.</summary>
    public int Parallel { get; init; } = DefaultParallel;

    /// <summary>
    /// Each scene's declared setup steps, by scene name, keyed by a step's index written plainly or its exact name: the steps a
    /// partial run adds to the ones options.steps selects. Which step a key names is resolved only on a partial run, so a key
    /// naming no step of its scene turns that scene red only then; empty when the section declares none.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Setup { get; init; } = ReadOnlyDictionary<string, IReadOnlyList<string>>.Empty;

    /// <summary>The patterns of a project that sets none: Godot's own error lines and its leak warning at exit.</summary>
    public static readonly IReadOnlyList<ScratchPattern> DefaultPatterns =
    [
        new ScratchPattern(Compile(@"^SCRIPT ERROR|^ERROR:|ObjectDB instances? (was|were) leaked"), null),
    ];

    /// <summary>The section of a project that has none: no folder, and the default patterns.</summary>
    public static readonly ScratchProfile None = new(
        null,
        null,
        null,
        new Dictionary<string, ScratchPace>(),
        new Dictionary<string, double>(),
        new Dictionary<string, string>(),
        DefaultPatterns
    );

    /// <summary>Whether <paramref name="seconds"/> is a pace: above 0 and at most <see cref="MaxPaceSeconds"/>.</summary>
    public static bool IsPace(double seconds) => seconds is > 0 and <= MaxPaceSeconds;

    /// <summary>Whether <paramref name="seconds"/> is a ceiling: above 0 and at most <see cref="MaxCeilingSeconds"/>.</summary>
    public static bool IsCeiling(double seconds) => seconds is > 0 and <= MaxCeilingSeconds;

    /// <summary>Whether <paramref name="parallel"/> is a number of scenes at once: from 1 to <see cref="MaxParallel"/>.</summary>
    public static bool IsParallel(int parallel) => parallel is >= 1 and <= MaxParallel;

    /// <summary>A pattern as the section takes it: case-sensitive, culture-invariant, with a one-second match timeout.</summary>
    /// <exception cref="ArgumentException">The pattern is not a valid regular expression.</exception>
    public static Regex Compile(string pattern) => new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

/// <summary>
/// A scene's or a step's pace in seconds and why it was set; the reason is null when the profile gave a bare number. A scene's
/// pace may carry the paces of some of its steps, keyed by a step's index or its name.
/// </summary>
internal sealed record ScratchPace(double Seconds, string? Reason)
{
    /// <summary>The paces of single steps, keyed by a step's index or its name; empty when the profile sets none.</summary>
    public IReadOnlyDictionary<string, ScratchPace> Steps { get; init; } = ReadOnlyDictionary<string, ScratchPace>.Empty;

    /// <summary>Whether a scene's pace has "timeScale": 1, which keeps the scene's game at normal speed whatever options.timeScale says.</summary>
    public bool NormalSpeed { get; init; }
}

/// <summary>A pattern a step's output line fails it by, and why; the reason is null for a bare string.</summary>
internal sealed record ScratchPattern(Regex Regex, string? Reason);

/// <summary>
/// A program the project's <c>processes</c> section declares: the command and its arguments, the folder it runs in (null for
/// the project folder), the variables set over the server's environment, the line it may print before run_process returns
/// (null when it declares none), what it is, and the command run once before it starts (null when it declares none).
/// </summary>
internal sealed record ProcessProfile(
    string[] Command,
    string? Cwd,
    IReadOnlyDictionary<string, string> Env,
    ReadyProfile? Ready,
    string? Description,
    string[]? Prep = null
);

/// <summary>What a process declares it prints before run_process returns: the pattern's text, its compiled form and its timeout.</summary>
internal sealed record ReadyProfile(string Output, int TimeoutMs)
{
    /// <summary>The timeout a ready sets none for: 60000 ms.</summary>
    public const int DefaultTimeoutMs = 60000;

    /// <summary>The longest ready timeout: 600000 ms.</summary>
    public const int MaxTimeoutMs = 600000;

    /// <summary>Whether <paramref name="milliseconds"/> is a ready timeout: from 1 to <see cref="MaxTimeoutMs"/>.</summary>
    public static bool IsTimeout(int milliseconds) => milliseconds is >= 1 and <= MaxTimeoutMs;

    /// <summary>The pattern compiled as the section takes patterns: case-sensitive, culture-invariant, one-second match timeout.</summary>
    public required Regex Regex { get; init; }
}
