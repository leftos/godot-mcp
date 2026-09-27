using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>How simulate_action plays its action: tap, press or release, and the press's strength.</summary>
internal sealed record ActionOptions(
    [property: Description("tap (the default: press, a frame, release), press or release.")] string Mode = "tap",
    [property: Description("The press's strength, 0 to 1; 1 when left out. A release's strength is always 0.")] double Strength = 1.0
);
