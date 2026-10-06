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

    [Fact]
    public async Task ADefaultNameHeldLiveOnAnotherFolderIsNumbered()
    {
        string one = _harness.Project(Path.Combine("one", "Sky.Client"));
        string two = _harness.Project(Path.Combine("two", "Sky.Client"));
        string three = _harness.Project(Path.Combine("three", "Sky.Client"));

        string first = await StartDefaultAttachAsync(one);
        string second = await StartDefaultAttachAsync(two);
        string third = await StartDefaultAttachAsync(three);

        Assert.Equal(["Sky.Client", "Sky.Client-2", "Sky.Client-3"], [first, second, third]);
    }

    [Fact]
    public async Task ADefaultNameHeldLiveOnTheSameFolderIsStillRefused()
    {
        string one = _harness.Project(Path.Combine("one", "Sky.Client"));
        await StartDefaultAttachAsync(one);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(
                new AttachRequest(one, null, RegistryHarness.LongWait, false, false, null),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(LiveSkyClientRefusal(one), refused.Message);
    }

    [Fact]
    public async Task AGivenNameHeldLiveIsRefusedNotNumbered()
    {
        string one = _harness.Project(Path.Combine("one", "Sky.Client"));
        string two = _harness.Project(Path.Combine("two", "Sky.Client"));
        await StartDefaultAttachAsync(one);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(
                new AttachRequest(two, "Sky.Client", RegistryHarness.LongWait, false, false, null),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(LiveSkyClientRefusal(one), refused.Message);
    }

    [Fact]
    public async Task ANumberedNameOfALongFolderFitsTheRule()
    {
        string folder = new('a', 64);
        string one = _harness.Project(Path.Combine("one", folder));
        string two = _harness.Project(Path.Combine("two", folder));
        await StartDefaultAttachAsync(one);

        string name = await StartDefaultAttachAsync(two);

        Assert.Equal(new string('a', 62) + "-2", name);
        Assert.Matches("^[A-Za-z0-9._-]{1,64}$", name);
    }

    [Fact]
    public async Task ANumberedNameWhoseSessionEndedIsReused()
    {
        string one = _harness.Project(Path.Combine("one", "Sky.Client"));
        string two = _harness.Project(Path.Combine("two", "Sky.Client"));
        string three = _harness.Project(Path.Combine("three", "Sky.Client"));
        await StartDefaultAttachAsync(one);
        await _harness.EndAttachedGameAsync(two, "Sky.Client-2");

        string name = await StartDefaultAttachAsync(three);

        Assert.Equal("Sky.Client-2", name);
    }

    [Fact]
    public async Task ScratchNamesThatClashAreNumbered()
    {
        string one = _harness.Project(Path.Combine("one", "Sky.Client"));
        string two = _harness.Project(Path.Combine("two", "Sky.Client"));
        string longScene = new('s', 55);

        string[] names =
        [
            (await ReserveScratchAsync(one, "Tray", "res://s/Tray.tscn")).Name,
            (await ReserveScratchAsync(one, "Tray", "res://s/Tray.tscn")).Name,
            (await ReserveScratchAsync(two, "Tray", "res://s/Tray.tscn")).Name,
            (await ReserveScratchAsync(one, "a b", "res://s/a b.tscn")).Name,
            (await ReserveScratchAsync(one, "a_b", "res://s/a_b.tscn")).Name,
            (await ReserveScratchAsync(one, longScene + "x", $"res://s/{longScene}x.tscn")).Name,
            (await ReserveScratchAsync(one, longScene + "y", $"res://s/{longScene}y.tscn")).Name,
        ];

        Assert.Equal(
            [
                "Sky.Client.scratch-Tray",
                "Sky.Client.scratch-Tray-2",
                "Sky.Client.scratch-Tray-3",
                "Sky.Client.scratch-a_b",
                "Sky.Client.scratch-a_b-2",
                ".scratch-" + longScene,
                ".scratch-" + longScene[..53] + "-2",
            ],
            names
        );
    }

    [Fact]
    public async Task AScratchRunNeverReplacesASessionStartedUnderItsNameSince()
    {
        string one = _harness.Project(Path.Combine("one", "Sky.Client"));
        GodotSession scratch = await ReserveScratchAsync(one, "Tray", "res://s/Tray.tscn");
        _harness.Sessions.Forget(scratch);
        await _harness.EndAttachedGameAsync(one, "Sky.Client.scratch-Tray");

        GodotSession again = await ReserveScratchAsync(one, "Tray", "res://s/Tray.tscn");

        Assert.Equal("Sky.Client.scratch-Tray-2", again.Name);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(false, "one|res://s/Tray.tscn", true)]
    [InlineData(false, "one|res://s/Other.tscn", false)]
    [InlineData(false, null, false)]
    [InlineData(true, "one|res://s/Tray.tscn", false)]
    public void AScratchSceneTakesAFreeNameOrItsOwnStoppedOne(bool? holderLive, string? holderScene, bool takes) =>
        Assert.Equal(takes, SessionRegistry.MayTakeScratchName(holderLive, holderScene, "one|res://s/Tray.tscn"));

    /// <summary>Reserves a scratch scene's session under the folder's own name, the prefix a run without options.session uses.</summary>
    private Task<GodotSession> ReserveScratchAsync(string projectDir, string scene, string resPath) =>
        _harness.Sessions.ReserveScratchAsync(projectDir, scene, resPath, SessionRegistry.NameFor(null, projectDir));

    /// <summary>The refusal of a start under 'Sky.Client' while a session on <paramref name="holderDir"/> holds it live.</summary>
    private static string LiveSkyClientRefusal(string holderDir) =>
        $"A session named 'Sky.Client' is live on {ProjectPaths.Normalise(holderDir)}; stop_project or detach_project it, "
        + "or pass another session name.";

    /// <summary>Starts an attach with no session name that stays waiting for its game; returns the name the registry gave it.</summary>
    private async Task<string> StartDefaultAttachAsync(string projectDir)
    {
        CancellationTokenSource cancel = new();
        Task attach = _harness.Sessions.AttachAsync(new AttachRequest(projectDir, null, RegistryHarness.LongWait, false, false, null), cancel.Token);
        _harness.Waiting.Add((attach, cancel));
        SessionInfo? started = null;
        await RegistryHarness.WaitUntilAsync(() =>
        {
            started = _harness.Sessions.List(includeStopped: false).FirstOrDefault(session => ProjectPaths.AreSame(session.ProjectPath, projectDir));
            return started is not null;
        });
        return started!.Name;
    }
}
