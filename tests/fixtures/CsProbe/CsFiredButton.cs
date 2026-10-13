using Godot;

namespace CsProbe;

/// <summary>
/// The Button the C# fired-list tests click: its own [Signal] Played, raised by the handler it puts on the engine's
/// Pressed, and the same signal subscribed with +=, which Godot cannot see. The plays meta counts the Played handler's
/// runs, so GDScript reads one back; the [Signal]'s += handlers live on a generated backing delegate, invisible to Godot.
/// </summary>
public partial class CsFiredButton : Button
{
    [Signal]
    public delegate void PlayedEventHandler();

    private int _plays;

    public override void _Ready()
    {
        Pressed += OnPressed;
        Played += OnPlayed;
    }

    private void OnPressed() => EmitSignal(SignalName.Played);

    private void OnPlayed()
    {
        _plays++;
        SetMeta("plays", _plays);
    }
}
