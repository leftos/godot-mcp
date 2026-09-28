using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>The naming rules on their own: the default name a folder gives, what a given name may be, and the sanitiser.</summary>
public sealed class SessionNameTests : IAsyncDisposable
{
    private const string NameRuleMessage = "a session name is 1 to 64 characters of letters, digits, '.', '_' and '-'";
    private readonly RegistryHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public void ADefaultNameIsTheProjectFolderName()
    {
        string projectPath = _harness.Combine("delve") + Path.DirectorySeparatorChar;

        Assert.Equal("delve", SessionRegistry.NameFor(null, projectPath));
        Assert.Equal("client-1", SessionRegistry.NameFor("client-1", projectPath));
    }

    [Fact]
    public void AnInvalidNameIsRefused()
    {
        foreach (string name in new[] { "a b", "", new string('a', 65), "client/1" })
        {
            SessionException refused = Assert.Throws<SessionException>(() => SessionRegistry.NameFor(name, _harness.Path));

            Assert.Equal($"session '{name}' is not a valid name: {NameRuleMessage}.", refused.Message);
        }
    }

    [Fact]
    public void ValidNamesPass()
    {
        foreach (string name in new[] { "server", "client-1", "a.b_C", new string('a', 64) })
        {
            Assert.Equal(name, SessionRegistry.NameFor(name, _harness.Path));
        }
    }

    [Fact]
    public void AFolderNameOutsideTheRuleIsSanitised()
    {
        string projectPath = _harness.Combine("My Game!") + Path.DirectorySeparatorChar;

        string name = SessionRegistry.NameFor(null, projectPath);

        Assert.Equal("My_Game_", name);
        Assert.Matches("^[A-Za-z0-9._-]{1,64}$", name);
    }

    [Fact]
    public void ANonAsciiFolderNameIsSanitised()
    {
        string projectPath = _harness.Combine("Café");

        string name = SessionRegistry.NameFor(null, projectPath);

        Assert.Equal("Caf_", name);
        Assert.Matches("^[A-Za-z0-9._-]{1,64}$", name);
    }

    [Fact]
    public void ALongFolderNameIsCutTo64()
    {
        string projectPath = _harness.Combine(new string('a', 66) + "STOP");

        string name = SessionRegistry.NameFor(null, projectPath);

        Assert.Equal(new string('a', 64), name);
        Assert.Matches("^[A-Za-z0-9._-]{1,64}$", name);
    }

    [Fact]
    public void AGivenSessionIsStillRefusedNotSanitised()
    {
        SessionException refused = Assert.Throws<SessionException>(() => SessionRegistry.NameFor("my game", _harness.Path));

        Assert.Equal($"session 'my game' is not a valid name: {NameRuleMessage}.", refused.Message);
    }

    [Fact]
    public void APreviewNameFitsTheRule()
    {
        string name = SessionRegistry.PreviewName(new string('a', 64), 12);

        Assert.Equal(new string('a', 53) + ".preview-12", name);
        Assert.True(name.Length <= 64, $"'{name}' is {name.Length} characters");
        Assert.EndsWith(".preview-12", name, StringComparison.Ordinal);
        Assert.Matches("^[A-Za-z0-9._-]{1,64}$", name);
    }
}
