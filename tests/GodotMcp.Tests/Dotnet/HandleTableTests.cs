using GodotMcp.Dotnet.Core;

namespace GodotMcp.Tests.Dotnet;

public sealed class HandleTableTests
{
    [Fact]
    public void AddIssuesIdsCountingFromOneUnderTheEpoch()
    {
        HandleTable table = new(3);

        Assert.Equal("h3.1", table.Add(new object()));
        Assert.Equal("h3.2", table.Add(new object()));
    }

    [Fact]
    public void GetReturnsTheValueAdded()
    {
        HandleTable table = new(1);
        Choice value = new("A", 1);

        Assert.Same(value, table.Get(table.Add(value)));
    }

    [Fact]
    public void AnIdIsNeverReusedAfterEviction()
    {
        HandleTable table = new(1, capacity: 1);
        table.Add(new object());
        table.Add(new object());

        Assert.Equal("h1.3", table.Add(new object()));
    }

    [Fact]
    public void TheLeastRecentlyUsedHandleIsEvictedPastCapacity()
    {
        HandleTable table = new(1, capacity: 2);
        object first = new();
        object third = new();
        string a = table.Add(first);
        string b = table.Add(new object());
        table.Get(a);
        string c = table.Add(third);

        Assert.Same(first, table.Get(a));
        Assert.Same(third, table.Get(c));
        Assert.Equal(
            "handle h1.2 is gone: at most 2 handles are kept, least recently used dropped first",
            Assert.Throws<HandleException>(() => table.Get(b)).Message
        );
    }

    [Fact]
    public void AHandleFromAnotherEpochIsRefused() =>
        Assert.Equal("handle h2.4 was dropped when the game restarted", Assert.Throws<HandleException>(() => new HandleTable(1).Get("h2.4")).Message);

    [Fact]
    public void AnUnknownHandleIsRefused() =>
        Assert.Equal(
            "handle h1.9 is gone: at most 256 handles are kept, least recently used dropped first",
            Assert.Throws<HandleException>(() => new HandleTable(1).Get("h1.9")).Message
        );

    [Theory]
    [InlineData("x1.1")]
    [InlineData("h1")]
    [InlineData("h.1")]
    [InlineData("h1.x")]
    [InlineData("h1.-1")]
    public void AMalformedIdIsRefused(string id) =>
        Assert.Equal(
            $"'{id}' is not a handle id: expected h<epoch>.<number>",
            Assert.Throws<HandleException>(() => new HandleTable(1).Get(id)).Message
        );
}
