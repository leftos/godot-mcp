using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Godot;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Dotnet;

/// <summary>
/// The base class of a <c>run_csharp</c> snippet: the compiled <c>GodotMcpSnippet</c> derives from it, so the snippet names
/// these members unqualified. They reach the running game as the C# tools do: a node by the bridge's rule, a kept object by
/// its handle, and a member by path or a method by name, private and internal ones included, with CLR values in and out.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "The members are the snippet's own inherited vocabulary, instance members of the globals object it runs as."
)]
public abstract class SnippetGlobals
{
    private const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>The game's main loop.</summary>
    protected SceneTree Tree => (SceneTree)Engine.GetMainLoop();

    /// <summary>The root window, <see cref="Tree"/>'s <c>Root</c>.</summary>
    protected Window Root => Tree.Root;

    /// <summary>The node a path or a bare name names, by the bridge's rule: a path, or the first node of that name.</summary>
    /// <exception cref="InvalidOperationException">No such node, or it is the bridge's own.</exception>
    protected Node Node(string path)
    {
        Node? node = Targets.Find(path);
        return node is null || Targets.InBridge(node)
            ? throw new InvalidOperationException($"No node '{path}' in the running game; get_scene_tree lists the nodes' paths.")
            : node;
    }

    /// <summary>The node <see cref="Node(string)"/> finds, as a <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidCastException">The node is not a <typeparamref name="T"/>.</exception>
    protected T Node<T>(string path)
        where T : class => Cast<T>(Node(path), path);

    /// <summary>The object kept under handle <paramref name="id"/>.</summary>
    /// <exception cref="HandleException">The id is malformed, from before a restart, or dropped.</exception>
    protected object Handle(string id) => Targets.Handles.Get(id);

    /// <summary>The object kept under handle <paramref name="id"/>, as a <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidCastException">The object is not a <typeparamref name="T"/>.</exception>
    protected T Handle<T>(string id)
        where T : class => Cast<T>(Handle(id), id);

    /// <summary>The value at <paramref name="path"/> from <paramref name="target"/>, as <c>cs_get</c> reads it.</summary>
    /// <exception cref="MemberPathException">The path is malformed, names no member, or a getter threw.</exception>
    protected object? Get(object target, string path) => Read(MemberRoot.Of(target), path);

    /// <summary>The value at <paramref name="path"/> from <paramref name="target"/>, as a <typeparamref name="T"/>.</summary>
    protected T Get<T>(object target, string path) => Value<T>(Get(target, path), path);

    /// <summary>The value at <paramref name="path"/> from <paramref name="type"/>'s statics, as <c>cs_get</c> reads it.</summary>
    protected object? Get(Type type, string path) => Read(MemberRoot.Statics(type), path);

    /// <summary>The value at <paramref name="path"/> from <paramref name="type"/>'s statics, as a <typeparamref name="T"/>.</summary>
    protected T Get<T>(Type type, string path) => Value<T>(Get(type, path), path);

    /// <summary>
    /// Writes <paramref name="value"/> to the slot <paramref name="path"/> names from <paramref name="target"/>; nothing is read back.
    /// </summary>
    /// <exception cref="MemberPathException">The path names no settable slot, the value is not of its type, or the setter threw.</exception>
    protected void Set(object target, string path, object? value) => Write(MemberRoot.Of(target), path, value);

    /// <summary>Writes <paramref name="value"/> to the slot <paramref name="path"/> names from <paramref name="type"/>'s statics.</summary>
    protected void Set(Type type, string path, object? value) => Write(MemberRoot.Statics(type), path, value);

    /// <summary>
    /// Calls <paramref name="method"/> on <paramref name="target"/>, the overload chosen by the arguments' runtime types; what it
    /// throws is rethrown with its own stack, and a returned <see cref="Task"/> is returned for the snippet to await.
    /// </summary>
    /// <exception cref="OverloadException">The name is no method <c>cs_call</c> calls, or no one overload takes the arguments.</exception>
    protected object? Call(object target, string method, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Invoke(CallCandidates.Find(target.GetType(), method, staticsOnly: false, Targets.StopAtGodot), target, args);
    }

    /// <summary>Calls the static <paramref name="method"/> of <paramref name="type"/>, as the instance form does.</summary>
    protected object? Call(Type type, string method, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Invoke(CallCandidates.Find(type, method, staticsOnly: true, Targets.StopAtGodot), null, args);
    }

    /// <summary>Keeps <paramref name="value"/> in the handle table the C# tools share and answers its handle id.</summary>
    protected string Keep(object value) => Targets.Handles.Add(value);

    private static object? Read(MemberRoot root, string path) => MemberPath.Read(root, MemberPath.Parse(path), Everything).Value;

    private static void Write(MemberRoot root, string path, object? value)
    {
        MemberSlot slot = MemberPath.Slot(root, MemberPath.Parse(path), Everything);
        if (Mismatch(slot.Type, value) is { } mismatch)
        {
            throw new MemberPathException($"cannot set '{path}' ({TypeNames.Format(slot.Type)}): {mismatch}");
        }
        slot.Write(value);
    }

    /// <summary>Why a slot of <paramref name="type"/> cannot hold <paramref name="value"/>, or null when it can.</summary>
    private static string? Mismatch(Type type, object? value)
    {
        if (value is null)
        {
            return type.IsValueType && Nullable.GetUnderlyingType(type) is null ? $"null is not {TypeNames.WithArticle(type)}" : null;
        }
        return type.IsInstanceOfType(value) ? null : $"{TypeNames.WithArticle(value.GetType())} is not {TypeNames.WithArticle(type)}";
    }

    private static object? Invoke(IReadOnlyList<MethodBase> candidates, object? instance, object?[]? args)
    {
        // A lone null literal binds to the params array itself; the snippet meant one null argument.
        OverloadChoice choice = ClrOverloadResolver.Choose(candidates, args ?? [null]);
        object?[] arguments = [.. choice.Arguments];
        try
        {
            return choice.Method is ConstructorInfo constructor
                ? constructor.Invoke(arguments)
                : choice.Method.Invoke(choice.Method.IsStatic ? null : instance, arguments);
        }
        catch (TargetInvocationException e) when (e.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Throw(inner);
            throw;
        }
    }

    private static T Cast<T>(object value, string name)
        where T : class =>
        value as T ?? throw new InvalidCastException($"'{name}' is {TypeNames.Format(value.GetType())}, not {TypeNames.Format(typeof(T))}");

    /// <summary><paramref name="value"/> as a <typeparamref name="T"/>; a null only where <typeparamref name="T"/> holds one.</summary>
    private static T Value<T>(object? value, string path)
    {
        if (value is T typed)
        {
            return typed;
        }
        if (value is null && default(T) is null)
        {
            return default!;
        }
        string actual = value is null ? "null" : TypeNames.Format(value.GetType());
        throw new InvalidCastException($"'{path}' is {actual}, not {TypeNames.Format(typeof(T))}");
    }
}
