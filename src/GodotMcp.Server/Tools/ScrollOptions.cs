using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>
/// How far each scroll notch goes, and whether it plays as a mouse wheel's button events or a trackpad's pan gesture.
/// </summary>
internal sealed record ScrollOptions(
    [property: Description(
        "How far each notch scrolls: a wheel event's factor, or a pan gesture's delta length. More than 0, at most 10; 1 (the "
            + "default) is one notch of a standard wheel, which moves a ScrollContainer an eighth of its page."
    )]
        double Factor = 1,
    [property: Description(
        "wheel (the default): each notch a press and a release of the wheel button, as a mouse sends it; pan: each notch one "
            + "InputEventPanGesture, as a trackpad sends it."
    )]
        string Via = "wheel"
);
