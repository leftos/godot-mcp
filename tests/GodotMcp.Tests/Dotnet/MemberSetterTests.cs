using System.Reflection;
using System.Text.Json.Nodes;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

/// <summary>MemberSetter's set, read-back and put-back, on plain objects whose getters and setters misbehave.</summary>
public sealed class MemberSetterTests
{
    private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void SetReportsBeforeFromBeforeTheWrite()
    {
        CopyInto holder = new();

        JsonObject result = Set(holder, "Items", JsonNode.Parse("[3]"));

        Assert.Equal("[1,2]", result["before"]?.ToJsonString());
        Assert.Equal("[3]", result["after"]?.ToJsonString());
        Assert.Equal("Items", result["member"]?.GetValue<string>());
    }

    [Fact]
    public void AMismatchPutsBeforeBack()
    {
        Clamping holder = new();

        string message = Refusal(holder, "Value", JsonValue.Create(50));

        Assert.Equal("'Value' did not take the value: it read 10 after the set, so it was put back to 5.", message);
        Assert.Equal(5, holder.Value);
    }

    [Fact]
    public void APutBackThroughACopyIntoSetterRestoresTheContents()
    {
        CopyIntoFirstTwo holder = new();

        string message = Refusal(holder, "Items", JsonNode.Parse("[7,8,9]"));

        Assert.Equal("'Items' did not take the value: it read [7,8] after the set, so it was put back to [1,2].", message);
        Assert.Equal([1, 2], holder.Items);
    }

    [Fact]
    public void APutBackThatThrowsSaysWhatTheMemberNowReads()
    {
        OneWay holder = new();

        string message = Refusal(holder, "Value", JsonValue.Create(50));

        Assert.Equal(
            "'Value' did not take the value: it read 10 after the set; putting 5 back threw InvalidOperationException: no going back, "
                + "so it now reads 10.",
            message.Split('\n')[0]
        );
        Assert.Contains("OneWay.set_Value", message, StringComparison.Ordinal);
        Assert.Equal(10, holder.Value);
    }

    [Fact]
    public void AnAfterReadThatThrowsPutsBeforeBack()
    {
        Unreadable holder = new();

        string message = Refusal(holder, "Value", JsonValue.Create(99));

        Assert.Equal("reading 'Value' back after the set threw InvalidOperationException: unreadable; 5 was put back.", message.Split('\n')[0]);
        Assert.Contains("Unreadable.get_Value", message, StringComparison.Ordinal);
        Assert.Equal(5, holder.Value);
    }

    [Fact]
    public void AMismatchMessageCutsLongValues()
    {
        Suffixed holder = new();

        string message = Refusal(holder, "Text", JsonValue.Create(new string('a', 5000)));

        // The after value and the put-back value are each cut to 2000 characters, the last three of them "...".
        string cut = "\"" + new string('a', 1996) + "...";
        Assert.Equal($"'Text' did not take the value: it read {cut} after the set, so it was put back to \"start\".", message);
        Assert.Equal("start", holder.Text);
    }

    [Fact]
    public void AFloatMemberTakesADoubleThatRoundsThroughSingle()
    {
        SinglePrecision holder = new();

        JsonObject result = Set(holder, "Value", JsonValue.Create(0.1));

        // A single-precision hold reads 0.1 back as 0.10000000149011612, the value written within a rounding step.
        Assert.Equal(0.10000000149011612, result["after"]!.GetValue<double>());
        Assert.Equal(0.10000000149011612, holder.Value);
    }

    [Fact]
    public void ARealDifferenceIsStillRefused()
    {
        RoundingToOneDecimal holder = new();

        string message = Refusal(holder, "Value", JsonValue.Create(1.26));

        Assert.Equal("'Value' did not take the value: it read 1.3 after the set, so it was put back to 0.5.", message);
        Assert.Equal(0.5, holder.Value);
    }

