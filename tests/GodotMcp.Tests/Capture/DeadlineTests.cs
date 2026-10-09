using System.Globalization;
using GodotMcp.Capture;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Capture;

public sealed class DeadlineTests : IDisposable
{
    /// <summary>Longer than the deadline's 200 ms re-read interval, so the next ask reads the file again.</summary>
    private static readonly TimeSpan PastReread = TimeSpan.FromMilliseconds(300);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AFutureEndHasNotPassed()
    {
        Deadline deadline = Open(DateTime.UtcNow.AddMinutes(5));

        Assert.False(deadline.HasPassed());
    }

    [Fact]
    public void AnOffsetInstantIsReadAsTheSameUtcInstant()
    {
        string path = _temp.Combine("deadline.txt");
        DateTime end = DateTime.UtcNow.AddMinutes(5);
        File.WriteAllText(path, new DateTimeOffset(end).ToOffset(TimeSpan.FromHours(-7)).ToString("o", CultureInfo.InvariantCulture) + "\n");

        Assert.False(Assert.IsType<Deadline>(Deadline.FromFile(path, out _)).HasPassed());
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("2026-10-09 12:00")]
    public void AFileWithoutARoundTripInstantIsRefused(string text)
    {
        string path = _temp.Combine("deadline.txt");
        File.WriteAllText(path, text + "\n");

        Assert.Null(Deadline.FromFile(path, out string? error));
        Assert.Equal($"the deadline file {path} holds '{text}', not a UTC instant in round-trip (\"o\") format", error);
    }

    [Fact]
    public async Task ARewrittenFileIsReadAgain()
    {
        string path = _temp.Combine("deadline.txt");
        Deadline deadline = Open(DateTime.UtcNow.AddMinutes(5));

        Write(path, DateTime.UtcNow.AddMinutes(-1));
        await Task.Delay(PastReread, TestContext.Current.CancellationToken);

        Assert.True(deadline.HasPassed());
    }

    [Fact]
    public async Task AFileThatGoesMissingKeepsTheLastEndItHeld()
    {
        string path = _temp.Combine("deadline.txt");
        Deadline deadline = Open(DateTime.UtcNow.AddMinutes(5));

        File.Delete(path);
        await Task.Delay(PastReread, TestContext.Current.CancellationToken);

        Assert.False(deadline.HasPassed());
    }

    [Fact]
    public async Task AnUnreadableRewriteKeepsTheLastEndItHeld()
    {
        string path = _temp.Combine("deadline.txt");
        Deadline deadline = Open(DateTime.UtcNow.AddMinutes(5));

        File.WriteAllText(path, "half a li");
        await Task.Delay(PastReread, TestContext.Current.CancellationToken);

        Assert.False(deadline.HasPassed());
    }

    [Fact]
    public async Task EndNowEndsTheRunWhateverTheFileSays()
    {
        Deadline deadline = Open(DateTime.UtcNow.AddMinutes(5));

        deadline.EndNow();
        await Task.Delay(PastReread, TestContext.Current.CancellationToken);

        Assert.True(deadline.HasPassed());
    }

    private Deadline Open(DateTime endUtc)
    {
        string path = _temp.Combine("deadline.txt");
        Write(path, endUtc);
        return Deadline.FromFile(path, out string? error) ?? throw new InvalidOperationException(error);
    }

    private static void Write(string path, DateTime endUtc) => File.WriteAllText(path, endUtc.ToString("o", CultureInfo.InvariantCulture) + "\n");
}
