using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

/// <summary>What <see cref="MemberListing"/> walks, what it leaves out, and how it names, filters and sorts the rest.</summary>
public sealed class MemberListingTests
{
    private static IReadOnlyList<MemberEntry> List(
        Type type,
        MemberScope scope = MemberScope.InstanceAndStatic,
        bool nonPublic = true,
        string? name = null,
        Func<Type, bool>? stopAt = null
    ) => MemberListing.List(type, scope, nonPublic, name, stopAt ?? (_ => false));

    private static MemberEntry[] Named(IEnumerable<MemberEntry> members, string name) => [.. members.Where(entry => entry.Name == name)];

    [Fact]
    public void ListsBothOverloadsOfAMethod()
    {
        MemberEntry[] hits = Named(List(typeof(Fighter)), "Hit");

        Assert.Equal(["string Hit(float amount)", "string Hit(int amount)"], hits.Select(entry => entry.Signature));
        Assert.All(hits, entry => Assert.Equal("method", entry.Kind));
    }

    [Fact]
    public void ListsPrivateFieldsWhenNonPublicAndNotOtherwise()
    {
        Assert.Contains(List(typeof(Chapter)), entry => entry.Name == "_banner");
        Assert.DoesNotContain(List(typeof(Chapter), nonPublic: false), entry => entry.Name == "_banner");
    }

    [Fact]
    public void LeavesOutAccessorsBackingFieldsAndRecordPlumbing()
    {
        MemberEntry[] members = [.. List(typeof(Offer))];

        Assert.Contains(members, entry => entry is { Name: "Title", Kind: "property" });
        Assert.DoesNotContain(
            members,
            entry => entry.Name.StartsWith("get_", StringComparison.Ordinal) || entry.Name.StartsWith("set_", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(members, entry => entry.Name.Contains("k__BackingField", StringComparison.Ordinal));
        Assert.DoesNotContain(members, entry => entry.Name is "EqualityContract" or "PrintMembers" or "<Clone>$");
    }

    [Fact]
    public void LeavesOutEditorBrowsableNeverMembers()
    {
        MemberEntry[] members = [.. List(typeof(Arcanum))];

        Assert.Contains(members, entry => entry.Name == "Open");
        Assert.DoesNotContain(members, entry => entry.Name is "Hidden" or "Vanish");
    }

    [Fact]
    public void WalksBasesUntilTheStopType()
    {
        MemberEntry[] walked = [.. List(typeof(Chapter))];
        MemberEntry[] stopped = [.. List(typeof(Chapter), stopAt: type => type == typeof(Guild))];

        Assert.Contains(walked, entry => entry.Name == "_ledger");
        Assert.Contains(stopped, entry => entry is { Name: "Banner", Kind: "property", Static: false });
        Assert.DoesNotContain(stopped, entry => entry.Name is "_ledger" or "Standing");
    }

    [Fact]
    public void AnOverrideIsListedOnce()
    {
        MemberEntry[] members = [.. List(typeof(Chapter))];

        Assert.Single(members, entry => entry.Name == "Name");
        Assert.Single(members, entry => entry.Name == "Motto");
    }

    [Fact]
    public void ATypeScopeListsStaticsAndConstructors()
    {
        MemberEntry[] duel = [.. List(typeof(Duel), MemberScope.StaticAndConstructors)];
        MemberEntry[] ledger = [.. List(typeof(Ledger), MemberScope.StaticAndConstructors)];

        Assert.All(duel, entry => Assert.True(entry.Static || entry.Kind == "constructor"));
        Assert.Contains(duel, entry => entry is { Name: "Label", Static: true });
        Assert.Contains(duel, entry => entry is { Kind: "constructor", Name: ".ctor", Signature: ".ctor(int a, string b)" });
        Assert.Equal(2, duel.Count(entry => entry.Kind == "constructor"));
        Assert.DoesNotContain(duel, entry => entry.Name is "Hit" or "Bind" or "_seed");
        Assert.Equal(["Add", "Total", "_total"], ledger.Select(entry => entry.Name));
        Assert.DoesNotContain(ledger, entry => entry.Kind == "constructor");
    }

    [Fact]
    public void ATypeScopeListsOnlyTheTypesOwnConstructors()
    {
        MemberEntry[] members = [.. List(typeof(Pennant), MemberScope.StaticAndConstructors)];

        Assert.Single(members, entry => entry.Kind == "constructor");
        Assert.Single(members, entry => entry is { Kind: "constructor", Signature: ".ctor(string label)" });
    }

    [Fact]
    public void AnInstanceScopeListsInstanceAndStaticMembersWithTheirFlag()
    {
        MemberEntry[] members = [.. List(typeof(Duel))];

        Assert.Contains(members, entry => entry is { Name: "Label", Static: true });
        Assert.Contains(members, entry => entry is { Name: "Hit", Static: false });
        Assert.DoesNotContain(members, entry => entry.Kind == "constructor");
    }

    [Fact]
    public void NameMatchesACaseInsensitiveSubstring()
    {
        MemberEntry[] hits = [.. List(typeof(Fighter), name: "hit")];

        Assert.Equal(2, hits.Length);
        Assert.All(hits, entry => Assert.Equal("Hit", entry.Name));
        Assert.Empty(List(typeof(Fighter), name: "absent"));
        Assert.Equal(2, List(typeof(Duel), MemberScope.StaticAndConstructors, name: ".CTOR").Count);
    }

    [Fact]
    public void SortsByNameThenSignature()
    {
        MemberEntry[] members = [.. List(typeof(Fighter))];

        Assert.Equal(["Echo", "Guard", "Heal", "Health", "Hit", "Hit"], members.Select(entry => entry.Name));
        Assert.Equal(["string Hit(float amount)", "string Hit(int amount)"], Named(members, "Hit").Select(entry => entry.Signature));
    }
}
