using System.Text.Json.Nodes;
using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>run_scratches' step status from GetStatus's text, with no Godot.</summary>
public sealed class ScratchRunStatusTests
{
    [Fact]
    public void AOneLineStatusIsKeptWhole()
    {
        JsonNode? status = JsonValue.Create("barriers-where-planned: 1/2 checks passed");
        Assert.Equal("barriers-where-planned: 1/2 checks passed", ScratchRun.StatusLine(status));
    }

    [Fact]
    public void ATwoLineStatusKeepsItsSecondLine()
    {
        JsonNode? status = JsonValue.Create("barriers-where-planned\r\n1/2 checks passed");
        Assert.Equal("1/2 checks passed", ScratchRun.StatusLine(status));
    }

    [Fact]
    public void AnEmptyNoteStaysEmpty()
    {
        JsonNode? status = JsonValue.Create("barriers-where-planned\n");
        Assert.Equal(string.Empty, ScratchRun.StatusLine(status));
    }

    [Fact]
    public void AOneLineStatusLosesItsCarriageReturn()
    {
        JsonNode? status = JsonValue.Create("done\r");
        Assert.Equal("done", ScratchRun.StatusLine(status));
    }

    [Fact]
    public void AValueThatIsNotTextIsEmpty()
    {
        JsonNode? number = JsonValue.Create(3);
        Assert.Equal(string.Empty, ScratchRun.StatusLine(number));
        Assert.Equal(string.Empty, ScratchRun.StatusLine(null));
    }
}
