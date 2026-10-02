using System;
using Godot;

namespace CsProbe;

/// <summary>A scratch scene in C# whose _Ready throws before any step plays, and whose one step is clean.</summary>
public partial class ScratchBootThrow : Node
{
    private static readonly string[] Names = ["calm"];
    private string _current = "";
    private string _note = "";

    public override void _Ready() => throw new InvalidOperationException("scratch boot threw");

    public int GetStepCount() => Names.Length;

    public string GetStepName(int index) => Names[index];

    public void PlayStep(int index)
    {
        _current = Names[index];
        _note = "calm";
    }

    public string GetStatus() => $"{_current}\n{_note}";
}
