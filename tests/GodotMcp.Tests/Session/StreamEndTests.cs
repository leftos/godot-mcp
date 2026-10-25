using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>The count of a process's two output streams reaching their end: ended fires once, when the second one does.</summary>
public sealed class StreamEndTests
{
    [Fact]
    public void EndedFiresOnceBothStreamsHaveEnded()
    {
        int calls = 0;
        StreamEnd end = new(() => calls++);

        end.One();
        int afterOne = calls;
        end.One();

        Assert.Equal(0, afterOne);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AnEndPastBothNeverFiresEndedAgain()
    {
        int calls = 0;
        StreamEnd end = new(() => calls++);

        end.One();
        end.One();
        end.One();

        Assert.Equal(1, calls);
    }
}
