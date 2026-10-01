using System.Text.Json.Nodes;

namespace GodotMcp.Server.Session;

/// <summary>What took a held snapshot, which decides how diff_snapshots takes it again and what it may be compared with.</summary>
internal enum SnapshotKind
{
    /// <summary>snapshot_subtree's capture: each node's inspector properties and its groups.</summary>
    Subtree,

    /// <summary>A get_game_state read kept with keep: each marked node's state flattened to one value per leaf.</summary>
    State,
}

/// <summary>
/// A held snapshot of the running game: its kind, what it was taken with, and its nodes, each an object of values. A subtree's
/// <see cref="Node"/> is the absolute path it starts from, its nodes keyed by path from it ("." for itself), each its property
/// values and groups, filtered by <see cref="Properties"/> and <see cref="Ignore"/>. A state read's <see cref="Node"/> is the
/// node as given (null: the whole tree), its nodes keyed by absolute path, each its state's leaves by dotted path
/// (<c>StateFlatten</c>), read with <see cref="Keys"/> and <see cref="MaxDepth"/>.
/// </summary>
internal sealed record Snapshot(SnapshotKind Kind, string? Node, int MaxNodes, JsonObject Nodes)
{
    /// <summary>A subtree's property names kept; null keeps every one.</summary>
    public IReadOnlyList<string>? Properties { get; init; }

    /// <summary>A subtree's property names left out; null leaves none out.</summary>
    public IReadOnlyList<string>? Ignore { get; init; }

    /// <summary>A state read's keys; null reads every key.</summary>
    public IReadOnlyList<string>? Keys { get; init; }

    /// <summary>A state read's levels of nested values written.</summary>
    public int MaxDepth { get; init; }
}

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
