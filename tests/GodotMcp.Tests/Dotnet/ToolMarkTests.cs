using System.Reflection;
using GodotMcp.Dotnet.Core;
using GodotMcp.Tests.Dotnet.Marks;

namespace GodotMcp.Tests.Dotnet;

public sealed class ToolMarkTests
{
    [Fact]
    public void AnUnmarkedMethodCarriesNoMark() => Assert.Null(ToolMark.Read(Method(nameof(ToolBench.Flagged))));

    [Fact]
    public void AnUnsetNameTakesTheMethodNameAndTheRestTakeTheirDefaults()
    {
        ToolMark mark = ToolMark.Read(Method(nameof(ToolBench.Marked)))!;

        Assert.Equal("Marks nothing in particular.", mark.Description);
        Assert.Equal(nameof(ToolBench.Marked), mark.Name);
        Assert.Null(mark.When);
        Assert.False(mark.ReadOnly);
    }

    [Fact]
    public void ANamedMarkTakesItsNameAndReadOnly()
    {
        ToolMark mark = ToolMark.Read(Method(nameof(ToolBench.GotHp)))!;

        Assert.Equal("Reads a party member's hit points.", mark.Description);
        Assert.Equal("get_hp", mark.Name);
        Assert.Null(mark.When);
        Assert.True(mark.ReadOnly);
    }

    [Fact]
    public void AWhenMarkTakesItsWhen()
    {
        ToolMark mark = ToolMark.Read(Method(nameof(ToolBench.JumpToRoomOfKind)))!;

        Assert.Equal("Jumps the party into the first uncleared room of a kind.", mark.Description);
        Assert.Equal(nameof(ToolBench.JumpToRoomOfKind), mark.Name);
        Assert.Equal("from the map", mark.When);
        Assert.False(mark.ReadOnly);
    }

    [Fact]
    public void AMarkInAnotherNamespaceIsRead()
    {
        MethodInfo mark = typeof(MarkedBench).GetMethod(nameof(MarkedBench.Clear))!;

        ToolMark read = ToolMark.Read(mark)!;

        Assert.Equal("Clears the room of every enemy.", read.Description);
        Assert.Equal("clear_room", read.Name);
        Assert.Null(read.When);
        Assert.False(read.ReadOnly);
    }

    private static MethodInfo Method(string name) =>
        typeof(ToolBench).GetMethod(name) ?? throw new InvalidOperationException($"ToolBench has no {name}");
}
