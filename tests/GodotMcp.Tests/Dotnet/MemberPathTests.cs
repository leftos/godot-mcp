using System.Reflection;
using System.Text.Json.Nodes;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

public sealed class MemberPathTests
{
    private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void ParseSplitsMembersAndIndexes()
    {
        MemberPathSegment[] expected =
        [
            new MemberSegment("BoundUpdate"),
            new MemberSegment("Pending"),
            new MemberSegment("Options"),
            new IndexSegment(0),
        ];

        Assert.Equal(expected, MemberPath.Parse("BoundUpdate.Pending.Options[0]").Segments);
    }

    [Fact]
    public void ParseReadsAQuotedKey()
    {
        MemberPathSegment[] expected = [new MemberSegment("Map"), new KeySegment("key")];

        Assert.Equal(expected, MemberPath.Parse("Map[\"key\"]").Segments);
    }

    [Fact]
    public void ParseReadsAMemberAfterAnIndex()
    {
        MemberPathSegment[] expected = [new MemberSegment("Items"), new IndexSegment(2), new MemberSegment("Name")];

        Assert.Equal(expected, MemberPath.Parse("Items[2].Name").Segments);
    }

    [Theory]
    [InlineData("", "member path '': expected a member name at 0")]
    [InlineData(".A", "member path '.A': expected a member name at 0")]
    [InlineData("A.", "member path 'A.': expected a member name at 2")]
    [InlineData("A..B", "member path 'A..B': expected a member name at 2")]
    [InlineData("A[0", "member path 'A[0': unclosed '[' at 1")]
    [InlineData("A[\"k", "member path 'A[\"k': unclosed '[' at 1")]
    [InlineData("A[x]", "member path 'A[x]': expected an integer or a quoted key at 2")]
    [InlineData("A.1b", "member path 'A.1b': '1b' is not a C# identifier at 2")]
    public void ParseRefusesAMalformedPath(string text, string message) =>
        Assert.Equal(message, Assert.Throws<MemberPathException>(() => MemberPath.Parse(text)).Message);

    [Fact]
    public void WalkFollowsPropertiesAndListIndexes()
    {
        Holder holder = new();
        holder.Items.Add(new Choice("A", 1));
        holder.Items.Add(new Choice("B", 2));

        Assert.Equal("B", Walk(holder, "Items[1].Label"));
    }

    [Fact]
    public void WalkReadsADictionaryByKey()
    {
        Holder holder = new();
        holder.Map["k"] = 5;

        Assert.Equal(5, Walk(holder, "Map[\"k\"]"));
    }

    [Fact]
    public void WalkReadsANonPublicField() => Assert.Equal(42, Walk(new Holder(), "_secret"));

    [Fact]
    public void AMemberOfANullIsRefused() =>
        Assert.Equal("'Child' is null at Child.Child", Refusal(new Holder { Child = new Holder() }, "Child.Child.Items"));

    [Fact]
    public void AnUnknownMemberIsRefused() => Assert.Equal("Holder has no member 'Nope'", Refusal(new Holder(), "Nope"));

    [Fact]
    public void AnIndexOutOfRangeIsRefused()
    {
        Holder holder = new();
        holder.Items.Add(new Choice("A", 1));
        holder.Items.Add(new Choice("B", 2));

        Assert.Equal("index 5 is out of range (count 2) at Items[5]", Refusal(holder, "Items[5]"));
    }

    [Fact]
    public void AMissingKeyIsRefused() => Assert.Equal("key \"zz\" is not in the dictionary at Map[\"zz\"]", Refusal(new Holder(), "Map[\"zz\"]"));

