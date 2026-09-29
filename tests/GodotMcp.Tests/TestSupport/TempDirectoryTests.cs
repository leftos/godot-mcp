using System.Diagnostics;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.TestSupport;

public sealed class TempDirectoryTests
{
    [Fact]
    public async Task DisposeRetriesAFileHeldBriefly()
    {
        TempDirectory temp = new();
        string held = temp.Combine("held.txt");
        File.WriteAllText(held, "held");

        CancellationToken cancellation = TestContext.Current.CancellationToken;
        var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            var release = Task.Run(
                async () =>
                {
                    await Task.Delay(150, cancellation);
                    handle.Dispose();
                },
                cancellation
            );

            temp.Dispose();
            await release;
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
