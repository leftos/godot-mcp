using System.Globalization;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

public sealed class ScratchUserDataTests : IDisposable
{
    /// <summary>A run that sets no project settings.</summary>
    private const string NoSettings = "";

    /// <summary>Session names Windows folds into one folder name: a trailing period dropped, and case ignored.</summary>
    private static readonly string[] FoldedNames = ["game", "game.", "Game", "GAME"];

    /// <summary>A server that has exited: this process's id with a start time it never had.</summary>
    private static readonly OverrideOwner Dead = new(Environment.ProcessId, OverrideOwner.Current.StartTicks - 1);

    private readonly TempDirectory _temp = new();
    private readonly StringWriter _errors = new();

    public void Dispose()
    {
        _errors.Dispose();
        _temp.Dispose();
    }

    /// <summary>
    /// A scratch folder of the name <see cref="ScratchUserData.RootFor"/> gives, under this test's own stand-in for the temp
    /// folder's <c>godot-mcp</c>.
    /// </summary>
    private string Root => _temp.Combine("temp", "godot-mcp", Path.GetFileName(ScratchUserData.RootFor(_temp.Combine("Probe"), "Probe")));

    private string GodotRoot => Root.Replace('\\', '/');

    [Fact]
    public void TheRootIsTheServersAndSessionsFolderUnderTheTempGodotMcpFolder()
    {
        string project = _temp.Combine("Probe");
        OverrideOwner owner = OverrideOwner.Current;

        string root = ScratchUserData.RootFor(project, "Probe");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "godot-mcp"), Path.GetDirectoryName(root));
        Assert.Matches($"^userdata-{owner.ProcessId}-{owner.StartTicks}-Probe-[0-9a-f]{{8}}$", Path.GetFileName(root));
        Assert.Equal(root, ScratchUserData.RootFor(project, "Probe"));
    }

    [Fact]
    public void NamesWindowsFoldsTogetherGetFoldersOfTheirOwn()
    {
        string project = _temp.Combine("Probe");
        string[] roots = [.. FoldedNames.Select(name => ScratchUserData.RootFor(project, name))];

        Assert.Equal(roots.Length, roots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(roots, root => Assert.False(root.EndsWith('.')));
    }

    [Fact]
    public void OnLinuxABackslashInTheRootIsKept()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only Windows turns '\\' into '/' in a data path.");
        string project = ProjectWith("[application]\n\nconfig/name=\"InputProbe\"\n");

        Assert.Equal(
            "/home/u/a\\b/root/godot/app_userdata/InputProbe",
            ScratchUserData.UserDataDir(project, "/home/u/a\\b/root", NoSettings, recording: false)
        );
    }

    [Fact]
    public void OnWindowsABackslashInTheRootBecomesASlash()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows turns '\\' into '/' in a data path.");
        string project = ProjectWith("[application]\n\nconfig/name=\"InputProbe\"\n");

        Assert.Equal("C:/u/a/root/Godot/app_userdata/InputProbe", ScratchUserData.UserDataDir(project, @"C:\u\a\root", NoSettings, recording: false));
    }

    [Fact]
    public void CreateEmptiesAFolderAlreadyThere()
    {
        string project = ProjectWith("[application]\n\nconfig/name=\"InputProbe\"\n");
        string stale = Path.Combine(ScratchUserData.UserDataDir(project, Root, NoSettings, recording: false), "stale.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "old");

        ScratchUserData.Create(project, Root, seed: null, NoSettings, recording: false);

        Assert.True(Directory.Exists(Root));
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void CreateUnlinksALinkAtTheRootAndLeavesItsTargetAlone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The link is a junction, made with mklink /J.");
        string project = ProjectWith("[application]\n\nconfig/name=\"InputProbe\"\n");
        string seed = _temp.Combine("Probe", "seed");
        Directory.CreateDirectory(seed);
        File.WriteAllText(Path.Combine(seed, "seed.txt"), "seeded");
        string target = _temp.Combine("elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "kept");
        Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
        ScratchUserData.Delete(Root);
        Links.MakeJunction(Root, target);

        string userDir = ScratchUserData.Create(project, Root, seed, NoSettings, recording: false);

        Assert.Null(new DirectoryInfo(Root).LinkTarget);
        Assert.Equal("seeded", File.ReadAllText(Path.Combine(userDir, "seed.txt")));
        Assert.Equal(["keep.txt"], Directory.GetFileSystemEntries(target).Select(Path.GetFileName));
    }

    [Fact]
    public void DeleteUnlinksALinkAtTheRootAndLeavesItsTargetAlone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The link is a junction, made with mklink /J.");
        string target = _temp.Combine("elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "kept");
        Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
        ScratchUserData.Delete(Root);
        Links.MakeJunction(Root, target);

        ScratchUserData.Delete(Root);

        Assert.False(Directory.Exists(Root));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void APlainNameIsTheGodotAppUserdataFolderUnderTheRoot()
    {
        string project = ProjectWith("[application]\n\nconfig/name=\"InputProbe\"\n");

        Assert.Equal(
            $"{GodotRoot}/{ScratchUserData.GodotDirName}/app_userdata/InputProbe",
            ScratchUserData.UserDataDir(project, Root, NoSettings, recording: false)
        );
    }

    [Fact]
    public void ANameIsMadeSafeAsGodotMakesIt()
    {
        string project = ProjectWith("[application]\n\nconfig/name=\" My: Game*?<>|/v1.. \"\n");

        Assert.Equal(
            $"{GodotRoot}/{ScratchUserData.GodotDirName}/app_userdata/My- Game------v1",
            ScratchUserData.UserDataDir(project, Root, NoSettings, recording: false)
        );
        Assert.Equal("a-b-c-d-e-f-g-h-i-j", ScratchUserData.SafeDirName(" a:b*c?d\"e<f>g|h/i\\j.. ", allowPaths: false));
        Assert.Equal("dot", ScratchUserData.SafeDirName(" . ", allowPaths: false));
        Assert.Equal("twodots", ScratchUserData.SafeDirName("..", allowPaths: false));
        Assert.Equal("saves/-/x-y", ScratchUserData.SafeDirName(" saves\\\\..\\x:y. ", allowPaths: true));
    }

    [Fact]
    public void ACustomUserDirIsTheCustomNameUnderTheRoot()
    {
        string custom = ProjectWith(
            "[application]\n\nconfig/name=\"InputProbe\"\nconfig/use_custom_user_dir=true\nconfig/custom_user_dir_name=\"Studio\\\\Game:1\"\n"
        );
        string unnamedCustom = ProjectWith("[application]\n\nconfig/name=\"InputProbe\"\nconfig/use_custom_user_dir=true\n", "Other");

        Assert.Equal($"{GodotRoot}/Studio/Game-1", ScratchUserData.UserDataDir(custom, Root, NoSettings, recording: false));
        Assert.Equal($"{GodotRoot}/InputProbe", ScratchUserData.UserDataDir(unnamedCustom, Root, NoSettings, recording: false));
    }

    [Fact]
    public void AMissingNameIsTheUnnamedProjectFolder()
    {
        string project = ProjectWith("[display]\n\nwindow/size/viewport_width=640\n");
        string blank = ProjectWith("[application]\n\nconfig/name=\" . \"\nconfig/use_custom_user_dir=true\n", "Blank");

        Assert.Equal(
            $"{GodotRoot}/{ScratchUserData.GodotDirName}/app_userdata/[unnamed project]",
            ScratchUserData.UserDataDir(project, Root, NoSettings, recording: false)
        );
        Assert.Equal($"{GodotRoot}/dot", ScratchUserData.UserDataDir(blank, Root, NoSettings, recording: false));
    }

    [Fact]
    public void CreateCopiesTheSeedsNestedFilesIntoTheUserDataFolder()
    {
        string project = ProjectWith("[application]\n\nconfig/name=\"InputProbe\"\n");
        string seed = _temp.Combine("Probe", "seed");
        Directory.CreateDirectory(Path.Combine(seed, "sub", "deeper"));
        Directory.CreateDirectory(Path.Combine(seed, "empty"));
        File.WriteAllText(Path.Combine(seed, "top.txt"), "top");
        File.WriteAllText(Path.Combine(seed, "sub", "deeper", "inner.cfg"), "inner");

        string userDir = ScratchUserData.Create(project, Root, seed, NoSettings, recording: false);

        Assert.Equal(ScratchUserData.UserDataDir(project, Root, NoSettings, recording: false), userDir);
        Assert.Equal("top", File.ReadAllText(Path.Combine(userDir, "top.txt")));
        Assert.Equal("inner", File.ReadAllText(Path.Combine(userDir, "sub", "deeper", "inner.cfg")));
        Assert.True(Directory.Exists(Path.Combine(userDir, "empty")));
    }

    [Fact]
    public void CreateWithoutASeedMakesOnlyTheRoot()
    {
        string project = ProjectWith("[application]\n\nconfig/name=\"InputProbe\"\n");

        ScratchUserData.Create(project, Root, seed: null, NoSettings, recording: false);

        Assert.True(Directory.Exists(Root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
    }

    [Fact]
    public void DeleteRemovesTheRootAndIgnoresAMissingOne()
    {
        string project = ProjectWith("[application]\n\nconfig/name=\"InputProbe\"\n");
        string userDir = ScratchUserData.Create(project, Root, seed: null, NoSettings, recording: false);
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "save.dat"), "x");

        ScratchUserData.Delete(Root);
        ScratchUserData.Delete(Root);

        Assert.False(Directory.Exists(Root));
        Assert.True(Directory.Exists(Path.GetDirectoryName(Root)));
    }

    [Fact]
    public void DeleteRefusesARootUnderALinkAndLeavesTheTargetsFolderAlone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The link is a junction, made with mklink /J.");
        string holder = _temp.Combine("temp");
        Directory.CreateDirectory(holder);
        string parent = Path.Combine(holder, "godot-mcp");
        string target = _temp.Combine("elsewhere");
        string name = Path.GetFileName(Root);
        Directory.CreateDirectory(Path.Combine(target, name));
        File.WriteAllText(Path.Combine(target, name, "keep.txt"), "kept");
        Links.MakeJunction(parent, target);

        IOException refused = Assert.Throws<IOException>(() => ScratchUserData.Delete(Path.Combine(parent, name)));

        Assert.Equal($"the scratch user data root {parent} is a link; not deleting {Path.Combine(parent, name)} through it", refused.Message);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(target, name, "keep.txt")));
    }

    [Fact]
    public void CreateRefusesARootUnderALink()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The link is a junction, made with mklink /J.");
        string project = ProjectWith("[application]\n\nconfig/name=\"InputProbe\"\n");
        string holder = _temp.Combine("temp");
        Directory.CreateDirectory(holder);
        string parent = Path.Combine(holder, "godot-mcp");
        string target = _temp.Combine("elsewhere");
        Directory.CreateDirectory(target);
        Links.MakeJunction(parent, target);
        string root = Path.Combine(parent, "userdata-Probe");

        SessionException refused = Assert.Throws<SessionException>(() =>
            ScratchUserData.Create(project, root, seed: null, NoSettings, recording: false)
        );

        Assert.Equal($"the scratch user data root {parent} is a link; remove it", refused.Message);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void SweepDeletesASessionFolderWhoseNamedOwnerHasExited()
    {
        string parent = TempScratchRoot();
        string folder = Path.Combine(parent, FolderOf(Dead, "my-game-2"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "save.dat"), "x");

        ScratchUserData.SweepLeftovers(parent, _errors);

        Assert.False(Directory.Exists(folder));
        Assert.Equal("", _errors.ToString());
    }

    [Fact]
    public void SweepKeepsASessionFolderItsNamedLiveOwnerStillUses()
    {
        string parent = TempScratchRoot();
        string folder = Path.Combine(parent, FolderOf(OverrideOwner.Current, "my-game-2"));
        Directory.CreateDirectory(folder);

        ScratchUserData.SweepLeftovers(parent, _errors);

        Assert.True(Directory.Exists(folder));
        Assert.Equal("", _errors.ToString());
    }

    [Fact]
    public void ASessionNameShapedLikeAnOwnerDoesNotMoveTheParsedOwner()
    {
        string parent = TempScratchRoot();
        string folder = Path.Combine(parent, FolderOf(OverrideOwner.Current, "1-2-x"));
        Directory.CreateDirectory(folder);

        ScratchUserData.SweepLeftovers(parent, _errors);

        Assert.True(Directory.Exists(folder));
        Assert.Equal("", _errors.ToString());
    }

    [Fact]
    public void SweepKeepsAFolderWhoseNameDoesNotNameItsServer()
    {
        string parent = TempScratchRoot();
        string folder = Path.Combine(parent, "userdata-Probe-00000000");
        Directory.CreateDirectory(folder);

        ScratchUserData.SweepLeftovers(parent, _errors);

        Assert.True(Directory.Exists(folder));
        Assert.Equal(
            $"godot-mcp: the scratch user data folder {folder} has a name that does not name its server; left in place{Environment.NewLine}",
            _errors.ToString()
        );
    }

    [Fact]
    public void SweepSkipsAParentThatIsALink()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The link is a junction, made with mklink /J.");
        string holder = _temp.Combine("temp");
        Directory.CreateDirectory(holder);
        string parent = Path.Combine(holder, "godot-mcp");
        string target = _temp.Combine("elsewhere");
        string leftover = Path.Combine(target, FolderOf(Dead, "Probe"));
        Directory.CreateDirectory(leftover);
        Links.MakeJunction(parent, target);

        ScratchUserData.SweepLeftovers(parent, _errors);

        Assert.True(Directory.Exists(leftover));
        Assert.Equal(
            $"godot-mcp: the scratch user data root {parent} is a link; leaving the folders in it alone{Environment.NewLine}",
            _errors.ToString()
        );
    }

    /// <summary>The scratch folder name a server <paramref name="owner"/> gives its session <paramref name="session"/>.</summary>
    private static string FolderOf(OverrideOwner owner, string session) =>
        string.Create(CultureInfo.InvariantCulture, $"userdata-{owner.ProcessId}-{owner.StartTicks}-{session}-0123abcd");

    [Fact]
    public void SweepUnlinksASessionFolderThatIsALinkAndLeavesItsTargetAlone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The link is a junction, made with mklink /J.");
        string parent = TempScratchRoot();
        string target = _temp.Combine("elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "kept");
        string folder = Path.Combine(parent, FolderOf(Dead, "Probe"));
        Links.MakeJunction(folder, target);

        ScratchUserData.SweepLeftovers(parent, _errors);

        Assert.False(Directory.Exists(folder));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    /// <summary>A scratch root of this test's own, standing in for the temp folder's <c>godot-mcp</c>.</summary>
    private string TempScratchRoot()
    {
        string parent = _temp.Combine("temp", "godot-mcp");
        Directory.CreateDirectory(parent);
        return parent;
    }

    [Fact]
    public void AFeatureTaggedCustomDirTheGameHasWins()
    {
        string project = ProjectWith(
            "[application]\n\nconfig/name=\"Game\"\nconfig/use_custom_user_dir=true\nconfig/custom_user_dir_name=\"Plain\"\n"
                + "config/custom_user_dir_name.editor=\"Dev\"\n"
        );

        Assert.Equal($"{GodotRoot}/Dev", ScratchUserData.UserDataDir(project, Root, NoSettings, recording: false));
    }

    [Fact]
    public void AFeatureTaggedNameForATagTheGameLacksIsIgnored()
    {
        string project = ProjectWith("[application]\n\nconfig/name=\"Game\"\nconfig/name.template=\"Shipped\"\n");

        Assert.Equal(AppUserdata("Game"), ScratchUserData.UserDataDir(project, Root, NoSettings, recording: false));
    }

    [Fact]
    public void TheFirstMatchingTaggedKeyInFileOrderWins()
    {
        string pcFirst = ProjectWith("[application]\n\nconfig/name=\"Game\"\nconfig/name.pc=\"A\"\nconfig/name.editor=\"B\"\n");
        string editorFirst = ProjectWith("[application]\n\nconfig/name=\"Game\"\nconfig/name.editor=\"B\"\nconfig/name.pc=\"A\"\n", "Other");

        Assert.Equal(AppUserdata("A"), ScratchUserData.UserDataDir(pcFirst, Root, NoSettings, recording: false));
        Assert.Equal(AppUserdata("B"), ScratchUserData.UserDataDir(editorFirst, Root, NoSettings, recording: false));
    }

    [Fact]
    public void AKeyWithTwoTagsMatchesEither()
    {
        string project = ProjectWith("[application]\n\nconfig/name=\"Game\"\nconfig/name.template.editor=\"E\"\n");

        Assert.Equal(AppUserdata("E"), ScratchUserData.UserDataDir(project, Root, NoSettings, recording: false));
    }

    [Fact]
    public void TheMovieTagCountsOnlyWhenTheRunRecords()
    {
        string project = ProjectWith("[application]\n\nconfig/name=\"Game\"\nconfig/name.movie=\"M\"\n");

        Assert.Equal(AppUserdata("M"), ScratchUserData.UserDataDir(project, Root, NoSettings, recording: true));
        Assert.Equal(AppUserdata("Game"), ScratchUserData.UserDataDir(project, Root, NoSettings, recording: false));
    }

    [Fact]
    public void TheRunsProjectSettingsOverrideTheFile()
    {
        string named = ProjectWith("[application]\n\nconfig/name=\"Game\"\n");
        string custom = ProjectWith(
            "[application]\n\nconfig/name=\"Game\"\nconfig/use_custom_user_dir=true\nconfig/custom_user_dir_name=\"Plain\"\n",
            "Other"
        );

        Assert.Equal(AppUserdata("FromRun"), ScratchUserData.UserDataDir(named, Root, Block("config/name=\"FromRun\"\n"), recording: false));
        Assert.Equal(
            $"{GodotRoot}/RunDev",
            ScratchUserData.UserDataDir(custom, Root, Block("config/custom_user_dir_name.editor=\"RunDev\"\n"), recording: false)
        );
    }

    [Fact]
    public void AFileTaggedKeyKeepsItsPlaceWhenTheRunSetsItAgain()
    {
        string both = ProjectWith("[application]\n\nconfig/name=\"Game\"\nconfig/name.pc=\"P\"\nconfig/name.editor=\"E\"\n");
        string editorOnly = ProjectWith("[application]\n\nconfig/name=\"Game\"\nconfig/name.editor=\"E\"\n", "Other");
        string block = Block("config/name.editor=\"E2\"\n");

        Assert.Equal(AppUserdata("P"), ScratchUserData.UserDataDir(both, Root, block, recording: false));
        Assert.Equal(AppUserdata("E2"), ScratchUserData.UserDataDir(editorOnly, Root, block, recording: false));
    }

    [Fact]
    public void EscapesInANameAreUndone()
    {
        string quoted = ProjectWith("[application]\n\nconfig/name=\"My \\\"Game\\\"\"\n", "Quoted");
        string backslash = ProjectWith("[application]\n\nconfig/name=\"A\\\\B\"\n", "Backslash");
        string unicode = ProjectWith("[application]\n\nconfig/name=\"Caf\\u00e9\"\n", "Unicode");
        string malformed = ProjectWith("[application]\n\nconfig/name=\"Caf\\u00zz\"\n", "Malformed");

        Assert.Equal(AppUserdata("My -Game-"), ScratchUserData.UserDataDir(quoted, Root, NoSettings, recording: false));
        Assert.Equal(AppUserdata("A-B"), ScratchUserData.UserDataDir(backslash, Root, NoSettings, recording: false));
        Assert.Equal(AppUserdata("Café"), ScratchUserData.UserDataDir(unicode, Root, NoSettings, recording: false));
        Assert.Equal(AppUserdata("[unnamed project]"), ScratchUserData.UserDataDir(malformed, Root, NoSettings, recording: false));
    }

    [Fact]
    public void UseCustomUserDirHonoursATaggedValue()
    {
        string project = ProjectWith(
            "[application]\n\nconfig/name=\"Game\"\nconfig/use_custom_user_dir=false\nconfig/use_custom_user_dir.editor=true\n"
                + "config/custom_user_dir_name=\"Custom\"\n"
        );

        Assert.Equal($"{GodotRoot}/Custom", ScratchUserData.UserDataDir(project, Root, NoSettings, recording: false));
    }

    [Fact]
    public void ReconcileKeepsTheServersFolderWhenTheGameAgreesOrSaysNothing()
    {
        Assert.Equal(("C:/a/Game", (string?)null), ScratchUserData.Reconcile("C:/a/Game", "C:/a/Game"));
        Assert.Equal(("C:/a/Game", (string?)null), ScratchUserData.Reconcile("C:/a/Game", null));
    }

    [Fact]
    public void ReconcileTakesTheGamesFolderAndWarnsWhenTheyDiffer()
    {
        Assert.Equal(
            (
                "C:/a/Game-dev",
                "The game reads its user data from C:/a/Game-dev, not C:/a/Game where godot-mcp made the scratch folder and copied any "
                    + "seed; set userData's seed or the project's custom_user_dir_name so they agree."
            ),
            ScratchUserData.Reconcile("C:/a/Game", "C:/a/Game-dev")
        );
    }

    /// <summary>The game's user data folder for a plain <paramref name="name"/>: Godot's app_userdata folder under the root.</summary>
    private string AppUserdata(string name) => $"{GodotRoot}/{ScratchUserData.GodotDirName}/app_userdata/{name}";

    /// <summary>A run's projectSettings block holding <paramref name="settings"/> under <c>[application]</c>.</summary>
    private static string Block(string settings) => $"{ProjectSettingsText.BlockMarker}\n\n[application]\n\n{settings}";

    /// <summary>A project folder under the temp folder whose project.godot holds <paramref name="settings"/>.</summary>
    private string ProjectWith(string settings, string name = "Probe")
    {
        string project = _temp.Combine(name);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "project.godot"), "config_version=5\n\n" + settings);
        return project;
    }
}
