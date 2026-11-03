using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

/// <summary>
/// The step a name plays, as the helper reads it for call_game_tool: one match gives its index, and a name that matches
/// none or several is refused in the bridge's own words, with its list.
/// </summary>
public sealed class StepNamesTests
{
    private static readonly string[] Steps = ["open", "move", "close"];

    [Fact]
    public void ANameGivesItsIndex()
    {
        StepMatch match = StepNames.Index("move", Steps);

        Assert.Equal((int?)1, match.Index);
        Assert.Null(match.Refusal);
    }

    [Fact]
    public void ANameThatIsANumberIsStillAName()
    {
        StepMatch match = StepNames.Index("2", ["a", "2", "b"]);

        Assert.Equal((int?)1, match.Index);
        Assert.Null(match.Refusal);
    }

    [Fact]
    public void AnUnknownNameIsRefusedWithTheStepList()
    {
        StepMatch match = StepNames.Index("nope", Steps);

        Assert.Null(match.Index);
        Assert.Equal("""PlayStep "nope" matches no step; its steps are 0: open, 1: move, 2: close""", match.Refusal);
    }

    [Fact]
    public void ANameOnARootWithoutStepsIsRefused()
    {
        StepMatch match = StepNames.Index("x", []);

        Assert.Null(match.Index);
        Assert.Equal("""PlayStep "x" matches no step; it has no steps""", match.Refusal);
    }

    [Fact]
    public void ANameSeveralStepsShareIsRefusedWithTheirIndices()
    {
        StepMatch match = StepNames.Index("move", ["open", "move", "close", "move"]);

        Assert.Null(match.Index);
        Assert.Equal("""PlayStep "move" names 2 steps (1, 3); call it by index""", match.Refusal);
    }

    [Fact]
    public void ANameIsMatchedCaseSensitively()
    {
        StepMatch match = StepNames.Index("Move", Steps);

        Assert.Null(match.Index);
        Assert.Equal("""PlayStep "Move" matches no step; its steps are 0: open, 1: move, 2: close""", match.Refusal);
    }
}
