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
        Assert.Null(read.Malformed);
    }

    [Theory]
    [InlineData(nameof(MalformedBench.Weighed))]
    [InlineData(nameof(MalformedBench.Blank))]
    public void AMarkWithNoDescriptionStringIsMalformedNamingTheMethod(string method)
    {
        ToolMark mark = ToolMark.Read(typeof(MalformedBench).GetMethod(method)!)!;

        Assert.Null(mark.Description);
        Assert.Equal(method, mark.Name);
        Assert.Equal(
            $"MalformedBench.{method} is marked GodotMcpTool, but its mark has no description string; give the attribute a "
                + "constructor taking the description.",
            mark.Malformed
        );
    }

    private static MethodInfo Method(string name) =>
        typeof(ToolBench).GetMethod(name) ?? throw new InvalidOperationException($"ToolBench has no {name}");

    /// <summary>A mark a game got wrong: its constructor takes something other than the description.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    private sealed class GodotMcpToolAttribute(object? weight) : Attribute
    {
        public object? Weight { get; } = weight;
    }

    /// <summary>Methods whose marks carry a number and a null where the description goes.</summary>
    private static class MalformedBench
    {
        [GodotMcpTool(3)]
        public static void Weighed() { }

        [GodotMcpTool(null)]
        public static void Blank() { }
    }
}
