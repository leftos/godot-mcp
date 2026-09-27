using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>What hover waits for after its move: the hovered Control's tooltip, and for how long.</summary>
internal sealed record HoverOptions(
    [property: Description("Wait for the hovered Control's tooltip to show (the default); false answers right after the move.")] bool Tooltip = true,
    [property: Description(
        "How long to wait for the tooltip, 0 to 10000 ms; gui/timers/tooltip_delay_sec plus 1000 ms (at most 10000) when left out."
    )]
        int? TimeoutMs = null
);
