using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

/// <summary>Parameters passed by reference, one that cannot be passed at all, and overloads that one argument list fits twice.</summary>
public sealed class Vault
{
    private readonly int _code = 7;

    public bool TryOpen(int code, out string secret)
    {
        secret = code == _code ? "gold" : "";
        return code == _code;
    }

    public int Grow(ref int value)
    {
        value *= _code - 5;
        return value;
    }

    public int Sum(Span<int> values)
    {
        int total = _code;
        foreach (int value in values)
        {
            total += value;
        }
        return total;
    }

    public string Take(IShield shield) => shield.GetType().Name + _code;

    public string Take(out int count)
    {
        count = _code;
        return "count";
    }

    public string Mark(int a, string b) => b + a + _code;

    public string Mark(long a, string? b) => b + a + _code;
}

/// <summary>A base standing in for Godot's own: a virtual method to override and a method the derived type does not declare.</summary>
public class Mentor
{
    private int _rested;

    public virtual int Teach(int hours) => hours + _rested;

    public void Rest() => _rested++;
}

/// <summary>Overrides <see cref="Mentor.Teach"/>.</summary>
public sealed class Tutor : Mentor
{
    public override int Teach(int hours) => hours * 2;
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

/// <summary>
/// The mark a game declares itself, matched by name from any namespace: the description an agent reads, and the name,
/// the note on when the tool is taken and whether it changes the game.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GodotMcpToolAttribute(string description) : Attribute
{
    public string Description { get; } = description;

    public string? Name { get; init; }

    public string? When { get; init; }

    public bool ReadOnly { get; init; }
}

/// <summary>Every method the project-tool tests describe: one per scalar, collection, record and refused parameter kind.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "The samples exist to be described by their signatures, not run; whether one touches instance state is beside the point."
)]
public sealed class ToolBench
{
    public bool Flagged(bool value) => value;

    public string Text(string value) => value;

    public bool Lettered(char value) => value == 'x';