    [Fact]
    public void AVectorLikeObjectTakesAFloatRoundTrip()
    {
        DriftingRotation holder = new();

        JsonObject result = Set(holder, "Rotation", JsonNode.Parse("""{"X": 1.5, "Y": 2, "Z": 0}"""));

        Assert.Equal(2, result["after"]!["Y"]!.GetValue<double>());
        Assert.InRange(result["after"]!["X"]!.GetValue<double>(), 1.5, 1.50000001);
    }

    private static JsonObject Set(object root, string path, JsonNode? value) =>
        MemberSetter.Set(
            MemberPath.Slot(MemberRoot.Of(root), MemberPath.Parse(path), AllInstance),
            path,
            value,
            new FakeResolver(),
            new NoFormatter()
        );

    private static string Refusal(object root, string path, JsonNode? value) =>
        Assert.Throws<MemberPathException>(() => Set(root, path, value)).Message;

    /// <summary>A setter that copies into the list it holds, so the old value changes with the set.</summary>
    private sealed class CopyInto
    {
        private readonly List<int> _items = [1, 2];

        public List<int> Items
        {
            get => _items;
            set
            {
                _items.Clear();
                _items.AddRange(value);
            }
        }
    }

    /// <summary>
    /// Copies the first two items it is given into the list it holds, so a mismatch's put-back of that same list clears it into
    /// itself.
    /// </summary>
    private sealed class CopyIntoFirstTwo
    {
        private readonly List<int> _items = [1, 2];

        public List<int> Items
        {
            get => _items;
            set
            {
                _items.Clear();
                _items.AddRange(value.Take(2));
            }
        }
    }

    private sealed class Clamping
    {
        private int _value = 5;

        public int Value
        {
            get => _value;
            set => _value = Math.Clamp(value, 0, 10);
        }
    }

    /// <summary>Clamps like <see cref="Clamping"/>, and refuses to go back to 5 once it has left it.</summary>
    private sealed class OneWay
    {
        private int _value = 5;

        public int Value
        {
            get => _value;
            set => _value = value == 5 ? throw new InvalidOperationException("no going back") : Math.Clamp(value, 0, 10);
        }
    }

    /// <summary>A getter that throws while the value is 99.</summary>
    private sealed class Unreadable
    {
        private int _value = 5;

        public int Value
        {
            get => _value == 99 ? throw new InvalidOperationException("unreadable") : _value;
            set => _value = value;
        }
    }

    /// <summary>Holds its value in single precision, as a Godot vector component is held.</summary>
    private sealed class SinglePrecision
    {
        private float _value;

        public double Value
        {
            get => _value;
            set => _value = (float)value;
        }
    }

    /// <summary>Rounds the value it is given to one decimal place.</summary>
    private sealed class RoundingToOneDecimal
    {
        private double _value = 0.5;

        public double Value
        {
            get => _value;
            set => _value = Math.Round(value, 1);
        }
    }

    /// <summary>A rotation of three degrees, as a Godot Vector3 is written: an object of three numbers.</summary>
    private sealed class Rotation
    {
        private double _x;
        private double _y;
        private double _z;

        public double X
        {
            get => _x;
            set => _x = value;
        }

        public double Y
        {
            get => _y;
            set => _y = value;
        }

        public double Z
        {
            get => _z;
            set => _z = value;
        }
    }

    /// <summary>Stores each degree a float32 rounding step off the one it was given, as a Godot rotation held as a basis does.</summary>
    private sealed class DriftingRotation
    {
        private Rotation _rotation = new();

        public Rotation Rotation
        {
            get => _rotation;
            set =>
                _rotation = new Rotation
                {
                    X = value.X + 1e-9,
                    Y = value.Y,
                    Z = value.Z,
                };
        }
    }

    /// <summary>A setter that changes a long value it is given, so it reads back as something else.</summary>
    private sealed class Suffixed
    {
        private string _text = "start";

        public string Text
        {
            get => _text;
            set => _text = value.Length > 100 ? value + "!" : value;
        }
    }
}
