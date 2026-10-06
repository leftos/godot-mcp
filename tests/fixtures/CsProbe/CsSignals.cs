using Godot;

namespace CsProbe;

/// <summary>The node watch's C# signal tests add: [Signal]s of 0, 3 and 6 arguments, which EmitAll emits once each.</summary>
public partial class CsSignals : Node
{
    [Signal]
    public delegate void PingedEventHandler();

    [Signal]
    public delegate void HitEventHandler(int damage, string by, Vector2 at);

    [Signal]
    public delegate void DealtEventHandler(int seat, float weight, string card, Vector2 at, Node holder, Godot.Collections.Array tags);

    /// <summary>Emits Pinged, Hit and Dealt once each, in that order.</summary>
    public void EmitAll()
    {
        EmitSignal(SignalName.Pinged);
        EmitSignal(SignalName.Hit, 3, "sam", new Vector2(1, 2));
        EmitSignal(SignalName.Dealt, 2, 0.5f, "ace", new Vector2(3, 4), this, new Godot.Collections.Array { 1, "two" });
    }
}
