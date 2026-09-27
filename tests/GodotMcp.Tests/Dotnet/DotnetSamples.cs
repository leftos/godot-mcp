using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json.Nodes;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

public enum Color
{
    Red,
    Green,
    Blue,
}

[Flags]
public enum Access
{
    None = 0,
    Read = 1,
    Write = 2,
}

public sealed record Choice(string Label, int Cost);

public sealed record Offer(string Title, Choice Best, ImmutableArray<Choice> Options);

public sealed class Settings
{
    public int Volume { get; set; }

    public string Name { get; init; } = "";

    public int Fixed { get; } = 1;
}

public sealed class Pair
{
    public Pair() => Made = "none";

    public Pair(int a)
    {
        A = a;
        Made = "a";
    }

    public Pair(int a, int b)
    {
        A = a;
        B = b;
        Made = "a,b";
    }

    public int A { get; }

    public int B { get; }

    public string Made { get; }
}

public interface IShield
{
    int Absorb(int amount);
}

public sealed class Shield : IShield
{
    public int Absorb(int amount) => amount / 2;
}

public sealed class Plain
{
    public int Weight { get; set; }
}

public sealed class Tally
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1051:Do not declare visible instance fields",
        Justification = "The writer's field rule needs a public field to read."
    )]
    public int Count;

    public int Twice => Count * 2;
}

public abstract class Weapon
{
    public abstract int Damage { get; }
}

public sealed class Link(int id)
{
    public int Id { get; } = id;

    public Link? Next { get; set; }
}

public sealed class Fragile
{
    private readonly int _base = 1;

    public int Before => _base;

    public int Broken => _base > 0 ? throw new InvalidOperationException("no") : _base;

    public int After => _base + 1;
}

public sealed class Holder
{
    private readonly int _secret = 42;

    public Holder? Child { get; set; }

    public List<Choice> Items { get; } = [];

    public Dictionary<string, int> Map { get; } = [];

    public string Broken => _secret > 0 ? throw new InvalidOperationException("no") : "";

    public int Reveal() => _secret;
}

public sealed class Fighter
{
    public int Health { get; private set; } = 10;

    public string Hit(int amount)
    {
        Health -= amount;
        return "int";
    }

    public string Hit(float amount)
    {
        Health -= (int)amount;
        return "float";
    }

    public int Heal(int amount, int bonus = 1) => Health += amount + bonus;

    public T Echo<T>(T value)
    {
        Health++;
        return value;
    }

    public int Guard(IShield shield) => Health += shield.Absorb(3);
}

public sealed class Duel
{
    private readonly int _seed = 7;

    public Duel() { }

    public Duel(int a, string b) => Note = b + a;

    public static string Label { get; } = "duel";

    public List<int> Values { get; set; } = [];

    public Color? PendingDecision { get; }

    public string? Note { get; set; }

    public string Name { get; init; } = "";

    public int Retries { get; private set; }

    public Dictionary<string, List<string?>> Tags { get; } = [];

    public int Hit(int amount) => _seed - amount;

    public void Bind(Choice update) => Retries += update.Cost;

    public Task<int> CountAsync() => Task.FromResult(_seed);

    public T Echo<T>(T value)
    {
        Retries++;
        return value;
    }

    public int Scale(int amount, int factor = 2) => amount * factor * _seed;

    private int Secret() => _seed + Retries;

    public int Peek() => Secret();
}

/// <summary>Resolves <c>h1.1</c> and the node <c>/root/Shield</c> to <see cref="Shield"/>, and <c>h1.2</c> to <see cref="Plain"/>.</summary>
public sealed class FakeResolver : IValueResolver
{
    public Shield Shield { get; } = new();

    public Plain Plain { get; } = new();

    public object? ResolveHandle(string id) =>
        id switch
        {
            "h1.1" => Shield,
            "h1.2" => Plain,
            _ => throw new HandleException($"handle {id} is gone"),
        };

    public object? ResolveNode(string path) => path == "/root/Shield" ? Shield : null;
}

/// <summary>Formats every <see cref="Choice"/> as the string <c>opt</c> and leaves the rest to the writer.</summary>
public sealed class OptionFormatter : IValueFormatter
{
    public bool TryFormat(object value, out JsonNode? json)
    {
        json = value is Choice ? JsonValue.Create("opt") : null;
        return value is Choice;
    }
}

/// <summary>Formats nothing.</summary>
public sealed class NoFormatter : IValueFormatter
{
    public bool TryFormat(object value, out JsonNode? json)
    {
        json = null;
        return false;
    }
}

/// <summary>The base a listing walks into: private state, a virtual property and a hider's target.</summary>
public class Guild
{
    private readonly int _ledger = 1;

    public static int Founded { get; set; }

    public int Standing => _ledger;

    public virtual string Name => "guild";

    public string Motto() => Name;
}

/// <summary>Overrides <see cref="Guild.Name"/>, hides <see cref="Guild.Motto"/> and <see cref="Guild.Founded"/>.</summary>
public sealed class Chapter : Guild
{
    private readonly string _banner = "red";

    public static new int Founded { get; set; }

    public override string Name => "chapter";

    public new string Motto() => Name;

    public int Banner => _banner.Length;
}

/// <summary>The members Godot's own source generator adds to a partial class, which it marks with <c>Never</c>.</summary>
public sealed class Arcanum
{
    private readonly int _depth = 1;

    public int Open => _depth;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public int Hidden => _depth;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public int Vanish() => _depth;
}

/// <summary>A static class: its statics alone, and no constructor, not even the one its field initialiser makes.</summary>
public static class Ledger
{
    private static int _total = 1;

    public static int Total => _total;

    public static int Add(int amount) => _total += amount;
}

/// <summary>A base whose constructor is its own: a derived class never inherits one.</summary>
public class Banner(int width)
{
    public int Width { get; } = width;
}

/// <summary>Its constructor alone, and not <see cref="Banner"/>'s.</summary>
public sealed class Pennant(string label) : Banner(label.Length)
{
    public string Label { get; } = label;
}
