using Godot;

namespace CsProbe;

/// <summary>
/// A scratch scene in C# whose one step ends at a state until: a 0.2 s timer advances the turn its Turn child's private
/// _McpState reads, which only the C# helper can read, and its public GetStepUntil gives that state as the step's until.
/// </summary>
public partial class ScratchUntilState : Node
{
    private static readonly string[] Names = ["counts"];
    private readonly ScratchTurn _turn = new() { Name = "Turn" };
    private string _current = "";
    private string _note = "";

    public override void _Ready()
    {
        _turn.AddToGroup("mcp_state");
        AddChild(_turn);
    }

    public int GetStepCount() => Names.Length;

    public string GetStepName(int index) => Names[index];

    public void PlayStep(int index)
    {
        _current = Names[index];
        _note = "started";
        GetTree().CreateTimer(0.2).Timeout += () =>
        {
            _note = "counted";
            _turn.Turn = 1;
        };
    }

    public Godot.Collections.Dictionary GetStepUntil(int index) =>
        new()
        {
            ["node"] = _turn.GetPath().ToString(),
            ["state"] = "turn",
            ["equals"] = 1,
        };

    public string GetStatus() => $"{_current}\n{_note}";
}

/// <summary>The turn a scratch step advances, read through a private _McpState, which Godot's own call cannot see.</summary>
public partial class ScratchTurn : Node
{
    public int Turn { get; set; }

    private Godot.Collections.Dictionary _McpState() => new() { ["turn"] = Turn };
}
