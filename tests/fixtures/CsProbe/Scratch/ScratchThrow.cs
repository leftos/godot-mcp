using System;
using Godot;

namespace CsProbe;

/// <summary>A scratch scene in C# whose second step throws: run_scratches reports it red with the exception among the step's errors.</summary>
public partial class ScratchThrow : Node
{
    private static readonly string[] Names = ["calm", "throws"];
    private string _current = "";
    private string _note = "";

    public int GetStepCount() => Names.Length;

    public string GetStepName(int index) => Names[index];

    public void PlayStep(int index)
    {
        _current = Names[index];
        _note = index == 0 ? "calm" : "about to throw";
        if (index == 1)
        {
            throw new InvalidOperationException("scratch step threw");
        }
    }

    public string GetStatus() => $"{_current}\n{_note}";
}
