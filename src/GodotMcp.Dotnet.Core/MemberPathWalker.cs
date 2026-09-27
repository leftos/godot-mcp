using System.Collections;
using System.Globalization;
using System.Reflection;

namespace GodotMcp.Dotnet.Core;

/// <summary>Follows a <see cref="MemberPath"/> from a root object; every failure names the segment and the path so far.</summary>
internal static class MemberPathWalker
{
    public static object? Walk(object? root, MemberPath path, BindingFlags flags)
    {
        object? current = root;
        for (int i = 0; i < path.Segments.Count; i++)
        {
            if (current is null)
            {
                throw i == 0
                    ? new MemberPathException("the target is null")
                    : new MemberPathException($"'{path.Segments[i - 1]}' is null at {path.Prefix(i)}");
            }
            current = Step(current, path, i, flags);
        }
        return current;
    }

    private static object? Step(object current, MemberPath path, int i, BindingFlags flags) =>
        path.Segments[i] switch
        {
            MemberSegment member => Member(current, member.Name, flags, path, i),
            IndexSegment index => Index(current, index.Index, path, i),
            KeySegment key => Key(current, key.Key, path, i),
            _ => throw new MemberPathException($"unknown segment at {path.Prefix(i + 1)}"),
        };

    private static object? Member(object current, string name, BindingFlags flags, MemberPath path, int i)
    {
        Type type = current.GetType();
        MemberInfo? member = Find(type, name, flags);
        return member switch
        {
            PropertyInfo property => Invoke(() => property.GetValue(current), path, i),
            FieldInfo field => field.GetValue(current),
            _ => throw new MemberPathException($"{TypeNames.Format(type)} has no member '{name}'"),
        };
    }

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

    private static object? Index(object current, int index, MemberPath path, int i)
    {
        if (current is IList list)
        {
            return index < list.Count
                ? list[index]
                : throw new MemberPathException($"index {index} is out of range (count {list.Count}) at {path.Prefix(i + 1)}");
        }
        return current is IDictionary dictionary ? Lookup(dictionary, index, path, i) : Indexer(current, index, path, i);
    }

    private static object? Key(object current, string key, MemberPath path, int i) =>
        current is IDictionary dictionary ? Lookup(dictionary, key, path, i) : Indexer(current, key, path, i);

    private static object? Lookup(IDictionary dictionary, object raw, MemberPath path, int i)
    {
        object? key = ConvertKey(raw, KeyType(dictionary));
        if (key is null || !dictionary.Contains(key))
        {
            string shown = raw is string text ? $"\"{text}\"" : Convert.ToString(raw, CultureInfo.InvariantCulture)!;
            throw new MemberPathException($"key {shown} is not in the dictionary at {path.Prefix(i + 1)}");
        }
        return dictionary[key];
    }

    private static Type KeyType(IDictionary dictionary) =>
        dictionary
            .GetType()
            .GetInterfaces()
            .FirstOrDefault(face => face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            ?.GetGenericArguments()[0]
        ?? typeof(object);

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

    private static object? Indexer(object current, object key, MemberPath path, int i)
    {
        PropertyInfo? indexer =
            current
                .GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(property => property.GetIndexParameters() is [ParameterInfo only] && only.ParameterType == key.GetType())
            ?? throw new MemberPathException(
                $"{TypeNames.Format(current.GetType())} has no indexer taking {TypeNames.WithArticle(key.GetType())} at {path.Prefix(i + 1)}"
            );
        return Invoke(() => indexer.GetValue(current, [key]), path, i);
    }

    private static object? Invoke(Func<object?> read, MemberPath path, int i)
    {
        try
        {
            return read();
        }
        catch (TargetInvocationException e) when (e.InnerException is Exception inner)
        {
            throw new MemberPathException($"'{path.Segments[i]}' threw {inner.GetType().Name}: {inner.Message} at {path.Prefix(i + 1)}", e);
        }
    }
}
