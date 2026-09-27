using System.Reflection;
using System.Text.Json.Nodes;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Dotnet;

/// <summary>
/// The helper's <c>get</c> and <c>set</c> ops: a member path read, or written and read back, from the request's
/// <c>{node}</c>, <c>{type}</c> or <c>{handle}</c> target.
/// </summary>
internal static class MemberAccess
{
    private const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private const int DefaultDepth = 8;

    private const int MaxDepth = 32;

    private const string Scriptless = "inspect_node and set_property reach its properties";

    private static readonly TargetHints GetHints = new("cs_get", Scriptless);

    private static readonly TargetHints SetHints = new("cs_set", Scriptless);

    private static readonly GodotResolver Resolver = new();

    /// <summary>
    /// <c>{"op":"get","target":{..},"member":"A.B[0]","maxDepth":8,"keep":true}</c> →
    /// <c>{"value":..,"type":"..","handle"?:"h..","warning"?:".."}</c>.
    /// </summary>
    public static JsonObject Get(JsonObject request)
    {
        int maxDepth = request["maxDepth"]?.GetValue<int>() ?? DefaultDepth;
        if (maxDepth is < 1 or > MaxDepth)
        {
            return Helper.Failure($"maxDepth {maxDepth} is out of range: give 1 to {MaxDepth}.");
        }
        Resolution resolution = Targets.Resolve(request["target"]!.AsObject(), GetHints);
        if (resolution.Failure is { } failure)
        {
            return failure;
        }
        MemberValue read;
        try
        {
            read = MemberPath.Read(resolution.Found!.Root, MemberPath.Parse(request["member"]!.GetValue<string>()), Everything);
        }
        catch (MemberPathException e)
        {
            return Helper.Failure(e.Message);
        }
        return Success(Reply(read, maxDepth, request["keep"]?.GetValue<bool>() ?? false));
    }

    /// <summary>
    /// <c>{"op":"set","target":{..},"member":"..","value":..}</c> → <c>{"member":"..","before":..,"after":..}</c>, or, when
    /// the value read back differs from the one written, the old value put back and the refusal naming both.
    /// </summary>
    public static JsonObject Set(JsonObject request)
    {
        Resolution resolution = Targets.Resolve(request["target"]!.AsObject(), SetHints);
        if (resolution.Failure is { } failure)
        {
            return failure;
        }
        try
        {
            return Assign(resolution.Found!, request["member"]!.GetValue<string>(), request["value"]);
        }
        catch (MemberPathException e)
        {
            return Helper.Failure(e.Message);
        }
        catch (HandleException e)
        {
            return Helper.Failure(e.Message);
        }
    }

    private static JsonObject Reply(MemberValue read, int maxDepth, bool keep)
    {
        JsonObject result = new()
        {
            ["value"] = ValueWriter.Write(read.Value, new GodotFormatter(maxDepth), maxDepth),
            ["type"] = (read.Value?.GetType() ?? read.DeclaredType).FullName,
        };
        if (keep)
        {
            Keep(read.Value, result);
        }
        return result;
    }

    /// <summary>
    /// Adds a handle to <paramref name="value"/> to <paramref name="result"/>, or a warning for a null, and one for a value
    /// type, whose handle holds a copy; shared with <see cref="Calls"/>.
    /// </summary>
    internal static void Keep(object? value, JsonObject result)
    {
        if (value is null)
        {
            result["warning"] = "the value is null; nothing to keep";
            return;
        }
        result["handle"] = Targets.Handles.Add(value);
        if (value.GetType().IsValueType)
        {
            result["warning"] =
                $"{TypeNames.Format(value.GetType())} is a value type: the handle holds a copy, and a set through it does not reach the source";
        }
    }

    private static JsonObject Assign(Target target, string member, JsonNode? value)
    {
        MemberSlot slot = MemberPath.Slot(target.Root, MemberPath.Parse(member), Everything);
        return Success(MemberSetter.Set(slot, member, value, Resolver, new GodotFormatter(DefaultDepth)));
    }

    private static JsonObject Success(JsonObject result) => new() { ["ok"] = true, ["result"] = result };
}
