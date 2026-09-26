using Godot;

namespace CsProbe;

/// <summary>The node call_method's C# tests call: one public method and one internal.</summary>
public partial class CsProbeNode : Node
{
    public int PlayStep(int n) => n + 1;

    internal string Secret() => "hidden";
}