    public string Identified(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    public int Stamped(DateTime at) => at.Year;

    public int Shifted(DateTimeOffset offset) => offset.Year;

    public long Ticked(TimeSpan span) => span.Ticks;

    public double Fraction(double value) => value;

    public float Small(float value) => value;

    public decimal Money(decimal value) => value;

    public int Bounded(byte small, sbyte tiny, short medium, ushort plus, int whole, uint count, long large, ulong big) =>
        small + tiny + medium + plus + whole + (int)count + (int)large + (int)(big % 10);

    public int Colored(Color color, Access access) => (int)color + (int)access;

    public int Tinted(Color tint = Color.Green, Access access = Access.Read | Access.Write) => (int)tint + (int)access;

    public int Supplied(int count = 3, string label = "hi", int? nothing = null, string? maybe = null) =>
        count + label.Length + (nothing ?? 0) + (maybe?.Length ?? 0);

    public int Rest(params string[] names) => names.Length;

    public int Listed(
        int[] numbers,
        List<Color> colors,
        IList<int> ordered,
        IReadOnlyList<string> labels,
        IEnumerable<int> sequence,
        ICollection<int> bag,
        ImmutableArray<int> frozen,
        ImmutableList<Color> chained
    ) => numbers.Length + colors.Count + ordered.Count + labels.Count + sequence.Count() + bag.Count + frozen.Length + chained.Count;

    public int Unique(HashSet<int> ids) => ids.Count;

    public int Mapped(Dictionary<string, int> counts, IReadOnlyDictionary<string, List<string?>> tags) => counts.Count + tags.Count;

    public int Powered([Description("How hard to hit")] int power) => power;

    public int Assigned(Choice choice) => choice.Cost;

    public int Offered(Offer offer) => offer.Options.Length;

    public int Chosen(Pair pair) => pair.A;

    public int Configured(Settings settings) => settings.Volume;

    public int Attached(Mentor mentor, Tutor tutor) => mentor.Teach(1) + tutor.Teach(2);

    public int Abstracted(IShield shield, Weapon weapon) => shield.Absorb(1) + weapon.Damage;

    public string Anything(object value) => value.GetType().Name;

    public int Waited(int amount, CancellationToken token) => token.IsCancellationRequested ? -amount : amount;

    public int Inlined(in int code) => code;

    public string Handled(Action<int> handler) => handler.Method.Name;

    public Task<string> Fetched() => Task.FromResult("done");

    public void Quiet() { }

    public bool Ping() => true;

    public int Needed(int amount, string label) => amount + label.Length;

    public int Arranged(int amount, string? label, Color tint = Color.Green) => amount + (label?.Length ?? 0) + (int)tint;

    public int Freed(int? amount, string? label) => (amount ?? 0) + (label?.Length ?? 0);

    public int Warded(Shield shield, Plain? plain = null) => shield.Absorb(1) + (plain?.Weight ?? 0);

    public bool Counted(int amount, out int count)
    {
        count = amount;
        return true;
    }

    public int Grown(ref int value) => value;

    public int Summed(Span<int> values) => values.Length;

    public int Collected(IReadOnlyCollection<int> items) => items.Count;

    public int Setted(ISet<int> items) => items.Count;

    public int Keyed(Dictionary<int, string> map) => map.Count;

    public int Sized(nint size, nuint limit) => (int)size + (int)limit;

    public int Sourced(Func<int> source) => source();

    public Span<int> Returned() => default;

    public T Echoed<T>(T value) => value;

    public int Printed(Ticket ticket) => ticket.Label.Length + Ticket.Built;

    public int Limited(nuint limit) => (int)limit;

    public int Native(nint? size) => (int)(size ?? 0);

    public int ListedDeep(List<Dictionary<int, string>> maps) => maps.Count;

    public int Nullables(int?[] numbers) => numbers.Length;

    public int Pursed(Purse? purse) => purse?.Coins ?? 0;

    public int KeyedReadOnly(IReadOnlyDictionary<int, string> map) => map.Count;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1708:Identifiers should differ by more than case",
        Justification = "The two names are the case hint's whole point: a key matching both is reported with both."
    )]
    public int Cased(int value, string Value) => value + Value.Length;

    public int Defaulted(Guid id = default, Purse purse = default) => purse.Coins + (id == Guid.Empty ? 0 : 1);

    public int Noted(Note note) => note.Title.Length + (note.Remark?.Length ?? 0);

    public int Timed([Optional, DateTimeConstant(630822816000000000)] DateTime at) => at.Year;

    public int Guarded([Description("The shield to ward with")] IShield shield) => shield.Absorb(1);

    /// <summary>A marked tool with every optional note unset.</summary>
    [GodotMcpTool("Marks nothing in particular.")]
    public void Marked() { }

    /// <summary>A marked tool that renames itself and says it changes nothing.</summary>
    [GodotMcpTool("Reads a party member's hit points.", Name = "get_hp", ReadOnly = true)]
    public int GotHp(bool enemy, int index) => index;

    /// <summary>A marked tool with a <c>When</c> and a parameter description.</summary>
    [GodotMcpTool("Jumps the party into the first uncleared room of a kind.", When = "from the map")]
    public bool JumpToRoomOfKind([Description("Enemy, Elite or Boss")] string kind) => kind.Length > 0;
}

/// <summary>A class whose one constructor counts itself, so a test can tell a refusal came before any building.</summary>
public sealed class Ticket
{
    public Ticket(string label)
    {
        Label = label;
        Built++;
    }

    /// <summary>How many tickets have been constructed.</summary>
    public static int Built { get; private set; }

    public string Label { get; }
}

/// <summary>A struct a tool takes as a nullable, whose members one level of schema describes.</summary>
public readonly record struct Purse(int Coins);

/// <summary>A record whose two members differ in nullability.</summary>
public sealed record Note(string Title, string? Remark);

/// <summary>A static class on an open generic type, whose methods no game tool can be built from.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "The sample exists to be the open generic type a game tool is refused for."
)]
public static class Pool<T>
{
    public static bool Add(T item) => item is not null;
}

/// <summary>A private state method, which only reflection calls, as a game's is.</summary>
public sealed class PrivateState
{
    private readonly int _hp = 3;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality",
        "IDE0051:Remove unused private members",
        Justification = "The state method is found and called by reflection."
    )]
    private int _McpState() => _hp;
}

