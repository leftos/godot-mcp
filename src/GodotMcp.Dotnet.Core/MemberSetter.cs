using System.Globalization;
using System.Text.Json;
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

    /// <summary>How far apart a number written and one read back may be and still be the same value: Godot's is_equal_approx.</summary>
    private const double Tolerance = 1e-5;

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
        if (!SameRead(writtenJson, afterJson))
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
    private static string Describe(MemberPathException failure) => Thrown.Describe(Cause(failure));

    /// <summary>The stack of what a getter or setter threw, on lines of its own, or nothing when it has none.</summary>
    private static string Stack(MemberPathException failure) => Thrown.Stack(Cause(failure));

    /// <summary>The exception the game code threw: the guard wraps it, and reflection wraps it once more.</summary>
    private static Exception Cause(MemberPathException failure) => Thrown.Unwrap(failure.InnerException ?? failure);

    private static string Show(JsonNode? json) => ValueReader.Show(json, ShownLength);

    /// <summary>
    /// Whether the JSON read back is the JSON written: a number within <see cref="Tolerance"/> of what was written (an
    /// integral pair only exactly), and objects and arrays by the same rule, key by key and item by item. A value a member holds
    /// in single precision or as a rotation reads back within a rounding step of the one written, which is not a refusal.
    /// </summary>
    private static bool SameRead(JsonNode? written, JsonNode? after)
    {
        if (written is null || after is null)
        {
            return written is null && after is null;
        }

        return (written, after) switch
        {
            (JsonObject writtenMap, JsonObject afterMap) => SameReadMap(writtenMap, afterMap),
            (JsonArray writtenList, JsonArray afterList) => SameReadList(writtenList, afterList),
            _ => SameScalar(written, after),
        };
    }

    private static bool SameReadMap(JsonObject written, JsonObject after)
    {
        if (written.Count != after.Count)
        {
            return false;
        }

        foreach ((string key, JsonNode? value) in written)
        {
            if (!after.TryGetPropertyValue(key, out JsonNode? read) || !SameRead(value, read))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameReadList(JsonArray written, JsonArray after)
    {
        if (written.Count != after.Count)
        {
            return false;
        }

        for (int index = 0; index < written.Count; index++)
        {
            if (!SameRead(written[index], after[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A pair of numbers by the tolerance rule, and anything else as <see cref="JsonNode.DeepEquals"/> compares it.</summary>
    private static bool SameScalar(JsonNode written, JsonNode after) =>
        ReadNumber(written, out double wanted) && ReadNumber(after, out double read)
            ? NumbersMatch(wanted, read)
            : JsonNode.DeepEquals(written, after);

    /// <summary>Whether a number took the value written, and whether it only differs by a rounding step: an integral pair only exactly.</summary>
    private static bool NumbersMatch(double written, double read) =>
        IsWhole(written) && IsWhole(read) ? written == read : Math.Abs(written - read) <= Math.Max(Tolerance * Math.Abs(written), Tolerance);

    private static bool IsWhole(double number) => Math.Floor(number) == number;

    /// <summary>
    /// The number a JSON value holds. The text is parsed rather than read as a double, since a float-backed value holds a single
    /// precision number that a double read would refuse.
    /// </summary>
    private static bool ReadNumber(JsonNode node, out double number)
    {
        number = 0;
        return node.GetValueKind() == JsonValueKind.Number
            && double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
}
