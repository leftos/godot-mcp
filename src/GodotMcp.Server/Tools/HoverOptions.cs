using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>
/// What hover waits for after its move: the tooltip Godot shows for the pointer (the hovered Control's, or the nearest
/// ancestor's up to a Stop-filter or top-level Control), and for how long.
/// </summary>
internal sealed record HoverOptions(
    [property: Description(
        "Wait for the tooltip Godot shows for the pointer to show (the default): the hovered Control's, or the nearest ancestor's "
            + "up to a Stop-filter or top-level Control, named in tooltip.owner; false answers right after the move."
    )]
        bool Tooltip = true,
    [property: Description(
        "How long to wait for the tooltip, 0 to 10000 ms; gui/timers/tooltip_delay_sec plus 1000 ms (at most 10000) when left out. "
            + "In a recording, clip time: 60 movie frames a second."
    )]
        int? TimeoutMs = null
);
