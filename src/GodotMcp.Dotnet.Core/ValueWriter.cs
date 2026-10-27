using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// Converts a CLR value to JSON: primitives as JSON, enums by name, dates in ISO 8601, collections and lazy sequences as arrays,
/// dictionaries as objects, and other objects as their public instance properties then fields. Cycles, depth, a lazy sequence past
/// <see cref="LazyItemLimit"/> items and an enumeration that throws, or whose dispose throws, are cut with a marker string; no other
/// length is cut here, and a count of values only by <see cref="WriteBounded"/>.
/// </summary>
public static class ValueWriter
{
    /// <summary>
    /// What <see cref="WriteBounded"/> writes in place of the first value past its budget, which ends that collection's walk, as the
    /// bridge's state reader does.
    /// </summary>
    public const string SizeLimit = "<size limit>";

    /// <summary>
    /// The most items of a lazy sequence one write gives, whether or not a value budget is set: past it the array ends at one
    /// <see cref="SizeLimit"/> and no further item is read, since enumerating a lazy sequence runs game code and may not end.
    /// </summary>
    public const int LazyItemLimit = 1000;

    /// <summary>
    /// The budget of the <see cref="WriteBounded"/> in progress on this thread, which every write it makes shares, including
    /// the ones a formatter starts for the values it holds (a Godot variant's content, a Godot dictionary's entries).
    /// </summary>
    [ThreadStatic]
    private static ValueBudget? _threadBudget;

    /// <summary>
    /// Writes <paramref name="value"/>. <paramref name="formatter"/> is asked first on every value. An object or collection
    /// nested deeper than <paramref name="maxDepth"/> levels (the root is level 1) writes <c>"&lt;depth limit: Type&gt;"</c>.
    /// </summary>
    public static JsonNode? Write(object? value, IValueFormatter formatter, int maxDepth = 8)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        return new ValueWalk(formatter, maxDepth, _threadBudget ?? ValueBudget.Unbounded()).Write(value, 0);
    }

    /// <summary>
    /// Writes <paramref name="value"/> as <see cref="Write"/> does, taking one of <paramref name="maxValues"/> for each value
    /// written, a null, a collection and a marker included. Once none is left the walk stops: the collection whose element was cut
    /// ends with one <see cref="SizeLimit"/>, its remaining elements are not visited, and each collection around it ends the same way.
    /// </summary>
    public static JsonNode? WriteBounded(object? value, IValueFormatter formatter, int maxDepth, int maxValues)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        ValueBudget? outer = _threadBudget;
        _threadBudget = new ValueBudget(maxValues);
        try
        {
            return Write(value, formatter, maxDepth);
        }
        finally
        {
            _threadBudget = outer;
        }
    }

    /// <summary>
    /// Gives back the one value the <see cref="WriteBounded"/> in progress on this thread just took, for a formatter that writes a
    /// value as a wrapper whose content stands in its place, as a Godot variant does; nothing outside such a write.
    /// </summary>
    internal static void RefundOneValue() => _threadBudget?.Refund();

    /// <summary>Whether the <see cref="WriteBounded"/> in progress on this thread has spent its values; false outside one.</summary>
    internal static bool BudgetSpent => _threadBudget?.Spent ?? false;
}

/// <summary>How many more values a bounded write may take.</summary>
internal sealed class ValueBudget(int left)
{
    private readonly int _most = left;

    private int _left = left;

    public static ValueBudget Unbounded() => new(int.MaxValue);

    /// <summary>Whether no value is left: a collection whose walk meets it ends with its one marker.</summary>
    public bool Spent => _left <= 0;

    /// <summary>Takes one value; false when none was left.</summary>
    public bool Take()
    {
        if (_left <= 0)
        {
            return false;
        }

        _left--;
        return true;
    }

    /// <summary>
    /// Gives back one value <see cref="Take"/> just took, never above the budget it started with: a formatter that writes a value
    /// as a wrapper hands that value's content back to the writer, which takes for the content in the wrapper's place.
    /// </summary>
    public void Refund()
    {
        if (_left < _most)
        {
            _left++;
        }
    }
}

