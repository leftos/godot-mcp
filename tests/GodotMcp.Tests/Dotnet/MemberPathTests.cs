using System.Reflection;
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
    public void AThrowingGetterIsReported() =>
        Assert.Equal("'Broken' threw InvalidOperationException: no at Broken", Refusal(new Holder(), "Broken"));

    private static object? Walk(object root, string path) => MemberPath.Walk(root, MemberPath.Parse(path), AllInstance);

    private static string Refusal(object root, string path) => Assert.Throws<MemberPathException>(() => Walk(root, path)).Message;
}
