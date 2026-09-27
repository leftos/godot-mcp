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
}
