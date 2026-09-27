using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Nodes;
using Godot;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Dotnet;

/// <summary>
/// The helper's <c>call</c> op: a method or constructor of the request's <c>{node}</c>, <c>{type}</c> or <c>{handle}</c>
/// target, chosen among its overloads and run on the main thread. A method whose declared return is a <c>Task</c> or
/// <c>ValueTask</c> that has not finished answers <c>{"ok":true,"pending":"c&lt;n&gt;"}</c> from <see cref="PendingTasks"/>,
/// and the bridge polls that id each frame until the reply is final or it forgets the call.
/// </summary>
internal static class Calls
{
    private const int DefaultDepth = 8;

    private const int MaxDepth = 32;

    private const BindingFlags Constructors = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly TargetHints Hints = new("cs_call", "call_method calls its methods");

    private static readonly GodotResolver Resolver = new();

    /// <summary>
    /// <c>{"op":"call","target":{..},"member":"Name"|".ctor","args":[..],"signature"?:[..],"typeArgs"?:[..],"keep"?:bool,
    /// "maxDepth"?:int,"timeoutMs"?:int}</c> → <c>{"value":..,"type":"..","handle"?:"h..","warning"?:"..","outs"?:{..}}</c>,
    /// or the pending reply. <c>timeoutMs</c> is the bridge's to enforce and is not read here.
    /// </summary>
    public static JsonObject Call(JsonObject request)
    {
        int maxDepth = request["maxDepth"]?.GetValue<int>() ?? DefaultDepth;
        if (maxDepth is < 1 or > MaxDepth)
        {
            return Helper.Failure($"maxDepth {maxDepth} is out of range: give 1 to {MaxDepth}.");
        }
        Resolution resolution = Targets.Resolve(request["target"]!.AsObject(), Hints);
        if (resolution.Failure is { } failure)
        {
            return failure;
        }
        Shape shape = new(request["member"]!.GetValue<string>(), maxDepth, request["keep"]?.GetValue<bool>() ?? false);
        try
        {
            return Run(resolution.Found!, shape, request);
        }
        catch (OverloadException e)
        {
            return Helper.Failure(e.Message);
        }
    }

    private static JsonObject Run(Target target, Shape shape, JsonObject request)
    {
        JsonArray args = request["args"]?.AsArray() ?? [];
        if (shape.Member != CallCandidates.Constructor)
        {
            return Invoke(Choose(target, shape.Member, args, request), target.Instance, shape);
        }
        if (target.Instance is not null)
        {
            return Helper.Failure(
                $"'{CallCandidates.Constructor}' constructs from a {{type}} target; a {{node}} or {{handle}} is an instance already."
            );
        }
        if (args.Count == 0 && target.Type.IsValueType && target.Type.GetConstructor(Constructors, Type.EmptyTypes) is null)
        {
            return Success(Result(Activator.CreateInstance(target.Type), target.Type, shape, null, constructed: true));
        }
        return Invoke(Choose(target, shape.Member, args, request), null, shape);
    }

    private static OverloadChoice Choose(Target target, string member, JsonArray args, JsonObject request)
    {
        IReadOnlyList<string>? signature = Strings(request["signature"]);
        IReadOnlyList<Type>? typeArgs = Strings(request["typeArgs"]) is { } names ? TypeArguments.Parse(names, Targets.Found) : null;
        IReadOnlyList<MethodBase> candidates = CallCandidates.Find(target.Type, member, target.Instance is null, Targets.StopAtGodot);
        return OverloadResolver.Choose(candidates, args, signature, typeArgs, Resolver);
    }

