using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A scene's memory watch decided on one reading: the ceiling passed only above it, a null reading ending the watch, the first
/// breach kept, and the kill reason it gives. Nothing here reads a process or waits on a clock.
/// </summary>
public sealed class ScratchMemoryWatchTests
{
    private const long Megabyte = ScratchMemoryWatch.Megabyte;

    private static readonly ScratchMemoryCeiling Ceiling = new(1024, ScratchMemoryCeiling.OptionsSource);

    [Fact]
    public void AReadingAtTheCeilingIsNotABreach()
    {
        using CancellationTokenSource play = new();

        Assert.Equal(MemoryReading.Watching, Watch(play).Read(1024 * Megabyte));
        Assert.False(play.IsCancellationRequested);
    }

    [Fact]
    public void AReadingOneByteAboveTheCeilingIsABreachThatCancelsThePlay()
    {
        using CancellationTokenSource play = new();
        ScratchMemoryWatch watch = Watch(play);

        Assert.Equal(MemoryReading.Breached, watch.Read((1024 * Megabyte) + 1));
        Assert.True(play.IsCancellationRequested);
        Assert.Equal((1024 * Megabyte) + 1, watch.BreachBytes);
    }

    [Fact]
    public void ANullReadingEndsTheWatchQuietly()
    {
        using CancellationTokenSource play = new();
        ScratchMemoryWatch watch = Watch(play);

        Assert.Equal(MemoryReading.Exited, watch.Read(null));
        Assert.False(play.IsCancellationRequested);
        Assert.Null(watch.KillReason);
    }

    [Fact]
    public void OnlyTheFirstBreachIsKept()
    {
        using CancellationTokenSource play = new();
        ScratchMemoryWatch watch = Watch(play);

        watch.Read(1536 * Megabyte);
        watch.Read(2048 * Megabyte);

        Assert.Equal(1536 * Megabyte, watch.BreachBytes);
    }

    [Fact]
    public void TheKillReasonNamesTheCeilingTheReadingInWholeMegabytesAndTheSource()
    {
        using CancellationTokenSource play = new();
        ScratchMemoryWatch watch = Watch(play);

        watch.Read((long)(1536.4 * Megabyte));

        Assert.Equal(
            "the scene's game passed its memory ceiling of 1024 MB (1536 MB private), set by options.memoryCeilings, so its game " + "was stopped",
            watch.KillReason
        );
    }

    private static ScratchMemoryWatch Watch(CancellationTokenSource play) => new(Ceiling, TimeProvider.System, play);
}
