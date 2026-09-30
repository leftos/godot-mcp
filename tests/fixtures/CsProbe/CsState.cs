using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace CsProbe;

/// <summary>
/// The C# nodes get_game_state's tests read: added as this node's children when it enters the tree, each in the mcp_state
/// group, in this order: a private _McpState returning a record, an internal one returning a Godot Dictionary, one that
/// throws, one returning a Task, one with both _McpState and a Godot-visible _mcp_state, one with neither, one whose
/// call throws (a Span return), one with only a Godot-visible _mcp_state, and one returning the process frame it is read in.
/// </summary>
public partial class CsState : Node
{
    public override void _Ready()
    {
        Mark(new RecordState(), "Record");
        Mark(new DictionaryState(), "Dictionary");
        Mark(new ThrowingState(), "Throwing");
        Mark(new WaitingState(), "Waiting");
        Mark(new BothState(), "Both");
        Mark(new BareState(), "Bare");
        Mark(new BrokenState(), "Broken");
        Mark(new VisibleState(), "Visible");
        Mark(new FrameState(), "Frame");
    }

    private void Mark(Node node, string name)
    {
        node.Name = name;
        node.AddToGroup("mcp_state");
        AddChild(node);
    }
}

/// <summary>A seat as <see cref="Standing"/> lists it.</summary>
internal sealed record Seat(string Name, int Hp);

/// <summary>A record state: a value, a nested list of records and a node.</summary>
internal sealed record Standing(int Turn, List<Seat> Seats, Node Holder);

/// <summary>A private _McpState returning a record, which Godot's own call cannot marshal.</summary>
public partial class RecordState : Node
{
    private Standing _McpState() => new(3, [new Seat("alex", 7), new Seat("sam", 9)], GetParent());
}

/// <summary>An internal _McpState returning a Godot Dictionary.</summary>
public partial class DictionaryState : Node
{
    internal Godot.Collections.Dictionary _McpState() => new() { ["hp"] = 7, ["name"] = "dict" };
}

/// <summary>An _McpState that throws.</summary>
public partial class ThrowingState : Node
{
    private int _McpState() => throw new System.InvalidOperationException("state broke");
}

/// <summary>An _McpState returning a Task, which is never awaited.</summary>
public partial class WaitingState : Node
{
    private async Task<int> _McpState()
    {
        await Task.Delay(1);
        return 1;
    }
}

/// <summary>Both names: the helper reads _McpState, and _mcp_state, which Godot sees, is not read.</summary>
public partial class BothState : Node
{
    private Godot.Collections.Dictionary _McpState() => new() { ["from"] = "_McpState" };

    public Godot.Collections.Dictionary _mcp_state() => new() { ["from"] = "_mcp_state" };
}

/// <summary>In the group with neither state method.</summary>
public partial class BareState : Node { }

/// <summary>An _McpState returning a Span, which reflection cannot call: the call throws NotSupportedException.</summary>
public partial class BrokenState : Node
{
    private readonly int[] _items = [1, 2];

    private System.Span<int> _McpState() => _items;
}

/// <summary>Only a Godot-visible _mcp_state, no _McpState: read through Godot's call once the helper finds none.</summary>
public partial class VisibleState : Node
{
    public Godot.Collections.Dictionary _mcp_state() => new() { ["via"] = "_mcp_state", ["hp"] = 4 };
}

/// <summary>The process frame the read runs in, so a read that waits a frame shows as a frame past the result's.</summary>
public partial class FrameState : Node
{
    private ulong _McpState() => Engine.GetProcessFrames();
}
