using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// The place a path's last segment names, ready to be set: a property with a setter (non-public and <c>init</c> ones
/// included), a field (readonly instance fields included), an <see cref="System.Collections.IList"/> index, an existing
/// dictionary entry or a single-parameter indexer. What a getter or setter throws becomes a
/// <see cref="MemberPathException"/> carrying the thrown exception's type, message and stack.
/// </summary>
public sealed class MemberSlot
{
    private readonly MemberPath _path;

    private readonly int _segment;

    private readonly Func<object?> _read;

    private readonly Action<object?> _write;

    internal MemberSlot(MemberPath path, int segment, Type type, Func<object?> read, Action<object?> write)
    {
        _path = path;
        _segment = segment;
        Type = type;
        _read = read;
        _write = write;
    }

    /// <summary>The type the slot declares, which a value is converted to.</summary>
    public Type Type { get; }

    /// <summary>The slot's value now, read through its getter.</summary>
    public object? Read() => MemberPathWalker.Guard(_read, _path, _segment, "");

    /// <summary>Assigns <paramref name="value"/>, already of <see cref="Type"/>.</summary>
    public void Write(object? value) =>
        MemberPathWalker.Guard(
            () =>
            {
                _write(value);
                return null;
            },
            _path,
            _segment,
            "setting "
        );

    /// <summary>Converts <paramref name="json"/> to <see cref="Type"/>; a failure names the path and the type.</summary>
    public object? Convert(JsonNode? json, IValueResolver resolver)
    {
        try
        {
            return ValueReader.Read(json, Type, resolver);
        }
        catch (ValueConversionException e)
        {
            throw new MemberPathException($"cannot set '{_path}' ({TypeNames.Format(Type)}): {e.Message}", e);
        }
    }
}