/// <summary>An internal state method.</summary>
public sealed class InternalState
{
    private readonly string _label = "internal";

    internal string _McpState() => _label;
}

/// <summary>A state method a derived type inherits or overrides.</summary>
public class BaseState
{
    private readonly string _label = "base";

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1707:Identifiers should not contain underscores",
        Justification = "_McpState is the state method's name, which a game's type declares."
    )]
    protected virtual object _McpState() => _label;
}

/// <summary>Inherits <see cref="BaseState"/>'s state method.</summary>
public sealed class InheritedState : BaseState;

/// <summary>Overrides <see cref="BaseState"/>'s state method.</summary>
public sealed class OverriddenState : BaseState
{
    protected override object _McpState() => "overridden";
}

/// <summary>A private state method on a base, which a walk stopping at that base does not reach.</summary>
public class StoppedBase
{
    private readonly int _level = 1;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality",
        "IDE0051:Remove unused private members",
        Justification = "The state method is found and called by reflection."
    )]
    private int _McpState() => _level;
}

/// <summary>Inherits <see cref="StoppedBase"/>'s private state method.</summary>
public sealed class PastTheStop : StoppedBase;

/// <summary>A generic _McpState, which is no state method.</summary>
public sealed class GenericState
{
    private readonly int _level = 1;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality",
        "IDE0051:Remove unused private members",
        Justification = "The sample exists to be the method reflection does not take."
    )]
    private int _McpState<T>() => _level;
}

/// <summary>An _McpState with a parameter, which is no state method.</summary>
public sealed class ParameterState
{
    private readonly int _level = 1;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality",
        "IDE0051:Remove unused private members",
        Justification = "The sample exists to be the method reflection does not take."
    )]
    private int _McpState(int extra) => _level + extra;
}

/// <summary>A static _McpState, which is no state method.</summary>
public sealed class StaticState
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality",
        "IDE0051:Remove unused private members",
        Justification = "The sample exists to be the method reflection does not take."
    )]
    private static int _McpState() => 1;
}

/// <summary>A collection whose enumerator throws, as a game's broken collection might.</summary>
public sealed class BrokenCollection : IEnumerable<int>
{
    public IEnumerator<int> GetEnumerator() => throw new InvalidOperationException("enumeration broke");

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A state method whose value throws when it is written.</summary>
public sealed class BrokenEnumerationState
{
    private readonly BrokenCollection _items = new();

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality",
        "IDE0051:Remove unused private members",
        Justification = "The state method is found and called by reflection."
    )]
    private BrokenCollection _McpState() => _items;
}

/// <summary>A state method returning a Span, which reflection cannot call.</summary>
public sealed class SpanState
{
    private readonly int[] _items = [1, 2];

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality",
        "IDE0051:Remove unused private members",
        Justification = "The state method is found and called by reflection."
    )]
    private Span<int> _McpState() => _items;
}

/// <summary>A state method returning a task: found, then refused rather than awaited.</summary>
public sealed class TaskState
{
    private readonly int _level = 1;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality",
        "IDE0051:Remove unused private members",
        Justification = "The state method is found by reflection."
    )]
    private Task<int> _McpState() => Task.FromResult(_level);
}

/// <summary>
/// A state method declared to return object that returns a running async task, whose runtime type is the async builder's
/// hidden state-machine box.
/// </summary>
public sealed class RunningTaskState
{
    private readonly TaskCompletionSource _never = new();

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality",
        "IDE0051:Remove unused private members",
        Justification = "The state method is found and called by reflection."
    )]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "Declared object on purpose: the task is found only in the value it returns."
    )]
    private object _McpState() => RunAsync();

    private async Task<int> RunAsync()
    {
        await _never.Task;
        return 1;
    }
}

/// <summary>A state of a million values, past the values one node's state writes.</summary>
public sealed class HugeState
{
    private readonly int[] _items = [.. Enumerable.Range(0, 1_000_000)];

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality",
        "IDE0051:Remove unused private members",
        Justification = "The state method is found and called by reflection."
    )]
    private int[] _McpState() => _items;
}
