using Godot;

namespace CsProbe;

/// <summary>
/// A scratch scene in C# whose one step ends when a 0.3 s timer emits its Done signal: its public GetStepUntil gives that signal
/// as the step's until, so run_scratches plays the step inside a wait on it rather than waiting the whole pace.
/// </summary>
public partial class ScratchUntil : Node
{
    private static readonly string[] Names = ["waits"];
    private string _current = "";
    private string _note = "";

    [Signal]
    public delegate void DoneEventHandler();

    public int GetStepCount() => Names.Length;

    public string GetStepName(int index) => Names[index];

    public void PlayStep(int index)
    {
        _current = Names[index];
        _note = "started";
        GetTree().CreateTimer(0.3).Timeout += () =>
        {
            _note = "ended";
            EmitSignal(SignalName.Done);
        };
    }

    public Godot.Collections.Dictionary GetStepUntil(int index) =>
        index < Names.Length ? new() { ["node"] = GetPath().ToString(), ["signal"] = SignalName.Done.ToString() } : [];

    public string GetStatus() => $"{_current}\n{_note}";
}
