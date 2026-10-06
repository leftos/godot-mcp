#nullable enable

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;

namespace CsProbe;

/// <summary>The tool mark list_game_tools finds by its name, declared as a game declares it.</summary>
[Conditional("DEBUG")]
[AttributeUsage(AttributeTargets.Method)]
internal sealed class GodotMcpToolAttribute(string description) : Attribute
{
    public string Description { get; } = description;

    public string? Name { get; init; }

    public string? When { get; init; }

    public bool ReadOnly { get; init; }
}

/// <summary>The base type of the Tools autoload, whose marked method lists on the autoload it derives into.</summary>
public abstract partial class CsToolsBase : Node
{
    [GodotMcpTool("Answers a greeting from the autoload's base type.")]
    internal string Greet(string who) => "hello " + who;
}

/// <summary>The node the game tools' tests make the Tools autoload, whose marked methods they list on it.</summary>
public partial class CsTools : CsToolsBase
{
    [GodotMcpTool(null!)]
    internal void Blank() { }

    [GodotMcpTool("Heals the probe by an amount and answers the amount.")]
    public int Heal(int amount) => amount;

    [GodotMcpTool("Answers a label after a short wait.")]
    internal async Task<string> FetchLater(string label)
    {
        await Task.Delay(10);
        return "later " + label;
    }

    [GodotMcpTool("Answers after waiting a number of milliseconds.")]
    internal async Task<string> Dawdle(int ms)
    {
        await Task.Delay(ms);
        return "done";
    }

    [GodotMcpTool("Always throws.")]
    internal void Boom() => throw new InvalidOperationException("tool failure");

    [GodotMcpTool("Logs an engine error, then answers one.")]
    internal int Complain()
    {
        GD.PushError("CsTools complained");
        return 1;
    }

    [GodotMcpTool("Sets the probe's mood a number of times.", When = "any time", ReadOnly = true)]
    internal string SetMood([Description("How the probe feels.")] Mood mood, int times = 2, string note = "calm") => $"{mood} x{times} ({note})";

    [GodotMcpTool("Looks a code up.")]
    internal bool TryFind(int code, out int found)
    {
        found = code;
        return code > 0;
    }

    [GodotMcpTool("One of the two tools named Twin.")]
    internal string Twin() => "autoload twin";
}

/// <summary>The game tools the tests list as static methods.</summary>
public static class CsStatics
{
    [GodotMcpTool("Adds two numbers.")]
    public static int Sum(int a, int b) => a + b;

    [GodotMcpTool("Answers a long a double cannot hold exactly.")]
    public static long Huge() => 9_007_199_254_740_993;

    [GodotMcpTool("The other tool named Twin.", Name = "Twin")]
    internal static string Mirror() => "static twin";
}

/// <summary>A type no autoload, scene root or static method reaches, so its tool is listed as unavailable.</summary>
public sealed class CsStray
{
    [GodotMcpTool("Cannot run: nothing owns it.")]
    internal string Wander() => "nowhere";
}
