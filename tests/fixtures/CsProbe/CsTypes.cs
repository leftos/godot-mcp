namespace CsProbe;

/// <summary>One of the values the C# runtime tools' tests read from a field and pass as an argument.</summary>
public enum Mood
{
    Calm,
    Angry,
    Sleepy,
}

/// <summary>A record the C# runtime tools' tests compare by value.</summary>
public sealed record Point2(int X, int Y);

/// <summary>The record the C# runtime tools' tests pass as an argument and read back from a call.</summary>
public sealed record Update(string Label, Point2 At, System.Collections.Immutable.ImmutableArray<int> Values, Mood Mood);

/// <summary>The interface the C# runtime tools' tests pass an implementation of into a call.</summary>
public interface IGreeter
{
    string Greet(string name);
}

/// <summary>The implementation of <see cref="IGreeter" /> the C# runtime tools' tests pass into a call.</summary>
public sealed class Greeter : IGreeter
{
    public string Greet(string name) => "hello " + name;
}

/// <summary>The static state the C# runtime tools' tests read and change across calls.</summary>
public static class Tally
{
    public static int Count { get; set; }

    public static int Bump(int by) => Count += by;
}