    private static List<string>? Strings(JsonNode? json) => json?.AsArray().Select(item => item!.GetValue<string>()).ToList();

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The method runs game code; whatever it throws is the failure to report."
    )]
    private static JsonObject Invoke(OverloadChoice choice, object? instance, Shape shape)
    {
        object?[] arguments = [.. choice.Arguments];
        object? value;
        try
        {
            value = choice.Method is ConstructorInfo constructor
                ? constructor.Invoke(arguments)
                : choice.Method.Invoke(choice.Method.IsStatic ? null : instance, arguments);
        }
        catch (Exception e)
        {
            return Threw(shape.Member, e);
        }
        JsonObject? outs = Outs(choice, arguments, shape.MaxDepth);
        if (choice.Method is not MethodInfo method)
        {
            return Success(Result(value, choice.Method.DeclaringType!, shape, outs, constructed: true));
        }
        return Awaited(method.ReturnType) is { } awaited
            ? Await(value, method.ReturnType, new Waiting(Task.CompletedTask, awaited, shape, outs))
            : Success(Result(value, method.ReturnType, shape, outs, constructed: false));
    }

    /// <summary>
    /// What a declared return of <c>Task</c> or <c>ValueTask</c> gives when awaited: <c>void</c> or its <c>T</c>; null for any
    /// other type.
    /// </summary>
    private static Type? Awaited(Type declared)
    {
        if (declared == typeof(Task) || declared == typeof(ValueTask))
        {
            return typeof(void);
        }
        if (!declared.IsGenericType)
        {
            return null;
        }
        Type definition = declared.GetGenericTypeDefinition();
        return definition == typeof(Task<>) || definition == typeof(ValueTask<>) ? declared.GetGenericArguments()[0] : null;
    }

    /// <summary>Answers a finished task's outcome at once; an unfinished one goes to <see cref="PendingTasks"/>, which answers pending.</summary>
    private static JsonObject Await(object? value, Type declared, Waiting shell)
    {
        if (value is null)
        {
            return Helper.Failure($"{shell.Shape.Member} returned a null {TypeNames.Format(declared)}.");
        }
        Task task = value as Task ?? (Task)declared.GetMethod(nameof(ValueTask.AsTask), Type.EmptyTypes)!.Invoke(value, null)!;
        Waiting waiting = shell with { Task = task };
        return task.IsCompleted ? Settled(waiting) : PendingTasks.Add(task, () => Settled(waiting), null);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The awaited task ran game code; whatever it threw is the failure to report."
    )]
    private static JsonObject Settled(Waiting waiting)
    {
        try
        {
            waiting.Task.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            return Threw(waiting.Shape.Member, e);
        }
        object? value =
            waiting.Result == typeof(void)
                ? null
                : typeof(Task<>).MakeGenericType(waiting.Result).GetProperty(nameof(Task<>.Result))!.GetValue(waiting.Task);
        return Success(Result(value, waiting.Result, waiting.Shape, waiting.Outs, constructed: false));
    }

    /// <summary>
    /// <c>{value, type, handle?, warning?, outs?}</c>; <c>type</c> is the value's runtime type, or <paramref name="declared"/>
    /// for a null. A node a constructor made outside the tree is freed once written unless kept, and warned about when kept; one
    /// that added itself to the tree is left there.
    /// </summary>
    private static JsonObject Result(object? value, Type declared, Shape shape, JsonObject? outs, bool constructed)
    {
        JsonObject result = new()
        {
            ["value"] = ValueWriter.Write(value, new GodotFormatter(shape.MaxDepth), shape.MaxDepth),
            ["type"] = (value?.GetType() ?? declared).FullName,
        };
        if (shape.Keep)
        {
            MemberAccess.Keep(value, result);
        }
        if (constructed && value is Node node && !node.IsInsideTree())
        {
            Orphan(node, shape.Keep, result);
        }
        if (outs is not null)
        {
            result["outs"] = outs;
        }
        return result;
    }

    private static void Orphan(Node node, bool kept, JsonObject result)
    {
        if (!kept)
        {
            node.Free();
            return;
        }
        string warning = $"{TypeNames.Format(node.GetType())} is a node outside the tree: free it or add it as a child";
        result["warning"] = result["warning"] is { } earlier ? $"{earlier.GetValue<string>()}; {warning}" : warning;
    }

    /// <summary>The values the call wrote into its <c>ref</c> and <c>out</c> parameters, by name; null when it has none.</summary>
    private static JsonObject? Outs(OverloadChoice choice, object?[] arguments, int maxDepth)
    {
        if (choice.WrittenBack.Count == 0)
        {
            return null;
        }
        GodotFormatter formatter = new(maxDepth);
        JsonObject outs = [];
        foreach (ParameterInfo parameter in choice.WrittenBack)
        {
            outs[parameter.Name!] = ValueWriter.Write(arguments[parameter.Position], formatter, maxDepth);
        }
        return outs;
    }

    private static JsonObject Threw(string member, Exception e)
    {
        Exception thrown = Thrown.Unwrap(e);
        return Helper.Failure($"{member} threw {Thrown.Describe(thrown)}{Thrown.Stack(thrown)}");
    }

    private static JsonObject Success(JsonObject result) => new() { ["ok"] = true, ["result"] = result };

    /// <summary>What the reply to a call needs besides its value: the member's name, the writer's depth and whether to keep the value.</summary>
    private sealed record Shape(string Member, int MaxDepth, bool Keep);

    /// <summary>A call's task, what it gives when awaited (<c>void</c> or its <c>T</c>), and the rest of its reply.</summary>
    private sealed record Waiting(Task Task, Type Result, Shape Shape, JsonObject? Outs);
}
