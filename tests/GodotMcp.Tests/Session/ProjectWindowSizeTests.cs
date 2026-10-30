using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

/// <summary>What a project's project.godot says: one [section] key=value at a time, and the window size it lays a game out at.</summary>
public sealed class ProjectWindowSizeTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ReadsTheViewportSizeTheProjectSets() =>
        Assert.Equal(
            new WindowSize(1920, 1080),
            ProjectWindowSize.Read(Project(Display("window/size/viewport_width=1920\nwindow/size/viewport_height=1080\n")))
        );

    [Fact]
    public void ReadsAKeyOfAnySectionUnquoted() =>
        Assert.Equal("My Game", ProjectWindowSize.Read(Project("[application]\n\nconfig/name=\"My Game\"\n"), "application", "config/name"));

    [Fact]
    public void AProjectWithNoWindowSettingsTakesGodotsDefault() =>
        Assert.Equal(new WindowSize(1152, 648), ProjectWindowSize.Read(Project("config_version=5\n")));

    [Fact]
    public void AValueThatIsNotANumberIsTreatedAsAbsent() =>
        Assert.Equal(
            new WindowSize(1152, 900),
            ProjectWindowSize.Read(Project(Display("window/size/viewport_width=wide\nwindow/size/viewport_height=900\n")))
        );

    [Fact]
    public void AWindowOverrideOfZeroLeavesThatSidesViewportSize()
    {
        string settings = Display(
            "window/size/viewport_width=1920\nwindow/size/viewport_height=1080\n"
                + "window/size/window_width_override=1280\nwindow/size/window_height_override=0\n"
        );

        Assert.Equal(new WindowSize(1280, 1080), ProjectWindowSize.Read(Project(settings)));
    }

    [Fact]
    public void AWindowOverrideOnOneSideOnlyReplacesThatSide()
    {
        string settings = Display("window/size/viewport_width=1920\nwindow/size/viewport_height=1080\nwindow/size/window_width_override=1280\n");

        Assert.Equal(new WindowSize(1280, 1080), ProjectWindowSize.Read(Project(settings)));
    }

    [Fact]
    public void AProjectFolderWithNoProjectGodotTakesGodotsDefault() =>
        Assert.Equal(new WindowSize(1152, 648), ProjectWindowSize.Read(_temp.Combine("NoProject")));

    private static string Display(string settings) => "[display]\n\n" + settings;

    /// <summary>A project folder whose project.godot holds <paramref name="content"/>.</summary>
    private string Project(string content)
    {
        string projectDir = _temp.Combine("Game");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "project.godot"), content);
        return projectDir;
    }
}
