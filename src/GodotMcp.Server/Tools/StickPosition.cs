using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>Where gamepad_stick pushes a stick: each axis from -1 to 1, as Godot's joypad axes read.</summary>
internal sealed record StickPosition(
    [property: Description("The stick's X axis, -1 (left) to 1 (right).")] double X,
    [property: Description("The stick's Y axis, -1 (up) to 1 (down), as Godot reads a stick.")] double Y
);