/// <summary>One write: the objects on the current path, for cutting cycles, and the budget of values it shares.</summary>
internal sealed class ValueWalk(IValueFormatter formatter, int maxDepth, ValueBudget budget)
{
    private static readonly Dictionary<Type, Func<object, JsonNode>> Scalars = new()
    {
        [typeof(string)] = value => JsonValue.Create((string)value),
        [typeof(bool)] = value => JsonValue.Create((bool)value),
        [typeof(char)] = value => JsonValue.Create(((char)value).ToString()),
        [typeof(sbyte)] = value => JsonValue.Create((sbyte)value),
        [typeof(byte)] = value => JsonValue.Create((byte)value),
        [typeof(short)] = value => JsonValue.Create((short)value),
        [typeof(ushort)] = value => JsonValue.Create((ushort)value),
        [typeof(int)] = value => JsonValue.Create((int)value),
        [typeof(uint)] = value => JsonValue.Create((uint)value),
        [typeof(long)] = value => JsonValue.Create((long)value),
        [typeof(ulong)] = value => JsonValue.Create((ulong)value),
        [typeof(nint)] = value => JsonValue.Create((long)(nint)value),
        [typeof(nuint)] = value => JsonValue.Create((ulong)(nuint)value),
        [typeof(float)] = value =>
            float.IsFinite((float)value) ? JsonValue.Create((float)value) : JsonValue.Create(((float)value).ToString(CultureInfo.InvariantCulture)),
        [typeof(double)] = value =>
            double.IsFinite((double)value)
                ? JsonValue.Create((double)value)
                : JsonValue.Create(((double)value).ToString(CultureInfo.InvariantCulture)),
        [typeof(decimal)] = value => JsonValue.Create((decimal)value),
        [typeof(Guid)] = value => JsonValue.Create(((Guid)value).ToString("D", CultureInfo.InvariantCulture)),
        [typeof(DateTime)] = value => JsonValue.Create(((DateTime)value).ToString("O", CultureInfo.InvariantCulture)),
        [typeof(DateTimeOffset)] = value => JsonValue.Create(((DateTimeOffset)value).ToString("O", CultureInfo.InvariantCulture)),
        [typeof(TimeSpan)] = value => JsonValue.Create(((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture)),
    };

    private readonly HashSet<object> _onPath = new(ReferenceEqualityComparer.Instance);

    public JsonNode? Write(object? value, int depth)
    {
        if (!budget.Take())
        {
            return JsonValue.Create(ValueWriter.SizeLimit);
        }
        if (value is null)
        {
            return null;
        }
        if (formatter.TryFormat(value, out JsonNode? formatted))
        {
            return formatted;
        }
        if (Scalars.TryGetValue(value.GetType(), out Func<object, JsonNode>? scalar))
        {
            return scalar(value);
        }
        if (value is Enum)
        {
            return JsonValue.Create(value.ToString());
        }
        return IsOpaque(value) ? Marker(OpaqueName(value)) : WriteComposite(value, depth);
    }

    private static JsonValue Marker(string text) => JsonValue.Create($"<{text}>");

    private static bool IsOpaque(object value) => value is Delegate or Pointer or Type or Task;

    private static string OpaqueName(object value)
    {
        if (value is Type)
        {
            return nameof(Type);
        }
        Type type = value.GetType();
        if (value is Task)
        {
            while (type != typeof(Task) && !IsGenericTask(type))
            {
                type = type.BaseType!;
            }
        }
        return TypeNames.Format(type);
    }

    private static bool IsGenericTask(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>);

    private JsonNode WriteComposite(object value, int depth)
    {
        Type type = value.GetType();
        bool tracked = !type.IsValueType;
        if (tracked && _onPath.Contains(value))
        {
            return Marker($"cycle: {TypeNames.Format(type)}");
        }
        if (depth >= maxDepth)
        {
            return Marker($"depth limit: {TypeNames.Format(type)}");
        }
        if (tracked)
        {
            _onPath.Add(value);
        }
        try
        {
            return WriteShape(value, depth + 1);
        }
        finally
        {
            _onPath.Remove(value);
        }
    }

    private JsonNode WriteShape(object value, int childDepth)
    {
        if (value is IDictionary dictionary)
        {
            return WriteDictionary(dictionary, childDepth);
        }
        return value is IEnumerable items ? WriteCollection(items, childDepth) : WriteObject(value, childDepth);
    }

    /// <summary>
    /// The items in an array, a lazy sequence's enumerated like a list's: the array ends at one <see cref="ValueWriter.SizeLimit"/>
    /// once the budget is spent or <see cref="ValueWriter.LazyItemLimit"/> items of a lazy sequence are written, the items after it
    /// not visited, and an enumeration that throws, or whose dispose throws after the items are read, ends it with one
    /// <c>&lt;threw ...&gt;</c> item.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a game's sequence throws while it is enumerated is that value's one marker, not the whole write's failure."
    )]
    private JsonArray WriteCollection(IEnumerable items, int childDepth)
    {
        JsonArray array = [];
        IEnumerator enumerator;
        try
        {
            enumerator = items.GetEnumerator();
        }
        catch (Exception e)
        {
            array.Add(ThrewItem(e));
            return array;
        }
        bool collected = false;
        try
        {
            Collect(enumerator, array, ItemLimit(items), childDepth);
            collected = true;
        }
        finally
        {
            DisposeInto(enumerator, collected ? array : null);
        }
        return array;
    }

    /// <summary>
    /// The items into <paramref name="array"/> until the sequence ends, a <see cref="ValueWriter.SizeLimit"/> takes the place of the
    /// item past the budget or the cap, or the enumeration's own <c>&lt;threw ...&gt;</c> item follows the items read so far.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a game's sequence throws while it is enumerated is that value's one marker, not the whole write's failure."
    )]
    private void Collect(IEnumerator enumerator, JsonArray array, int most, int childDepth)
    {
        while (true)
        {
            object? item;
            try
            {
                if (!enumerator.MoveNext())
                {
                    return;
                }
                item = enumerator.Current;
            }
            catch (Exception e)
            {
                array.Add(ThrewItem(e));
                return;
            }
            if (budget.Spent || array.Count >= most)
            {
                array.Add(JsonValue.Create(ValueWriter.SizeLimit));
                return;
            }
            array.Add(Write(item, childDepth));
        }
    }

    /// <summary>
    /// Disposes <paramref name="enumerator"/>, a throw from it becoming the sequence's marker in <paramref name="array"/>; with no
    /// array there, the item's own write is already failing and stands, a dispose throw having nothing to be written into.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A sequence's dispose throw is that value's one marker, or is dropped when the item's own write is already failing."
    )]
    private static void DisposeInto(IEnumerator enumerator, JsonArray? array)
    {
        try
        {
            (enumerator as IDisposable)?.Dispose();
        }
        catch (Exception e) when (array is not null)
        {
            RecordThrow(array, e);
        }
        catch (Exception)
        {
            // The item's own write threw and is the write's failure; a dispose throw here has no array left to be written into.
        }
    }

    /// <summary>
    /// Puts a sequence's <c>&lt;threw ...&gt;</c> item in its place: over a <see cref="ValueWriter.SizeLimit"/> that ends the array,
    /// after the items written otherwise, and never beside a throw the enumeration itself already recorded.
    /// </summary>
    private static void RecordThrow(JsonArray array, Exception failure)
    {
        if (array.Count > 0 && Text(array[^1]) == ValueWriter.SizeLimit)
        {
            array[^1] = ThrewItem(failure);
        }
        else if (array.Count == 0 || !IsThrow(Text(array[^1])))
        {
            array.Add(ThrewItem(failure));
        }
    }

    /// <summary>The text <paramref name="item"/> holds, or null for an item that is no string.</summary>
    private static string? Text(JsonNode? item) => item is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    /// <summary>Whether <paramref name="text"/> is the start of a <c>&lt;threw ...&gt;</c> item.</summary>
    private static bool IsThrow(string? text) => text?.StartsWith("<threw ", StringComparison.Ordinal) == true;

    /// <summary>
    /// The most items <paramref name="items"/> is written with: <see cref="ValueWriter.LazyItemLimit"/> for a lazy sequence, whose
    /// enumeration runs game code and may not end, and none for a materialised collection, which only the budget cuts.
    /// </summary>
    private static int ItemLimit(IEnumerable items) => IsMaterialised(items) ? int.MaxValue : ValueWriter.LazyItemLimit;

    /// <summary>
    /// Whether <paramref name="value"/> holds its items already, rather than producing them as they are read: LINQ's own iterators
    /// answer a count while still enumerating lazily, so none of that assembly's types is materialised.
    /// </summary>
    private static bool IsMaterialised(object value) => !IsLinqIterator(value) && (value is ICollection || CountsItself(value));

    /// <summary>Whether the value's type is one System.Linq declares, its own iterators included.</summary>
    private static bool IsLinqIterator(object value) => value.GetType().Assembly == typeof(Enumerable).Assembly;

    /// <summary>Whether the value's type declares itself a collection by its generic interfaces.</summary>
    private static bool CountsItself(object value) =>
        value
            .GetType()
            .GetInterfaces()
            .Any(face =>
                face.IsGenericType
                && (face.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>) || face.GetGenericTypeDefinition() == typeof(ICollection<>))
            );

    private JsonNode WriteDictionary(IDictionary dictionary, int childDepth)
    {
        List<DictionaryEntry> entries = [];
        IDictionaryEnumerator enumerator = dictionary.GetEnumerator();
        while (enumerator.MoveNext())
        {
            entries.Add(enumerator.Entry);
        }
        if (entries.TrueForAll(entry => IsStringLike(entry.Key)))
        {
            return WriteNamedEntries(entries, childDepth);
        }
        return WritePairs(entries, childDepth);
    }

    /// <summary>The entries under their keys' text, the first key past the budget mapped to one marker and the keys after it left out.</summary>
    private JsonObject WriteNamedEntries(List<DictionaryEntry> entries, int childDepth)
    {
        JsonObject map = [];
        foreach (DictionaryEntry entry in entries)
        {
            string key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture)!;
            if (budget.Spent)
            {
                map[key] = JsonValue.Create(ValueWriter.SizeLimit);
                break;
            }
            map[key] = Write(entry.Value, childDepth);
        }
        return map;
    }

    /// <summary>
    /// The entries as key/value pairs, the pair whose key takes the last value carrying the marker, and the keys after it left out.
    /// </summary>
    private JsonArray WritePairs(List<DictionaryEntry> entries, int childDepth)
    {
        JsonArray pairs = [];
        foreach (DictionaryEntry entry in entries)
        {
            if (budget.Spent)
            {
                pairs.Add(EndPair(entry.Key));
                break;
            }
            JsonNode? key = Write(entry.Key, childDepth);
            if (budget.Spent)
            {
                pairs.Add(new JsonObject { ["key"] = key, ["value"] = JsonValue.Create(ValueWriter.SizeLimit) });
                break;
            }
            pairs.Add(new JsonObject { ["key"] = key, ["value"] = Write(entry.Value, childDepth) });
        }
        return pairs;
    }

    /// <summary>The pair that ends a pairs list: the key's own text, and the marker its value would have been.</summary>
    private static JsonObject EndPair(object key) =>
        new()
        {
            ["key"] = JsonValue.Create(Convert.ToString(key, CultureInfo.InvariantCulture)),
            ["value"] = JsonValue.Create(ValueWriter.SizeLimit),
        };

    private static bool IsStringLike(object key) => key is string or Enum or Guid or decimal || key.GetType().IsPrimitive;

    private JsonObject WriteObject(object value, int childDepth)
    {
        JsonObject map = [];
        List<Type> hierarchy = [];
        for (Type? type = value.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            hierarchy.Insert(0, type);
        }
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        IEnumerable<PropertyInfo> properties = hierarchy.SelectMany(type =>
            type.GetProperties(Declared)
                .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.MetadataToken)
        );
        foreach (PropertyInfo property in properties)
        {
            if (budget.Spent)
            {
                map[property.Name] = JsonValue.Create(ValueWriter.SizeLimit);
                return map;
            }
            map[property.Name] = ReadProperty(value, property, childDepth);
        }
        foreach (FieldInfo field in hierarchy.SelectMany(type => type.GetFields(Declared).OrderBy(f => f.MetadataToken)))
        {
            if (budget.Spent)
            {
                map[field.Name] = JsonValue.Create(ValueWriter.SizeLimit);
                return map;
            }
            map[field.Name] = Write(field.GetValue(value), childDepth);
        }
        return map;
    }

    private JsonNode? ReadProperty(object value, PropertyInfo property, int childDepth)
    {
        if (property.PropertyType.IsByRefLike)
        {
            return Marker(TypeNames.Format(property.PropertyType));
        }
        try
        {
            return Write(property.GetValue(value), childDepth);
        }
        catch (TargetInvocationException e) when (e.InnerException is Exception inner)
        {
            return ThrewItem(inner);
        }
    }

    /// <summary>The marker for a value whose read threw: the exception's name and message.</summary>
    private static JsonValue ThrewItem(Exception failure) => JsonValue.Create($"<threw {failure.GetType().Name}: {failure.Message}>");
}
