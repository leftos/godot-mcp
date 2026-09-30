using System.Diagnostics;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.TestSupport;

public sealed class TempDirectoryTests
{
    [Fact]
    public void DisposeRetriesAFileHeldBriefly()
    {
        TempDirectory temp = new();
        string held = temp.Combine("held.txt");
        File.WriteAllText(held, "held");

        var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
        // The hold is released the moment the first delete attempt is refused, so the retry is proven whatever the
        // machine is doing: a release scheduled on the thread pool can run after Dispose's budget has run out.
        temp.AfterRefusedAttempt = handle.Dispose;
        try
        {
            temp.Dispose();
        }
        finally
        {
            handle.Dispose();
        }

        Assert.False(Directory.Exists(temp.Path));
    }

    [Fact]
    public void DisposeGivesUpOnAFileHeldPastTheBudget()
    {
        TempDirectory temp = new();
        string held = temp.Combine("held.txt");
        File.WriteAllText(held, "held");

        var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            var clock = Stopwatch.StartNew();
            Assert.Throws<IOException>(temp.Dispose);
            clock.Stop();

            Assert.True(clock.ElapsedMilliseconds >= 2000, $"Dispose gave up after {clock.ElapsedMilliseconds} ms");
        }
        finally
        {
            handle.Dispose();
            temp.Dispose();
        }
    }
}
