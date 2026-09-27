using System.Globalization;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// The objects an agent asked to keep, by id <c>h&lt;epoch&gt;.&lt;n&gt;</c>: <c>n</c> counts from 1 and is never reused, and the
/// epoch changes with each game run, so an id from before a restart is recognised as such. Past
/// <paramref name="capacity"/> entries, the least recently used is dropped.
/// </summary>
public sealed class HandleTable(int epoch, int capacity = 256)
{
    private readonly int _epoch = epoch >= 0 ? epoch : throw new ArgumentOutOfRangeException(nameof(epoch), epoch, "An epoch is not negative.");

    private readonly int _capacity =
        capacity >= 1 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "A handle table keeps at least one handle.");

    private readonly Dictionary<long, LinkedListNode<Entry>> _entries = [];

    private readonly LinkedList<Entry> _recency = new();

    private long _issued;

    public string Add(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        long number = ++_issued;
        _entries[number] = _recency.AddFirst(new Entry(number, value));
        if (_entries.Count > _capacity)
        {
            LinkedListNode<Entry> oldest = _recency.Last!;
            _recency.RemoveLast();
            _entries.Remove(oldest.Value.Number);
        }
        return $"h{_epoch}.{number}";
    }

    /// <summary>The value <paramref name="id"/> stands for; it becomes the most recently used.</summary>
    public object Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        (int idEpoch, long number) = Parse(id);
        if (idEpoch != _epoch)
        {
            throw new HandleException($"handle {id} was dropped when the game restarted");
        }
        if (!_entries.TryGetValue(number, out LinkedListNode<Entry>? node))
        {
            throw new HandleException($"handle {id} is gone: at most {_capacity} handles are kept, least recently used dropped first");
        }
        _recency.Remove(node);
        _recency.AddFirst(node);
        return node.Value.Value;
    }

    private static (int Epoch, long Number) Parse(string id)
    {
        int dot = id.IndexOf('.', StringComparison.Ordinal);
        if (
            id.StartsWith('h')
            && dot > 1
            && int.TryParse(id.AsSpan(1, dot - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int idEpoch)
            && long.TryParse(id.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long number)
        )
        {
            return (idEpoch, number);
        }
        throw new HandleException($"'{id}' is not a handle id: expected h<epoch>.<number>");
    }

    private sealed record Entry(long Number, object Value);
}
