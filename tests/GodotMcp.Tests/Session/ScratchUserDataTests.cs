using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

public sealed class ScratchUserDataTests : IDisposable
{
    /// <summary>A run that sets no project settings.</summary>
    private const string NoSettings = "";

    /// <summary>Session names Windows folds into one folder name: a trailing period dropped, and case ignored.</summary>
    private static readonly string[] FoldedNames = ["game", "game.", "Game", "GAME"];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Root => _temp.Combine("Probe", ".godot", "godot-mcp", "userdata-Probe");

    private string GodotRoot => Root.Replace('\\', '/');

    [Fact]
    public void TheRootIsTheServersAndSessionsFolderUnderTheProjectsGodotMcpFolder()
    {
        string project = _temp.Combine("Probe");

        string root = ScratchUserData.RootFor(project, "Probe");

        Assert.Equal(Path.Combine(project, ".godot", "godot-mcp"), Path.GetDirectoryName(root));
        Assert.Matches($"^userdata-{Environment.ProcessId}-Probe-[0-9a-f]{{8}}$", Path.GetFileName(root));
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
        MakeJunction(Root, target);

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
        MakeJunction(Root, target);

        ScratchUserData.Delete(Root);

        Assert.False(Directory.Exists(Root));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    /// <summary>Makes <paramref name="link"/> a junction to <paramref name="target"/>.</summary>
    internal static void MakeJunction(string link, string target)
    {
        using System.Diagnostics.Process mklink = System.Diagnostics.Process.Start("cmd", ["/c", "mklink", "/J", link, target])!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
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
