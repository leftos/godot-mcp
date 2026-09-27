using System.Collections;
using System.Reflection;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// Walks every segment but the last, refusing a struct met on the way, and turns the last into a <see cref="MemberSlot"/>,
/// refusing what cannot be written: a property with no setter, a const and a static readonly field.
/// </summary>
internal static class MemberSlotFinder
{
    public static MemberSlot Find(MemberRoot root, MemberPath path, BindingFlags flags)
    {
        int last = path.Segments.Count - 1;
        MemberValue holder = MemberPathWalker.Follow(root, path, last, flags, refuseStructs: true);
        if (last == 0 && root.StaticType is { } type)
        {
            return ForMember(null, type, MemberPathWalker.ResolveStatic(type, path, flags), path, 0);
        }
        return ForSegment(MemberPathWalker.Holder(holder.Value, path, last, refuseStructs: true), path, last, flags);
    }

    private static MemberSlot ForSegment(object holder, MemberPath path, int i, BindingFlags flags) =>
        path.Segments[i] switch
        {
            MemberSegment member => ForMember(holder, holder.GetType(), MemberPathWalker.Resolve(holder.GetType(), member.Name, flags), path, i),
            IndexSegment index => ForIndex(holder, index.Index, path, i),
            KeySegment key => holder is IDictionary dictionary ? ForEntry(dictionary, key.Key, path, i) : ForIndexer(holder, key.Key, path, i),
            _ => throw new MemberPathException($"unknown segment at {path.Prefix(i + 1)}"),
        };

    private static MemberSlot ForMember(object? holder, Type owner, MemberInfo member, MemberPath path, int i)
    {
        string name = $"{TypeNames.Format(owner)}.{member.Name}";
        return member is PropertyInfo property ? ForProperty(holder, property, name, path, i) : ForField(holder, (FieldInfo)member, name, path, i);
    }

    private static MemberSlot ForProperty(object? holder, PropertyInfo property, string name, MemberPath path, int i)
    {
        if (property.SetMethod is null)
        {
            throw new MemberPathException($"{name} has no setter");
        }
        if (property.GetMethod is null)
        {
            throw new MemberPathException($"{name} has no getter, so a set could not be read back");
        }
        return new MemberSlot(path, i, property.PropertyType, () => property.GetValue(holder), value => property.SetValue(holder, value));
    }

    private static MemberSlot ForField(object? holder, FieldInfo field, string name, MemberPath path, int i)
    {
        if (field.IsLiteral)
        {
            throw new MemberPathException($"{name} is a const");
        }
        if (field.IsStatic && field.IsInitOnly)
        {
            throw new MemberPathException($"{name} is static readonly; the runtime refuses to write it");
        }
        return new MemberSlot(path, i, field.FieldType, () => field.GetValue(holder), value => field.SetValue(holder, value));
    }

    private static MemberSlot ForIndex(object holder, int index, MemberPath path, int i)
    {
        if (holder is IList list)
        {
            MemberPathWalker.CheckRange(list, index, path, i);
            Type element = MemberPathWalker.GenericArgument(list.GetType(), typeof(IList<>), 0);
            return new MemberSlot(path, i, element, () => list[index], value => list[index] = value);
        }
        return holder is IDictionary dictionary ? ForEntry(dictionary, index, path, i) : ForIndexer(holder, index, path, i);
    }

    private static MemberSlot ForEntry(IDictionary dictionary, object raw, MemberPath path, int i)
    {
        object key = MemberPathWalker.DictionaryKey(dictionary, raw, path, i);
        Type entry = MemberPathWalker.GenericArgument(dictionary.GetType(), typeof(IDictionary<,>), 1);
        return new MemberSlot(path, i, entry, () => dictionary[key], value => dictionary[key] = value);
    }

    /// <summary>
    /// Refuses a key a generic-only dictionary (Godot's <c>Dictionary&lt;K, V&gt;</c>, which is no
    /// <see cref="IDictionary"/>) has no entry for, as a set would add it rather than change an entry.
    /// </summary>
    private static void CheckGenericKey(object holder, object key, MemberPath path, int i)
    {
        Type? face = holder
            .GetType()
            .GetInterfaces()
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        if (face is null || !face.GetGenericArguments()[0].IsInstanceOfType(key))
        {
            return;
        }
        if (!(bool)face.GetMethod(nameof(IDictionary<,>.ContainsKey))!.Invoke(holder, [key])!)
        {
            throw MemberPathWalker.AbsentKey(key, path, i);
        }
    }

    private static MemberSlot ForIndexer(object holder, object key, MemberPath path, int i)
    {
        PropertyInfo indexer = MemberPathWalker.FindIndexer(holder, key, path, i);
        if (indexer.SetMethod is null)
        {
            throw new MemberPathException($"{TypeNames.Format(holder.GetType())}'s indexer has no setter at {path.Prefix(i + 1)}");
        }
        CheckGenericKey(holder, key, path, i);
        return new MemberSlot(path, i, indexer.PropertyType, () => indexer.GetValue(holder, [key]), value => indexer.SetValue(holder, value, [key]));
    }
}