    [Fact]
    public void AThrowingGetterCarriesItsStack()
    {
        string message = Refusal(new Holder(), "Broken");

        Assert.Equal("'Broken' threw InvalidOperationException: no at Broken", message.Split('\n')[0]);
        Assert.Contains("Holder.get_Broken", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeRootedWalkReadsAStaticProperty()
    {
        MemberValue read = MemberPath.Read(MemberRoot.Statics(typeof(Roster)), MemberPath.Parse("Names[1]"), AllInstance);

        Assert.Equal("b", read.Value);
        Assert.Equal(typeof(string), read.DeclaredType);
    }

    [Fact]
    public void TypeRootedWalkRefusesAnInstanceMember() =>
        Assert.Equal("'Size' is an instance member of Roster; target a {node} or {handle}", StaticRefusal(typeof(Roster), "Size"));

    [Fact]
    public void AMethodNameSaysCsCallCallsIt()
    {
        Assert.Equal("'Reveal' is a method of Holder; cs_call calls it", Refusal(new Holder(), "Reveal"));
        Assert.Equal("'Add' is a method of Ledger; cs_call calls it", StaticRefusal(typeof(Ledger), "Add"));
    }

    [Fact]
    public void SetWritesAPrivateSetterAndAnInitSetter()
    {
        Duel duel = new();

        JsonObject retries = Set(duel, "Retries", JsonValue.Create(3));
        Set(duel, "Name", JsonValue.Create("x"));

        Assert.Equal(0, retries["before"]?.GetValue<int>());
        Assert.Equal(3, retries["after"]?.GetValue<int>());
        Assert.Equal(3, duel.Retries);
        Assert.Equal("x", duel.Name);
    }

    [Fact]
    public void SetWritesAReadonlyInstanceField()
    {
        Holder holder = new();

        JsonObject result = Set(holder, "_secret", JsonValue.Create(7));

        Assert.Equal(42, result["before"]?.GetValue<int>());
        Assert.Equal(7, holder.Reveal());
    }

    [Fact]
    public void SetRefusesAGetterOnlyPropertyAConstAndAStaticReadonly()
    {
        Assert.Equal("Settings.Fixed has no setter", SlotRefusal(MemberRoot.Of(new Settings()), "Fixed"));
        Assert.Equal("Rules.Limit is a const", SlotRefusal(MemberRoot.Statics(typeof(Rules)), "Limit"));
        Assert.Equal("Rules.Seed is static readonly; the runtime refuses to write it", SlotRefusal(MemberRoot.Statics(typeof(Rules)), "Seed"));
    }

    [Fact]
    public void SetThroughAStructStepIsRefusedNamingIt() =>
        Assert.Equal(
            "'At' is a Spot struct; a set through it would change a copy — set 'At' whole",
            SlotRefusal(MemberRoot.Of(new Placed()), "At.X")
        );

    [Fact]
    public void SetWritesAnIListIndexAndADictionaryKey()
    {
        Holder holder = new();
        holder.Items.Add(new Choice("A", 1));
        holder.Map["k"] = 5;

        JsonObject item = Set(holder, "Items[0]", JsonNode.Parse("{\"Label\":\"Z\",\"Cost\":9}"));
        JsonObject entry = Set(holder, "Map[\"k\"]", JsonValue.Create(6));

        Assert.Equal("A", item["before"]?["Label"]?.GetValue<string>());
        Assert.Equal(1, item["before"]?["Cost"]?.GetValue<int>());
        Assert.Equal(new Choice("Z", 9), holder.Items[0]);
        Assert.Equal(5, entry["before"]?.GetValue<int>());
        Assert.Equal(6, holder.Map["k"]);
    }

    [Fact]
    public void SetConvertsByTheMembersType()
    {
        Palette palette = new();

        Set(palette, "Tint", JsonValue.Create("Blue"));
        string refusal = Assert.Throws<MemberPathException>(() => Set(palette, "Tint", JsonValue.Create("Mauve"))).Message;

        Assert.Equal(Color.Blue, palette.Tint);
        Assert.StartsWith("cannot set 'Tint' (Color): ", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AThrowingSetterCarriesItsStack()
    {
        string message = Assert.Throws<MemberPathException>(() => Set(new Brittle(), "Level", JsonValue.Create(2))).Message;

        Assert.Equal("setting 'Level' threw InvalidOperationException: locked at Level", message.Split('\n')[0]);
        Assert.Contains("Brittle.set_Level", message, StringComparison.Ordinal);
    }

    private static object? Walk(object root, string path) => MemberPath.Walk(root, MemberPath.Parse(path), AllInstance);

    private static string Refusal(object root, string path) => Assert.Throws<MemberPathException>(() => Walk(root, path)).Message;

    private static string StaticRefusal(Type type, string path) =>
        Assert.Throws<MemberPathException>(() => MemberPath.Read(MemberRoot.Statics(type), MemberPath.Parse(path), AllInstance)).Message;

    private static JsonObject Set(object root, string path, JsonNode? value) =>
        MemberSetter.Set(
            MemberPath.Slot(MemberRoot.Of(root), MemberPath.Parse(path), AllInstance),
            path,
            value,
            new FakeResolver(),
            new NoFormatter()
        );

    private static string SlotRefusal(MemberRoot root, string path) =>
        Assert.Throws<MemberPathException>(() => MemberPath.Slot(root, MemberPath.Parse(path), AllInstance)).Message;

    /// <summary>A static list reached from the type, beside an instance member a type root cannot reach.</summary>
    private sealed class Roster
    {
        private readonly int _size = 2;

        public static List<string> Names { get; } = ["a", "b"];

        public int Size => _size;
    }

    /// <summary>The two statics a set cannot write.</summary>
    private static class Rules
    {
        public const int Limit = 3;

        public static readonly int Seed = 5;
    }

    private record struct Spot(int X);

    private sealed class Placed
    {
        public Spot At { get; set; }
    }

    private sealed class Palette
    {
        public Color Tint { get; set; }
    }

    private sealed class Brittle
    {
        private readonly int _level = 1;

        public int Level
        {
            get => _level;
            set => throw new InvalidOperationException("locked");
        }
    }
}
