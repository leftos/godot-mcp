using Godot;

namespace CsProbe;

/// <summary>The node call_method's C# tests call (one public method and one internal), with the scene root's game tool.</summary>
public partial class CsProbeNode : Node
{
    public int PlayStep(int n) => n + 1;

    internal string Secret() => "hidden";

    // A member starting with a keyword follows Secret, so the red-build tests' missing ';' there stays CS1002 on line 10.
    internal int Steps { get; private set; }

    [GodotMcpTool("Advances the probe by a number of steps and answers the steps taken.")]
    public int Advance(int steps = 1) => Steps += steps;
}
