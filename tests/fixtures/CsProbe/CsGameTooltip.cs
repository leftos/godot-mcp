#nullable enable

using System.Collections.Generic;
using Godot;

namespace CsProbe;

/// <summary>
/// A node reporting a tooltip the game draws itself: in the mcp_tooltip group only (not mcp_state, so the state tests never
/// read it), its private _McpState answers a showing tooltip with its rect and a game field, locked.
/// </summary>
public partial class CsGameTooltip : Node
{
    public override void _Ready() => AddToGroup("mcp_tooltip");

    private Dictionary<string, object?> _McpState() =>
        new()
        {
            ["text"] = "Bribe",
            ["x"] = 10,
            ["y"] = 20,
            ["width"] = 80,
            ["height"] = 24,
            ["locked"] = true,
        };
}
