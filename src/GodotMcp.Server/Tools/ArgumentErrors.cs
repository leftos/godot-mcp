using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// Names the argument that does not fit a tool's input schema: a top-level argument the tool does not take, and, for a call
/// the SDK could not bind, the first value of the wrong kind or key an object does not take (in argument order, then depth
/// first), else the first required argument left out.
/// </summary>
internal static class ArgumentErrors
{
    private const int GivenLimit = 60;
    private static readonly JsonSerializerOptions Shown = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The server's call-tool filter: refuses an unknown argument before the tool runs, and names a binding failure.</summary>
    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (context, cancellationToken) => CallAsync(next, context, cancellationToken);

    /// <summary>The refusal for the first name the schema's properties do not list, compared exactly; null when all are listed.</summary>
    internal static string? UnknownArgument(string tool, JsonElement schema, IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            if (PropertyOf(schema, name, StringComparison.Ordinal) is null)
            {
                return $"{tool} has no argument '{name}'; {Takes(schema)}";
            }
        }

        return null;
    }

    /// <summary>
    /// The message for arguments that did not bind: a <see cref="JsonException"/>, or an <see cref="ArgumentException"/> for the
    /// parameter "arguments" (a required argument left out). Any other exception is the tool's own, and gets null.
    /// </summary>
    internal static string? Describe(string tool, JsonElement schema, IEnumerable<KeyValuePair<string, JsonElement>>? arguments, Exception exception)
    {
        if (exception is not (JsonException or ArgumentException { ParamName: "arguments" }))
        {
            return null;
        }

        return Walk(tool, schema, [.. arguments ?? []]) ?? Fallback(tool, exception);
    }

    private static async ValueTask<CallToolResult> CallAsync(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken
    )
    {
        if (context.MatchedPrimitive is not McpServerTool tool)
        {
            return await next(context, cancellationToken);
        }

        string name = tool.ProtocolTool.Name;
        JsonElement schema = tool.ProtocolTool.InputSchema;
        IDictionary<string, JsonElement>? arguments = context.Params?.Arguments;
        string? unknown = UnknownArgument(name, schema, arguments?.Keys ?? Enumerable.Empty<string>());
        if (unknown is not null)
        {
            throw new McpException(unknown);
        }

        try
        {
            return await next(context, cancellationToken);
        }
        catch (Exception e) when (Describe(name, schema, arguments, e) is { } message)
        {
            throw new McpException(message, e);
        }
    }

    private static string? Walk(string tool, JsonElement schema, List<KeyValuePair<string, JsonElement>> arguments)
    {
        string? unknown = UnknownArgument(tool, schema, arguments.Select(pair => pair.Key));
        if (unknown is not null)
        {
            return unknown;
        }

        foreach ((string name, JsonElement value) in arguments)
        {
            string? problem = Check(schema, PropertyOf(schema, name, StringComparison.Ordinal)!.Value, value, name);
            if (problem is not null)
            {
                return problem;
            }
        }

        return MissingArgument(tool, schema, arguments.Select(pair => pair.Key));
    }

    /// <summary>The refusal for the first argument the schema requires that names do not hold; null when none is missing.</summary>
    internal static string? MissingArgument(string tool, JsonElement schema, IEnumerable<string> names)
    {
        if (!TryGet(schema, "required", out JsonElement required) || required.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        HashSet<string> given = [.. names];
        string? missing = required
            .EnumerateArray()
            .Where(name => name.ValueKind == JsonValueKind.String)
            .Select(name => name.GetString()!)
            .FirstOrDefault(name => !given.Contains(name));
        return missing is null ? null : $"{tool} needs the argument '{missing}'.";
    }

    /// <summary>The first problem with a value against its schema, depth first; null when it fits.</summary>
    private static string? Check(JsonElement root, JsonElement schema, JsonElement value, string path)
    {
        JsonElement resolved = Resolve(root, schema);
        if (!Fits(resolved, value))
        {
            return Mismatch(schema, resolved, value, path);
        }

        return value.ValueKind switch
        {
            JsonValueKind.Object => CheckObject(root, resolved, value, path),
            JsonValueKind.Array => CheckArray(root, resolved, value, path),
            _ => null,
        };
    }

    private static string? CheckObject(JsonElement root, JsonElement schema, JsonElement value, string path)
    {
        foreach (JsonProperty member in value.EnumerateObject())
        {
            JsonElement? memberSchema = PropertyOf(schema, member.Name, StringComparison.OrdinalIgnoreCase) ?? AdditionalSchema(schema);
            if (memberSchema is null && IsClosed(schema))
            {
                return $"{path} has no '{member.Name}'; {Takes(schema)}";
            }

            string? problem = memberSchema is null ? null : Check(root, memberSchema.Value, member.Value, $"{path}.{member.Name}");
            if (problem is not null)
            {
                return problem;
            }
        }

        return null;
    }

    private static string? CheckArray(JsonElement root, JsonElement schema, JsonElement value, string path)
    {
        if (!TryGet(schema, "items", out JsonElement items))
        {
            return null;
        }

        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            string? problem = Check(root, items, item, $"{path}[{index}]");
            if (problem is not null)
            {
                return problem;
            }

            index++;
        }

        return null;
    }

    /// <summary>A schema's <c>$ref</c> into the tool's own schema followed; an unresolvable one accepts anything.</summary>
    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        if (!TryGet(schema, "$ref", out JsonElement reference) || reference.ValueKind != JsonValueKind.String)
        {
            return schema;
        }

        string pointer = reference.GetString()!;
        JsonElement at = pointer.StartsWith('#') ? root : default;
        foreach (string segment in pointer.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            TryGet(at, segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal), out at);
        }

        return at;
    }

    /// <summary>Whether a value is of a kind the schema's type allows; a null fits a nullable type or a reference-typed one.</summary>
    private static bool Fits(JsonElement schema, JsonElement value)
    {
        string[] kinds = KindsOf(schema);
        if (kinds.Length == 0)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return kinds.Any(kind => kind is "null" or "string" or "object" or "array");
        }

        string given = KindOf(value);
        return kinds.Any(kind => kind == given || (kind is "number" or "integer" && FitsNumber(kind, value)));
    }

    /// <summary>Whether a value binds to a number or an integer, as a JSON number or, since binding reads numbers from strings, a string.</summary>
    private static bool FitsNumber(string kind, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()!;
            return kind == "integer"
                ? long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        return value.ValueKind == JsonValueKind.Number && (kind == "number" || value.TryGetInt64(out _));
    }

    private static string KindOf(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            _ => "null",
        };

    private static string[] KindsOf(JsonElement schema)
    {
        if (!TryGet(schema, "type", out JsonElement type))
        {
            return [];
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => [type.GetString()!],
            JsonValueKind.Array => [.. type.EnumerateArray().Where(kind => kind.ValueKind == JsonValueKind.String).Select(kind => kind.GetString()!)],
            _ => [],
        };
    }

    private static string Mismatch(JsonElement schema, JsonElement resolved, JsonElement value, string path)
    {
        string kinds = string.Join(" or ", KindsOf(resolved).Where(kind => kind != "null").Select(Phrase));
        string message = $"{path} takes {kinds}, not {Given(value)}.";
        string? description = DescriptionOf(schema) ?? DescriptionOf(resolved);
        return description is null ? message : $"{message} {path[(path.LastIndexOf('.') + 1)..]}: {description}";
    }

    private static string Phrase(string kind) =>
        kind switch
        {
            "string" => "a string",
            "number" => "a number",
            "integer" => "an integer",
            "boolean" => "true or false",
            "object" => "an object",
            "array" => "an array",
            _ => kind,
        };

    /// <summary>The value's JSON, cut to <see cref="GivenLimit"/> characters and an ellipsis.</summary>
    private static string Given(JsonElement value)
    {
        string raw = JsonSerializer.Serialize(value, Shown);
        return raw.Length <= GivenLimit ? raw : raw[..GivenLimit] + "…";
    }

    private static string? DescriptionOf(JsonElement schema) =>
        TryGet(schema, "description", out JsonElement description) && description.ValueKind == JsonValueKind.String ? description.GetString() : null;

    /// <summary>"it takes: a, b." with the schema's property names in its order, or "it takes none." when it lists none.</summary>
    private static string Takes(JsonElement schema)
    {
        string[] names =
            TryGet(schema, "properties", out JsonElement properties) && properties.ValueKind == JsonValueKind.Object
                ? [.. properties.EnumerateObject().Select(property => property.Name)]
                : [];
        return names.Length == 0 ? "it takes none." : $"it takes: {string.Join(", ", names)}.";
    }

    private static JsonElement? PropertyOf(JsonElement schema, string name, StringComparison comparison)
    {
        if (!TryGet(schema, "properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in properties.EnumerateObject())
        {
            if (string.Equals(property.Name, name, comparison))
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>The schema every key of a free-form object takes: its <c>additionalProperties</c>, when that is a schema.</summary>
    private static JsonElement? AdditionalSchema(JsonElement schema) =>
        TryGet(schema, "additionalProperties", out JsonElement additional) && additional.ValueKind == JsonValueKind.Object ? additional : null;

    /// <summary>Whether an object refuses keys it does not list: it lists properties and does not allow additional ones.</summary>
    private static bool IsClosed(JsonElement schema) =>
        TryGet(schema, "properties", out _)
        && !(TryGet(schema, "additionalProperties", out JsonElement additional) && additional.ValueKind == JsonValueKind.True);

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return element.TryGetProperty(name, out value);
        }

        value = default;
        return false;
    }

    /// <summary>The binding failure's own message, less the JSON path and position it gives relative to one parameter.</summary>
    private static string Fallback(string tool, Exception exception)
    {
        string message = exception.Message;
        int path = message.IndexOf(" Path:", StringComparison.Ordinal);
        int line = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        int cut = path >= 0 ? path : line;
        string reason = (cut < 0 ? message : message[..cut]).TrimEnd();
        return reason.EndsWith('.') ? $"{tool}'s arguments do not bind: {reason}" : $"{tool}'s arguments do not bind: {reason}.";
    }
}
