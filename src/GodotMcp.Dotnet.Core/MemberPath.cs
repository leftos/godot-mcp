using System.Reflection;
using System.Text;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// A dotted path of member names and indexes, e.g. <c>BoundUpdate.Pending.Options[0]</c>, <c>Map["key"]</c>,
/// <c>Items[2].Name</c>.
/// </summary>
public sealed class MemberPath
{
    public required string Text { get; init; }

    public required IReadOnlyList<MemberPathSegment> Segments { get; init; }

    /// <summary>Parses <paramref name="text"/>; throws <see cref="MemberPathException"/> naming the character index.</summary>
    public static MemberPath Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new MemberPath { Text = text, Segments = new MemberPathParser(text).ParseAll() };
    }

    /// <summary>
    /// Follows <paramref name="path"/> from <paramref name="root"/> through properties, fields and indexers (lists by
    /// integer, dictionaries by key), finding members with <paramref name="flags"/> on the type and its bases.
    /// </summary>
    public static object? Walk(object? root, MemberPath path, BindingFlags flags)
    {
        ArgumentNullException.ThrowIfNull(path);
        return MemberPathWalker.Walk(root, path, flags);
    }

    public override string ToString() => Text;

    /// <summary>The path through its first <paramref name="count"/> segments, as text.</summary>
    internal string Prefix(int count)
    {
        StringBuilder text = new();
        for (int i = 0; i < count; i++)
        {
            if (i > 0 && Segments[i] is MemberSegment)
            {
                text.Append('.');
            }
            text.Append(Segments[i]);
        }
        return text.ToString();
    }
}

/// <summary>One step of a <see cref="MemberPath"/>.</summary>
public abstract record MemberPathSegment;

/// <summary>A property or field by name.</summary>
public sealed record MemberSegment(string Name) : MemberPathSegment
{
    public override string ToString() => Name;
}

/// <summary>An integer index: <c>[2]</c>.</summary>
public sealed record IndexSegment(int Index) : MemberPathSegment
{
    public override string ToString() => $"[{Index}]";
}

/// <summary>A quoted key: <c>["key"]</c>.</summary>
public sealed record KeySegment(string Key) : MemberPathSegment
{
    public override string ToString() =>
        $"[\"{Key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"]";
}
