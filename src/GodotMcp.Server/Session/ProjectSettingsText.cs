using System.Text;
using System.Text.Json;
using ModelContextProtocol;

namespace GodotMcp.Server.Session;

/// <summary>
/// A run's project settings as the text that carries them: the block written at the end of the bridge's override.cfg under
/// <see cref="BlockMarker"/>, one section per setting name's first segment, and the summary a refusal names them by. Godot reads
/// override.cfg right after project.godot and a setting there overwrites the file's (4.7.2 <c>project_settings.cpp</c> L773-781,
/// L985-989), and it stops reading at the first parse error (L978), so the block goes last. Value text is what
/// <c>core/variant/variant_parser.cpp</c> takes: a string takes <c>\\ \" \n \r \t</c> (L277-312), and a number keeps its JSON
/// spelling (L425-470).
/// </summary>
internal static class ProjectSettingsText
{
    /// <summary>The line the settings block starts with, after which every line is a section or a setting.</summary>
    public const string BlockMarker = "; godot-mcp project settings";

    /// <summary>The section every <c>autoload/*</c> setting belongs to, which the bridge's own autoload entry shares.</summary>
    private const string AutoloadPrefix = "autoload/";

    /// <summary>The settings the bridge's own override.cfg lines set, under their sections.</summary>
    private static readonly string[] BridgeOwnedSettings =
    [
        "input_devices/" + OverrideFile.IgnoreJoypadOnUnfocusedSetting,
        "display/" + OverrideFile.NoFocusSetting,
        "display/" + OverrideFile.InitialPositionTypeSetting,
        "display/" + OverrideFile.InitialPositionSetting,
        "display/" + OverrideFile.WindowModeSetting,
    ];

    /// <summary>What a refusal names as the bridge's own settings: <see cref="AutoloadPrefix"/> as a prefix, then the ones above.</summary>
    private static readonly string[] BridgeOwnedNames = [AutoloadPrefix + "*", .. BridgeOwnedSettings];

    /// <summary>
    /// The block a run's merged settings are written as: <see cref="BlockMarker"/> and then, per section in ordinal order, a
    /// section header and its settings in ordinal key order; empty when there are none. Every entry is checked first, in ordinal
    /// order of name.
    /// </summary>
    /// <param name="settings">The settings by their <c>section/key</c> names, each name once.</param>
    /// <param name="source">Where the settings came from, which a refusal names.</param>
    /// <param name="refuse">How that place's refusal is raised, so each place's reads as it does.</param>
    /// <exception cref="McpException">A name is not a <c>section/key</c> name the bridge does not set, or a value cannot be written.</exception>
    public static string Block(IEnumerable<KeyValuePair<string, JsonElement>> settings, string source, Func<string, McpException> refuse)
    {
        List<Rendered> rendered = [.. settings.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => Render(entry, source, refuse))];
        if (rendered.Count == 0)
        {
            return "";
        }

