using GodotMcp.Server.CSharp;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.CSharp;

public sealed class HelperCacheTests : IDisposable
{
    private const string HexDigits = "0123456789abcdef";

    private readonly TempDirectory _temp = new();
    private readonly string _source;
    private readonly string _cache;

    public HelperCacheTests()
    {
        _source = _temp.Combine("build");
        _cache = _temp.Combine("cache");
        Directory.CreateDirectory(_source);
        File.WriteAllText(Path.Combine(_source, "godot_mcp_dotnet.gdextension"), "[configuration]\n");
        File.WriteAllText(Path.Combine(_source, "helper.dll"), "helper");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ACopyIsNamedByTheContentHashAndReused()
    {
        HelperCache cache = new(_cache);
        string first = cache.Prepare(_source);
        string folder = Path.GetDirectoryName(first)!;
        string name = Path.GetFileName(folder);

        Assert.Equal(16, name.Length);
        Assert.True(name.All(character => HexDigits.Contains(character)), $"'{name}' is not lowercase hex");
        Assert.True(File.Exists(first));

        string sentinel = Path.Combine(folder, "sentinel");
        File.WriteAllText(sentinel, "kept");

        Assert.Equal(first, cache.Prepare(_source));
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public void AChangedFileMakesANewCopy()
    {
        HelperCache cache = new(_cache);
        string first = cache.Prepare(_source);

        File.AppendAllText(Path.Combine(_source, "helper.dll"), "!");

        Assert.NotEqual(first, cache.Prepare(_source));
    }

    [Fact]
    public void AnIncompleteCopyIsRebuilt()
    {
        string folder = Path.Combine(_cache, HelperCache.Hash(_source));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "godot_mcp_dotnet.gdextension"), "stale");

        string path = new HelperCache(_cache).Prepare(_source);

        Assert.Equal(Path.Combine(folder, "godot_mcp_dotnet.gdextension"), path);
        Assert.Equal("[configuration]\n", File.ReadAllText(path));
    }

    [Fact]
    public void ACompleteCopyThatAppearsIsNotDeleted()
    {
        string destination = Path.Combine(_cache, HelperCache.Hash(_source));
        Directory.CreateDirectory(destination);
        string sentinel = Path.Combine(destination, "sentinel");
        File.WriteAllText(sentinel, "kept");
        File.WriteAllBytes(Path.Combine(destination, ".complete"), []);

        HelperCache.CopyInto(_source, destination);

        Assert.Equal("kept", File.ReadAllText(sentinel));
    }

    [Fact]
    public void TheFirstPreparePrunesOtherCopies()
    {
        string stale = Path.Combine(_cache, "0000000000000000");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "helper.dll"), "stale");
        string abandoned = Path.Combine(_cache, "abc.tmp-x");
        Directory.CreateDirectory(abandoned);

        new HelperCache(_cache).Prepare(_source);

        Assert.False(Directory.Exists(stale));
        Assert.False(Directory.Exists(abandoned));
    }

    [Fact]
    public void APruneSkipsAFolderItCannotDelete()
    {
        string held = Path.Combine(_cache, "0000000000000000");
        Directory.CreateDirectory(held);
        string locked = Path.Combine(held, "helper.dll");
        File.WriteAllText(locked, "held");

        using FileStream stream = new(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        using StreamReader reader = new(stream);

        string path = new HelperCache(_cache).Prepare(_source);

        Assert.True(File.Exists(path));
        Assert.True(Directory.Exists(held));
        Assert.Equal("held", reader.ReadToEnd());
    }

    [Fact]
    public async Task ACopyWhoseTempFolderIsHeldBrieflyStillLands()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        for (int i = 0; i < 200; i++)
        {
            File.WriteAllText(Path.Combine(_source, $"z{i:D4}.bin"), new string('x', 64));
        }
        Directory.CreateDirectory(_cache);
        string destination = Path.Combine(_cache, HelperCache.Hash(_source));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<string> scanner = Task.Run(() => HoldLikeAScanner(_cache, stop.Token), cancellation);

        string outcome;
        try
        {
            HelperCache.CopyInto(_source, destination);
        }
        finally
        {
            await stop.CancelAsync();
            outcome = await scanner;
        }

        Assert.Equal("held", outcome);
        Assert.True(File.Exists(Path.Combine(destination, ".complete")));
        Assert.True(File.Exists(Path.Combine(destination, "helper.dll")));
        Assert.Empty(Directory.GetDirectories(_cache, "*.tmp-*"));
    }

    [Fact]
    public async Task ACopyHeldPastTheBudgetFailsNamingTheCause()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        for (int i = 0; i < 200; i++)
        {
            File.WriteAllText(Path.Combine(_source, $"z{i:D4}.bin"), new string('x', 64));
        }
        Directory.CreateDirectory(_cache);
        string destination = Path.Combine(_cache, HelperCache.Hash(_source));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<string> holder = Task.Run(() => HoldUntilStopped(_cache, stop.Token), cancellation);

        InvalidOperationException thrown;
        string outcome;
        try
        {
            thrown = Assert.Throws<InvalidOperationException>(() => HelperCache.CopyInto(_source, destination));
        }
        finally
        {
            await stop.CancelAsync();
            outcome = await holder;
        }

        Assert.Equal("held", outcome);
        Assert.NotNull(thrown.InnerException);
        Assert.Equal(
            $"The C# helper's copy could not be moved into place at {destination}: {thrown.InnerException.Message}; "
                + "something (an antivirus scan?) held it for over 2 s.",
            thrown.Message
        );
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.GetDirectories(_cache, "*.tmp-*"));
    }

    // Opens helper.dll inside the first *.tmp-* folder that appears, for reading with full sharing, and keeps it open until
    // 300 ms after the folder's .complete marker appears: the shape of an on-write antivirus scan.
    private static string HoldLikeAScanner(string cache, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            string? temp = Directory.GetDirectories(cache, "*.tmp-*").FirstOrDefault();
            string? dll = temp is null ? null : Path.Combine(temp, "helper.dll");
            if (dll is null || !File.Exists(dll))
            {
                Thread.Sleep(1);
                continue;
            }

            using FileStream held = new(dll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string marker = Path.Combine(temp!, ".complete");
            while (!File.Exists(marker) && !stop.IsCancellationRequested)
            {
                Thread.Sleep(1);
            }

            Thread.Sleep(300);
            return "held";
        }

        return "never saw the temp folder";
    }

    // The same open, kept until the test stops it: a scan that outlasts the copy's retry budget.
    private static string HoldUntilStopped(string cache, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            string? temp = Directory.GetDirectories(cache, "*.tmp-*").FirstOrDefault();
            string? dll = temp is null ? null : Path.Combine(temp, "helper.dll");
            if (dll is null || !File.Exists(dll))
            {
                Thread.Sleep(1);
                continue;
            }

            using FileStream held = new(dll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stop.WaitHandle.WaitOne();
            return "held";
        }

        return "never saw the temp folder";
    }
}
