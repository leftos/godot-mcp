using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using Godot;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Dotnet;

/// <summary>
/// Writes Godot's own values for <see cref="ValueWriter"/>: a node in the tree as <c>{"$node": path}</c>, any other
/// object as <c>{"$object": class, "id": instance id}</c> and a freed one as <c>"&lt;freed object&gt;"</c>; Godot structs
/// as <see cref="GodotStructs"/> writes them; <see cref="StringName"/> and <see cref="NodePath"/> as strings; a
/// <see cref="Variant"/> as the value it holds; and a Godot dictionary as an object keyed by its keys' text. Godot
/// arrays are collections the writer walks itself.
/// </summary>
internal sealed class GodotFormatter(int maxDepth) : IValueFormatter
{
    /// <summary>
    /// The levels left to the values this formatter hands back to the writer, a variant's content and a dictionary's
    /// entries: each is a walk of its own, which the outer walk's depth and cycle checks do not reach, so this budget
    /// bounds how deep a Godot collection holding itself can go.
    /// </summary>
    private int _budget = maxDepth;

    public bool TryFormat(object value, out JsonNode? json)
    {
        switch (value)
        {
            case Variant variant:
                json = Nested(variant.Obj);
                return true;
            case GodotObject godotObject:
                json = Object(godotObject);
                return true;
            case StringName or NodePath:
                json = JsonValue.Create(value.ToString());
                return true;
            default:
                json = IsGodotDictionary(value.GetType()) ? Map((IEnumerable)value) : GodotStructs.Format(value);
                return json is not null;
        }
    }

    private static JsonNode Object(GodotObject value)
    {
        if (!GodotObject.IsInstanceValid(value))
        {
            return JsonValue.Create("<freed object>");
        }
        if (value is Node node && node.IsInsideTree())
        {
            return new JsonObject { ["$node"] = node.GetPath().ToString() };
        }
        return new JsonObject { ["$object"] = value.GetClass(), ["id"] = value.GetInstanceId() };
    }

    private static bool IsGodotDictionary(Type type) =>
        type == typeof(Godot.Collections.Dictionary)
        || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Godot.Collections.Dictionary<,>));

    /// <summary>A dictionary's key-value pairs, whatever their types, as an object keyed by each key's text.</summary>
    private JsonObject Map(IEnumerable entries)
    {
        JsonObject map = [];
        foreach (object entry in entries)
        {
            Type pair = entry.GetType();
            object? key = pair.GetProperty("Key")!.GetValue(entry);
            map[KeyText(key)] = Nested(pair.GetProperty("Value")!.GetValue(entry));
        }
        return map;
    }

    private static string KeyText(object? key)
    {
        object? inner = key is Variant variant ? variant.Obj : key;
        return Convert.ToString(inner, CultureInfo.InvariantCulture) ?? "";
    }

    private JsonNode? Nested(object? inner)
    {
        int left = _budget;
        _budget = Math.Max(0, left - 1);
        try
        {
            return ValueWriter.Write(inner, this, left);
        }
        finally
        {
            _budget = left;
        }
    }
}

/// <summary>
/// Godot's structs in the shapes the bridge's JSON module (<c>bridge/godot_mcp_json.gd</c>) writes: vectors as
/// <c>{x, y[, z]}</c>, a colour as <c>{r, g, b, a}</c>, a rect as <c>{x, y, width, height}</c>, and any other Godot struct
/// as its text, as the module writes its <c>str()</c>.
/// </summary>
internal static class GodotStructs
{
    public static JsonNode? Format(object value) =>
        value switch
        {
            Vector2 v => new JsonObject { ["x"] = Real(v.X), ["y"] = Real(v.Y) },
            Vector2I v => new JsonObject { ["x"] = v.X, ["y"] = v.Y },
            Vector3 v => new JsonObject
            {
                ["x"] = Real(v.X),
                ["y"] = Real(v.Y),
                ["z"] = Real(v.Z),
            },
            Vector3I v => new JsonObject
            {
                ["x"] = v.X,
                ["y"] = v.Y,
                ["z"] = v.Z,
            },
            _ => FormatOther(value),
        };

    private static JsonNode? FormatOther(object value) =>
        value switch
        {
            Color c => new JsonObject
            {
                ["r"] = Real(c.R),
                ["g"] = Real(c.G),
                ["b"] = Real(c.B),
                ["a"] = Real(c.A),
            },
            Rect2 r => Rect(Real(r.Position.X), Real(r.Position.Y), Real(r.Size.X), Real(r.Size.Y)),
            Rect2I r => Rect(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y),
            _ when value.GetType().IsValueType && Targets.StopAtGodot(value.GetType()) => JsonValue.Create(value.ToString()),
            _ => null,
        };

    private static JsonObject Rect(JsonNode x, JsonNode y, JsonNode width, JsonNode height) =>
        new()
        {
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height,
        };

    /// <summary>A finite float as a number, and NaN or an infinity as its text, which JSON has no number for.</summary>
    private static JsonValue Real(float value) =>
        float.IsFinite(value) ? JsonValue.Create(value) : JsonValue.Create(value.ToString(CultureInfo.InvariantCulture));
}
