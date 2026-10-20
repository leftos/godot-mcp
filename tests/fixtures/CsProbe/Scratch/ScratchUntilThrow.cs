using System;
using Godot;

namespace CsProbe;

/// <summary>
/// A scratch scene in C# whose first step's PlayStep throws inside the wait on its until: run_scratches reports it red with the
/// exception's message, file and line, and stops the scene before its second step.
/// </summary>
public partial class ScratchUntilThrow : Node
{
    private static readonly string[] Names = ["throws", "after"];
    private string _current = "";
    private string _note = "";

    [Signal]
    public delegate void DoneEventHandler();

    public int GetStepCount() => Names.Length;

    public string GetStepName(int index) => Names[index];

    public void PlayStep(int index)
    {
        _current = Names[index];
        _note = index == 0 ? "about to throw" : "played after";
        if (index == 0)
        {
            throw new InvalidOperationException("scratch until step threw");
        }

        EmitSignal(SignalName.Done);
    }

    public Godot.Collections.Dictionary GetStepUntil(int index) => new() { ["node"] = GetPath().ToString(), ["signal"] = SignalName.Done.ToString() };

    public string GetStatus() => $"{_current}\n{_note}";
}