        StringBuilder text = new(BlockMarker);
        text.Append('\n');
        foreach (IGrouping<string, Rendered> section in Sections(rendered))
        {
            text.Append("\n[").Append(section.Key).Append("]\n\n");
            foreach (Rendered setting in section.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                text.Append(setting.Key).Append('=').Append(setting.Value).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Every setting of a block as <c>section/key=value</c>, in block order, joined with <c>, </c>; <c>none</c> for an empty block.
    /// The block is parsed here, so one another server wrote summarises the same way.
    /// </summary>
    public static string Summary(string block)
    {
        if (block.Length == 0)
        {
            return "none";
        }

        List<string> settings = [];
        string section = "";
        foreach (string line in block.Split('\n'))
        {
            if (line.Length == 0 || line == BlockMarker)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            settings.Add($"{section}/{line}");
        }

        return string.Join(", ", settings);
    }

    /// <summary>The rendered settings grouped by section, the sections in ordinal order.</summary>
    private static IEnumerable<IGrouping<string, Rendered>> Sections(List<Rendered> rendered) =>
        rendered.GroupBy(setting => setting.Section, StringComparer.Ordinal).OrderBy(section => section.Key, StringComparer.Ordinal);

    /// <summary>One setting checked and split into its section, its key and the value text Godot reads.</summary>
    private static Rendered Render(KeyValuePair<string, JsonElement> entry, string source, Func<string, McpException> refuse)
    {
        CheckName(entry.Key, source, refuse);
        int slash = entry.Key.IndexOf('/', StringComparison.Ordinal);
        return new Rendered(entry.Key[..slash], entry.Key[(slash + 1)..], Value(entry, source, refuse));
    }

    /// <summary>
    /// Refuses a name that is not a plain-ASCII <c>section/key</c> name, or that names a setting the bridge's override.cfg sets,
    /// a feature-tagged name of one included.
    /// </summary>
    private static void CheckName(string name, string source, Func<string, McpException> refuse)
    {
        if (name.Length == 0 || name.IndexOf('/', StringComparison.Ordinal) <= 0 || name.EndsWith('/') || HoldsARefusedCharacter(name))
        {
            throw refuse($"{source} \"{name}\" must be a \"section/key\" project setting name, e.g. \"rendering/renderer/rendering_method\".");
        }

        if (IsBridgeOwned(name))
        {
            throw refuse($"{source} cannot set \"{name}\": godot-mcp's override.cfg sets it for the bridge ({string.Join(", ", BridgeOwnedNames)}).");
        }
    }

    /// <summary>
    /// Whether a name holds a character override.cfg's parser would misread or rewrite: one outside printable ASCII, a space, a
    /// backslash, or one of <c>= [ ] ; "</c>. The key reader drops spaces and keeps bytes above U+007F undecoded, a section tag
    /// strips its edges and escapes after a backslash, so such a name lands under another one (<c>variant_parser.cpp</c> L66-85,
    /// L1771-1782, L1808, L1933-1949; <c>project_settings.cpp</c> L991-992).
    /// </summary>
    private static bool HoldsARefusedCharacter(string name) =>
        name.Any(character => character is < '!' or > '~' || character is '=' or '[' or ']' or ';' or '"' or '\\');

    /// <summary>
    /// Whether the bridge writes the setting itself, so a run may not set it: an <c>autoload/</c> name, an owned setting, or a
    /// feature override of one, which Godot prefers over the setting it belongs to (<c>project_settings.cpp</c> L324-339, L415-431).
    /// </summary>
    private static bool IsBridgeOwned(string name) =>
        name.StartsWith(AutoloadPrefix, StringComparison.Ordinal)
        || BridgeOwnedSettings.Any(owned => name.Equals(owned, StringComparison.Ordinal) || name.StartsWith(owned + ".", StringComparison.Ordinal));

    /// <summary>The value as override.cfg carries it, or a refusal naming why it cannot.</summary>
    private static string Value(KeyValuePair<string, JsonElement> entry, string source, Func<string, McpException> refuse)
    {
        return entry.Value.ValueKind switch
        {
            JsonValueKind.String => Text(entry, source, refuse),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => entry.Value.GetRawText(),
            JsonValueKind.Object when RawText(entry.Value) is { } raw => Raw(entry.Key, raw, source, refuse),
            _ => throw refuse($"{source} \"{entry.Key}\" must be a string, a number or a boolean, or {{\"raw\": \"<Godot value text>\"}}."),
        };
    }

    /// <summary>A JSON string as a quoted Godot string, its own characters escaped.</summary>
    private static string Text(KeyValuePair<string, JsonElement> entry, string source, Func<string, McpException> refuse)
    {
        string text = entry.Value.GetString()!;
        if (text.Any(character => character < ' ' && character is not ('\n' or '\r' or '\t')))
        {
            throw refuse(LineBreakRefusal(source, entry.Key));
        }

        return "\"" + Escape(text) + "\"";
    }

    /// <summary>A <c>{"raw": "..."}</c> value, which must be one line.</summary>
    private static string Raw(string name, string raw, string source, Func<string, McpException> refuse)
    {
        if (raw.Length == 0 || raw.Any(character => character < ' '))
        {
            throw refuse(LineBreakRefusal(source, name));
        }

        return raw;
    }

    private static string LineBreakRefusal(string source, string name) =>
        $"{source} \"{name}\" holds a line break or control character; override.cfg takes one line per setting.";

    /// <summary>The Godot text of an object that is exactly <c>{"raw": "&lt;text&gt;"}</c>; null for any other object.</summary>
    private static string? RawText(JsonElement value)
    {
        JsonProperty? single = null;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (single is not null)
            {
                return null;
            }

            single = property;
        }

        return single is { Name: "raw", Value.ValueKind: JsonValueKind.String } raw ? raw.Value.GetString() : null;
    }

    /// <summary>The text with Godot's own escapes: <c>\\ \" \n \r \t</c>.</summary>
    private static string Escape(string text)
    {
        StringBuilder escaped = new(text.Length);
        foreach (char character in text)
        {
            switch (character)
            {
                case '\\':
                    escaped.Append("\\\\");
                    break;
                case '"':
                    escaped.Append("\\\"");
                    break;
                case '\n':
                    escaped.Append("\\n");
                    break;
                case '\r':
                    escaped.Append("\\r");
                    break;
                case '\t':
                    escaped.Append("\\t");
                    break;
                default:
                    escaped.Append(character);
                    break;
            }
        }

        return escaped.ToString();
    }

    /// <summary>One rendered setting: its section, its key within that section, and the value as override.cfg takes it.</summary>
    private sealed record Rendered(string Section, string Key, string Value);
}
