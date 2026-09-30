using System.Text.Json;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>What run_project launches once the profile is merged: the request, and the session name (null for the default).</summary>
internal sealed record ProfileLaunch(LaunchRequest Request, string? Session);

/// <summary>
/// The values the top level of godot-mcp.json or one of its presets sets: null, or empty for the argument lists, where it
/// sets none. <see cref="Session"/> is only ever set by a preset.
/// </summary>
internal sealed record ProfileValues(
    string? Scene,
    IReadOnlyList<string> UserArgs,
    IReadOnlyList<string> EngineArgs,
    string? Resolution,
    bool? Quiet,
    string? Session
)
{
    /// <summary>Values that set nothing.</summary>
    public static readonly ProfileValues None = new(null, [], [], null, null, null);
}

/// <summary>
/// A project's launch profile, the optional <c>godot-mcp.json</c> beside <c>project.godot</c>: top-level defaults for every
/// run_project on the folder, and named presets that layer over them. Parsing is strict: an unknown key, a value of the wrong
/// type or a malformed resolution refuses the whole file.
/// </summary>
internal sealed partial class ProjectProfile
{
    /// <summary>The profile's file name, in the project folder.</summary>
    public const string FileName = "godot-mcp.json";

    private const string TopLevel = "top level";
    private static readonly string[] TopLevelKeys = ["scene", "userArgs", "engineArgs", "resolution", "quiet", "presets"];
    private static readonly string[] PresetKeys = ["scene", "userArgs", "engineArgs", "resolution", "quiet", "session"];

    private readonly ProfileValues _defaults;
    private readonly IReadOnlyDictionary<string, ProfileValues> _presets;

    private ProjectProfile(string projectDir, bool exists, ProfileValues defaults, IReadOnlyDictionary<string, ProfileValues> presets)
    {
        ProjectDir = projectDir;
        FilePath = Path.Combine(projectDir, FileName);
        Exists = exists;
        _defaults = defaults;
        _presets = presets;
    }

    /// <summary>The project folder the profile belongs to; merged requests launch it.</summary>
    public string ProjectDir { get; }

    /// <summary>Where the profile's file is, or would be.</summary>
    public string FilePath { get; }

    /// <summary>Whether the file exists.</summary>
    public bool Exists { get; }

    /// <summary>The profile of a folder with no godot-mcp.json: it sets nothing and has no presets.</summary>
    public static ProjectProfile Empty(string projectDir) => new(projectDir, false, ProfileValues.None, new Dictionary<string, ProfileValues>());

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
        ProfileValues defaults = ReadValues(keys, top, session: null);
        return new ProjectProfile(projectDir, true, defaults, ReadPresets(keys, top));
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

        string names = string.Join(", ", _presets.Keys.Order(StringComparer.Ordinal));
        return new McpException($"options.preset \"{name}\" is not in {FilePath}; its presets are {names}. Pass one of those.");
    }

    private static JsonDocument Parse(string text, string path)
    {
        try
        {
            return JsonDocument.Parse(text);
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
            ProfileValues preset = ReadValues(presetKeys, place, ReadSession(presetKeys, place));
            if (!presets.TryAdd(entry.Name, preset))
            {
                throw Refused(top, $"the preset \"{entry.Name}\" appears twice in \"presets\"; keep one");
            }
        }

        return presets;
    }

    private static ProfileValues ReadValues(Dictionary<string, JsonElement> keys, Place place, string? session) =>
        new(
            ReadString(keys, "scene", place),
            ReadStrings(keys, "userArgs", place),
            ReadStrings(keys, "engineArgs", place),
            ReadResolution(keys, place),
            ReadBool(keys, "quiet", place),
            session
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

    /// <summary>Where in the file a value is: the file's path, and "top level" or "preset \"name\"".</summary>
    private sealed record Place(string Path, string Where);
}
