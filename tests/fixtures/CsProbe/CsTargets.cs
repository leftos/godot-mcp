using System.Collections.Generic;
using Godot;

namespace CsProbe;

/// <summary>The node the C# runtime tools' tests add to a scene and drive: one member of every shape a tool calls or reads.</summary>
public partial class CsTargets : Node
{
    private Update _last = new("start", new Point2(1, 2), [1, 2, 3], Mood.Calm);

    internal List<int> Numbers { get; } = [4, 5, 6];

    internal int? MaybeCount(bool give) => give ? 7 : null;

    internal Update Last() => _last;

    internal void Take(Update update) => _last = update;

    internal T Echo<T>(T value) => value;

    internal string Hit(int amount) => "int " + amount;

    internal string Hit(float amount) => "float " + amount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal async System.Threading.Tasks.Task<int> CountLaterAsync(int n)
    {
        await System.Threading.Tasks.Task.Delay(10);
        return n * 2;
    }

    internal string GreetWith(IGreeter greeter, string name) => greeter.Greet(name);

    internal Mood Mood { get; set; } = Mood.Calm;

    internal void Fail() => throw new System.InvalidOperationException("probe failure");
}
