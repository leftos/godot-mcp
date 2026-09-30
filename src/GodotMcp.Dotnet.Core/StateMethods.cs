using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// Finds a node's state method, <c>_McpState</c>: an instance method with no parameters and no type parameters, of any
/// accessibility, declared on the type or on a base below the first one <c>stopAt</c> names, the most derived first, as
/// <see cref="MemberListing"/> walks them. What a type has, or that it has none, is found once per type.
/// </summary>
public sealed class StateMethods(Func<Type, bool> stopAt)
{
    public const string MethodName = "_McpState";

    /// <summary>
    /// How many values one node's state writes before each further one is <see cref="ValueWriter.SizeLimit"/>, as the
    /// bridge's MAX_VALUES.
    /// </summary>
    public const int MaxValues = 5000;

    private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly Func<Type, bool> _stopAt = stopAt ?? throw new ArgumentNullException(nameof(stopAt));

    private readonly ConcurrentDictionary<Type, MethodInfo?> _found = new();

    /// <summary>The state method of <paramref name="type"/>, or null when neither it nor a base below the stop declares one.</summary>
    public MethodInfo? Find(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _found.GetOrAdd(type, Walk);
    }

    /// <summary>
    /// Adds to <paramref name="entry"/> what <paramref name="target"/>'s state method gives, as <see cref="Read"/> writes it;
    /// or, when its type has none, <c>missing: true</c> and <see cref="Missing"/> as its <c>error</c>; or as its
    /// <c>error</c> what finding the method threw (a type whose members cannot load), so the other nodes are still read.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever looking up a game type's members throws is that node's error, not the whole read's."
    )]
    public void ReadEntry(JsonObject entry, object target, IValueFormatter formatter, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);
        Type type = target.GetType();
        MethodInfo? method;
        try
        {
            method = Find(type);
        }
        catch (Exception e)
        {
            entry["error"] = Thrown.Describe(Thrown.Unwrap(e));
            return;
        }
        if (method is null)
        {
            entry["missing"] = true;
            entry["error"] = Missing(type);
            return;
        }
        Read(entry, target, method, formatter, maxDepth);
    }

    /// <summary>The error for an object of <paramref name="type"/> that has no state method.</summary>
    public static string Missing(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return $"{FullName(type)} has no _McpState() (an instance method with no parameters, any accessibility)";
    }

    /// <summary>
    /// The error for a state method of an object of <paramref name="type"/> that returns a task, <paramref name="returned"/>
    /// being the type it returns; null when that is not a task, so the value is the state. The task is named by the first
    /// public type up its base chain, so an async method's hidden state-machine box reads as the <c>Task&lt;T&gt;</c> it is.
    /// </summary>
    public static string? TaskRefusal(Type type, Type returned)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(returned);
        return IsTask(returned)
            ? $"{FullName(type)}._McpState returns a {TypeNames.Format(FirstPublic(returned))}; a state method must return its value, not a task"
            : null;
    }

    /// <summary>
    /// Adds to <paramref name="entry"/> the value <paramref name="method"/> returns on <paramref name="target"/> as its
    /// <c>state</c>, written by <see cref="ValueWriter"/> to <paramref name="maxDepth"/> levels and <see cref="MaxValues"/>
    /// values; or as its <c>error</c> what
    /// the method or the write threw, or that it returns a task, which is never awaited (a method declared to return one is
    /// not called). A throw is this entry's error alone, so the other nodes of a read are still read.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a game's state method or its value throws is that node's error, not the whole read's."
    )]
    public static void Read(JsonObject entry, object target, MethodInfo method, IValueFormatter formatter, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(method);
        Type type = target.GetType();
        if (TaskRefusal(type, method.ReturnType) is { } declared)
        {
            entry["error"] = declared;
            return;
        }
        try
        {
            object? value = method.Invoke(target, null);
            if (value is not null && TaskRefusal(type, value.GetType()) is { } returned)
            {
                entry["error"] = returned;
                return;
            }
            entry["state"] = ValueWriter.WriteBounded(value, formatter, maxDepth, MaxValues);
        }
        catch (Exception e)
        {
            entry["error"] = Thrown.Describe(Thrown.Unwrap(e));
        }
    }

    private MethodInfo? Walk(Type type)
    {
        for (Type? level = type; level is not null && level != typeof(object) && !_stopAt(level); level = level.BaseType)
        {
            MethodInfo? method = Array.Find(level.GetMethods(Declared), IsStateMethod);
            if (method is not null)
            {
                return method;
            }
        }
        return null;
    }

    private static bool IsStateMethod(MethodInfo method) =>
        method.Name == MethodName && !method.IsGenericMethodDefinition && method.GetParameters().Length == 0;

    private static bool IsTask(Type type) =>
        typeof(Task).IsAssignableFrom(type)
        || type == typeof(ValueTask)
        || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>));

    /// <summary><paramref name="type"/>, or its first base that is public (Task and Task&lt;T&gt; are, so a task's walk ends there).</summary>
    private static Type FirstPublic(Type type)
    {
        Type named = type;
        while (!(named.IsPublic || named.IsNestedPublic) && named.BaseType is { } baseType)
        {
            named = baseType;
        }
        return named;
    }

    private static string FullName(Type type) => type.FullName ?? type.Name;
}
