using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace GodotMcp.Dotnet.Core;

/// <summary>Follows a <see cref="MemberPath"/> from a root; every failure names the segment and the path so far.</summary>
internal static class MemberPathWalker
{
    public static MemberValue Read(MemberRoot root, MemberPath path, BindingFlags flags) =>
        Follow(root, path, path.Segments.Count, flags, refuseStructs: false);

    /// <summary>
    /// The value of the path's first <paramref name="count"/> segments, with its declared type. A type root resolves the
    /// first segment among the type's statics. With <paramref name="refuseStructs"/>, a value type met as the holder of
    /// a later segment is refused, since a set through it would change a copy.
    /// </summary>
    internal static MemberValue Follow(MemberRoot root, MemberPath path, int count, BindingFlags flags, bool refuseStructs)
    {
        MemberValue current = new(root.Instance, root.StaticType ?? root.Instance?.GetType() ?? typeof(object));
        for (int i = 0; i < count; i++)
        {
            current =
                i == 0 && root.StaticType is { } type
                    ? ReadMember(null, ResolveStatic(type, path, flags), path, 0)
                    : Step(Holder(current.Value, path, i, refuseStructs), path, i, flags);
        }
        return current;
    }

    /// <summary>The value segment <paramref name="i"/> is read from, refused when null or, under a set, a struct copy.</summary>
    internal static object Holder(object? value, MemberPath path, int i, bool refuseStructs)
    {
        if (value is null)
        {
            throw i == 0
                ? new MemberPathException("the target is null")
                : new MemberPathException($"'{path.Segments[i - 1]}' is null at {path.Prefix(i)}");
        }
        if (refuseStructs && i > 0 && value.GetType().IsValueType)
        {
            string through = path.Prefix(i);
            throw new MemberPathException(
                $"'{through}' is {TypeNames.WithArticle(value.GetType())} struct; a set through it would change a copy — set '{through}' whole"
            );
        }
        return value;
    }

    /// <summary>The property or field <paramref name="name"/> on <paramref name="type"/> or a base, or the refusal naming why not.</summary>
    internal static MemberInfo Resolve(Type type, string name, BindingFlags flags) => Find(type, name, flags) ?? throw Missing(type, name);

    /// <summary>The static property or field the type root's first segment names, or the refusal naming why not.</summary>
    internal static MemberInfo ResolveStatic(Type type, MemberPath path, BindingFlags flags)
    {
        if (path.Segments[0] is not MemberSegment { Name: var name })
        {
            throw new MemberPathException($"a type target's path starts with a static member's name, not {path.Segments[0]}");
        }
        if (Find(type, name, (flags & ~BindingFlags.Instance) | BindingFlags.Static) is { } found)
        {
            return found;
        }
        throw Find(type, name, (flags & ~BindingFlags.Static) | BindingFlags.Instance) is null
            ? Missing(type, name)
            : new MemberPathException($"'{name}' is an instance member of {TypeNames.Format(type)}; target a {{node}} or {{handle}}");
    }

    internal static void CheckRange(IList list, int index, MemberPath path, int i)
    {
        if (index >= list.Count)
        {
            throw new MemberPathException($"index {index} is out of range (count {list.Count}) at {path.Prefix(i + 1)}");
        }
    }

    /// <summary>The type argument at <paramref name="position"/> of the generic interface <paramref name="definition"/>, or object.</summary>
    internal static Type GenericArgument(Type type, Type definition, int position) =>
        type.GetInterfaces()
            .FirstOrDefault(face => face.IsGenericType && face.GetGenericTypeDefinition() == definition)
            ?.GetGenericArguments()[position]
        ?? typeof(object);

    /// <summary>The dictionary's own key for <paramref name="raw"/>, refused when the dictionary has no entry for it.</summary>
    internal static object DictionaryKey(IDictionary dictionary, object raw, MemberPath path, int i)
    {
        object? key = ConvertKey(raw, GenericArgument(dictionary.GetType(), typeof(IDictionary<,>), 0));
        if (key is null || !dictionary.Contains(key))
        {
            throw AbsentKey(raw, path, i);
        }
        return key;
    }

    /// <summary>The refusal for a key the dictionary at segment <paramref name="i"/> has no entry for.</summary>
    internal static MemberPathException AbsentKey(object raw, MemberPath path, int i)
    {
        string shown = raw is string text ? $"\"{text}\"" : Convert.ToString(raw, CultureInfo.InvariantCulture)!;
        return new MemberPathException($"key {shown} is not in the dictionary at {path.Prefix(i + 1)}");
    }

    /// <summary>The single-parameter indexer taking exactly <paramref name="key"/>'s type.</summary>
    internal static PropertyInfo FindIndexer(object current, object key, MemberPath path, int i) =>
        current
            .GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(property => property.GetIndexParameters() is [ParameterInfo only] && only.ParameterType == key.GetType())
        ?? throw new MemberPathException(
            $"{TypeNames.Format(current.GetType())} has no indexer taking {TypeNames.WithArticle(key.GetType())} at {path.Prefix(i + 1)}"
        );

