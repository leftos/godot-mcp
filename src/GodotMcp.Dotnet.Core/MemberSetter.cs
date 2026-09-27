using System.Reflection;
using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// Sets a <see cref="MemberSlot"/> and reads it back, as <c>set_property</c> does: the JSON is converted by the slot's type,
/// the old value is read and written out before the set (a setter that copies into the object it holds would change it),
/// and a value that reads back as something other than what was written is put back. Every failure is a
/// <see cref="MemberPathException"/> saying what the member holds afterwards.
/// </summary>
public static class MemberSetter
{
    /// <summary>How much of a value a refusal quotes.</summary>
    internal const int ShownLength = 2000;

    /// <summary>
    /// Sets <paramref name="slot"/> from <paramref name="json"/> and returns <c>{member, before, after}</c>, each value written
    /// with <paramref name="formatter"/>.
    /// </summary>
    /// <exception cref="MemberPathException">
    /// The value does not convert, a getter or setter threw, or the member read back something else and was put back.
    /// </exception>
    public static JsonObject Set(MemberSlot slot, string member, JsonNode? json, IValueResolver resolver, IValueFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(slot);
        object? written = slot.Convert(json, resolver);
        object? before = slot.Read();
        JsonNode? beforeJson = ValueWriter.Write(before, formatter);
        slot.Write(written);
        JsonNode? writtenJson = ValueWriter.Write(written, formatter);
        JsonNode? afterJson = ValueWriter.Write(ReadBack(slot, member, before, beforeJson), formatter);
        if (!JsonNode.DeepEquals(writtenJson, afterJson))
        {
            string mismatch = $"'{member}' did not take the value: it read {Show(afterJson)} after the set";
            throw PutBack(slot, mismatch, before, beforeJson, resolver, formatter);
        }

        return new JsonObject
        {
            ["member"] = member,
            ["before"] = beforeJson,
            ["after"] = afterJson,
        };
    }

    /// <summary>The value read after the set; when the getter throws, the old value is put back and the refusal says so.</summary>
    private static object? ReadBack(MemberSlot slot, string member, object? before, JsonNode? beforeJson)
    {
        try
        {
            return slot.Read();
        }
        catch (MemberPathException read)
        {
            string failed = $"reading '{member}' back after the set threw {Describe(read)}";
            try
            {
                slot.Write(before);
            }
            catch (MemberPathException putBack)
            {
                throw new MemberPathException(
                    $"{failed}; putting {Show(beforeJson)} back threw {Describe(putBack)}.{Stack(read)}{Stack(putBack)}",
                    read
                );
            }

            throw new MemberPathException($"{failed}; {Show(beforeJson)} was put back.{Stack(read)}", read);
        }
    }

    /// <summary>
    /// Puts the old value back after a set that did not take, and returns the refusal: <paramref name="mismatch"/> followed by
    /// what the member holds now.
    /// </summary>
    private static MemberPathException PutBack(
        MemberSlot slot,
        string mismatch,
        object? before,
        JsonNode? beforeJson,
        IValueResolver resolver,
        IValueFormatter formatter
    )
    {
        try
        {
            if (!Restore(slot, before, beforeJson, resolver, formatter))
            {
                return new MemberPathException(
                    $"{mismatch}; putting {Show(beforeJson)} back did not restore it, so it now reads {Current(slot, formatter)}."
                );
            }
        }
        catch (MemberPathException putBack)
        {
            return new MemberPathException(
                $"{mismatch}; putting {Show(beforeJson)} back threw {Describe(putBack)}, so it now reads {Current(slot, formatter)}.{Stack(putBack)}",
                putBack
            );
        }

        return new MemberPathException($"{mismatch}, so it was put back to {Show(beforeJson)}.");
    }

    /// <summary>
    /// Writes <paramref name="before"/> back, which keeps its identity for a setter that stores what it is given. A setter that
    /// copies into the object it holds would copy that object into itself, so when the member then reads other than
    /// <paramref name="beforeJson"/>, a fresh conversion of <paramref name="beforeJson"/> is written instead.
    /// </summary>
    /// <returns>Whether the member reads <paramref name="beforeJson"/> afterwards.</returns>
    private static bool Restore(MemberSlot slot, object? before, JsonNode? beforeJson, IValueResolver resolver, IValueFormatter formatter)
    {
        slot.Write(before);
        if (Reads(slot, beforeJson, formatter))
        {
            return true;
        }

        slot.Write(slot.Convert(beforeJson, resolver));
        return Reads(slot, beforeJson, formatter);
    }

    private static bool Reads(MemberSlot slot, JsonNode? expected, IValueFormatter formatter) =>
        JsonNode.DeepEquals(ValueWriter.Write(slot.Read(), formatter), expected);

    /// <summary>What the member reads now, or <c>unknown</c> when its getter throws.</summary>
    private static string Current(MemberSlot slot, IValueFormatter formatter)
    {
        try
        {
            return Show(ValueWriter.Write(slot.Read(), formatter));
        }
        catch (MemberPathException)
        {
            return "unknown";
        }
    }

    /// <summary><c>Type: message</c> of what a getter or setter threw.</summary>
    private static string Describe(MemberPathException failure)
    {
        Exception thrown = Thrown(failure);
        return $"{thrown.GetType().Name}: {thrown.Message}";
    }

    /// <summary>The stack of what a getter or setter threw, on lines of its own, or nothing when it has none.</summary>
    private static string Stack(MemberPathException failure) => Thrown(failure).StackTrace is { Length: > 0 } trace ? "\n" + trace : "";

    /// <summary>The exception the game code threw: the guard wraps it, and reflection wraps it once more.</summary>
    private static Exception Thrown(MemberPathException failure) =>
        failure.InnerException is TargetInvocationException { InnerException: { } inner } ? inner : failure.InnerException ?? failure;

    private static string Show(JsonNode? json) => ValueReader.Show(json, ShownLength);
}
