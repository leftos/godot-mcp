using System.Text.Json.Nodes;

namespace GodotMcp.Server.Session;

/// <summary>
/// A captured subtree of the running game: the absolute path of the node it starts from, what it was captured with, and its
/// nodes, keyed by path from that node ("." for itself), each an object of property values and its groups.
/// </summary>
internal sealed record Snapshot(string Node, IReadOnlyList<string>? Properties, IReadOnlyList<string>? Ignore, int MaxNodes, JsonObject Nodes);

/// <summary>
/// A session's snapshots, the <see cref="Capacity"/> most recently used, under ids s1, s2, … that never repeat within the
/// session, so an id from before a stop or a restart is refused rather than naming a newer snapshot.
/// </summary>
internal sealed class SnapshotStore
{
    public const int Capacity = 16;

    private readonly Lock _lock = new();

    // Most recently used first; the last is the one evicted.
    private readonly LinkedList<KeyValuePair<string, Snapshot>> _held = [];
    private long _lastId;

    /// <summary>How many snapshots are held.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _held.Count;
            }
        }
    }

    /// <summary>Holds a snapshot as the most recently used, evicting the least recently used past <see cref="Capacity"/>.</summary>
    /// <returns>The snapshot's id.</returns>
    public string Add(Snapshot snapshot)
    {
        lock (_lock)
        {
            string id = $"s{++_lastId}";
            _held.AddFirst(new KeyValuePair<string, Snapshot>(id, snapshot));
            if (_held.Count > Capacity)
            {
                _held.RemoveLast();
            }

            return id;
        }
    }

    /// <summary>The snapshot with this id, marked the most recently used; null when it is not held.</summary>
    public Snapshot? Find(string id)
    {
        lock (_lock)
        {
            for (LinkedListNode<KeyValuePair<string, Snapshot>>? entry = _held.First; entry is not null; entry = entry.Next)
            {
                if (entry.Value.Key == id)
                {
                    _held.Remove(entry);
                    _held.AddFirst(entry);
                    return entry.Value.Value;
                }
            }

            return null;
        }
    }

    /// <summary>Drops every snapshot; the ids keep counting.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _held.Clear();
        }
    }
}