    /// <summary>
    /// Runs a getter or setter; what it throws becomes a <see cref="MemberPathException"/> naming the segment and carrying
    /// the thrown exception's type, message and stack.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A getter or setter runs game code; whatever it throws is the failure to report."
    )]
    internal static object? Guard(Func<object?> run, MemberPath path, int i, string action)
    {
        try
        {
            return run();
        }
        catch (TargetInvocationException e) when (e.InnerException is Exception inner)
        {
            throw Threw(inner, path, i, action, e);
        }
        catch (Exception e)
        {
            throw Threw(e, path, i, action, e);
        }
    }

    private static MemberPathException Threw(Exception thrown, MemberPath path, int i, string action, Exception cause)
    {
        string stack = thrown.StackTrace is { Length: > 0 } trace ? "\n" + trace : "";
        return new MemberPathException(
            $"{action}'{path.Segments[i]}' threw {thrown.GetType().Name}: {thrown.Message} at {path.Prefix(i + 1)}{stack}",
            cause
        );
    }

    private static MemberValue Step(object current, MemberPath path, int i, BindingFlags flags) =>
        path.Segments[i] switch
        {
            MemberSegment member => ReadMember(current, Resolve(current.GetType(), member.Name, flags), path, i),
            IndexSegment index => Index(current, index.Index, path, i),
            KeySegment key => Key(current, key.Key, path, i),
            _ => throw new MemberPathException($"unknown segment at {path.Prefix(i + 1)}"),
        };

    private static MemberValue ReadMember(object? instance, MemberInfo member, MemberPath path, int i) =>
        member is PropertyInfo property
            ? new MemberValue(Guard(() => property.GetValue(instance), path, i, ""), property.PropertyType)
            : new MemberValue(Guard(() => ((FieldInfo)member).GetValue(instance), path, i, ""), ((FieldInfo)member).FieldType);

    private static MemberInfo? Find(Type type, string name, BindingFlags flags)
    {
        for (Type? declaring = type; declaring is not null; declaring = declaring.BaseType)
        {
            MemberInfo? found = declaring
                .GetMember(name, MemberTypes.Property | MemberTypes.Field, flags | BindingFlags.DeclaredOnly)
                .FirstOrDefault(member => member is FieldInfo || ((PropertyInfo)member).GetIndexParameters().Length == 0);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private static MemberPathException Missing(Type type, string name) =>
        HasMethod(type, name)
            ? new MemberPathException($"'{name}' is a method of {TypeNames.Format(type)}; cs_call calls it")
            : new MemberPathException($"{TypeNames.Format(type)} has no member '{name}'");

    private static bool HasMethod(Type type, string name)
    {
        const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        for (Type? declaring = type; declaring is not null; declaring = declaring.BaseType)
        {
            if (declaring.GetMember(name, MemberTypes.Method, Any).Length > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static MemberValue Index(object current, int index, MemberPath path, int i)
    {
        if (current is IList list)
        {
            CheckRange(list, index, path, i);
            return new MemberValue(Guard(() => list[index], path, i, ""), GenericArgument(list.GetType(), typeof(IList<>), 0));
        }
        return current is IDictionary dictionary ? Lookup(dictionary, index, path, i) : Indexer(current, index, path, i);
    }

    private static MemberValue Key(object current, string key, MemberPath path, int i) =>
        current is IDictionary dictionary ? Lookup(dictionary, key, path, i) : Indexer(current, key, path, i);

    private static MemberValue Lookup(IDictionary dictionary, object raw, MemberPath path, int i)
    {
        object key = DictionaryKey(dictionary, raw, path, i);
        return new MemberValue(Guard(() => dictionary[key], path, i, ""), GenericArgument(dictionary.GetType(), typeof(IDictionary<,>), 1));
    }

    private static MemberValue Indexer(object current, object key, MemberPath path, int i)
    {
        PropertyInfo indexer = FindIndexer(current, key, path, i);
        return new MemberValue(Guard(() => indexer.GetValue(current, [key]), path, i, ""), indexer.PropertyType);
    }

    private static object? ConvertKey(object raw, Type keyType)
    {
        if (keyType.IsInstanceOfType(raw))
        {
            return raw;
        }
        if (keyType.IsEnum)
        {
            return Enum.TryParse(keyType, Convert.ToString(raw, CultureInfo.InvariantCulture), ignoreCase: true, out object? member) ? member : null;
        }
        return typeof(IConvertible).IsAssignableFrom(keyType) ? ChangeType(raw, keyType) : null;
    }

    private static object? ChangeType(object raw, Type keyType)
    {
        try
        {
            return Convert.ChangeType(raw, keyType, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }
}
