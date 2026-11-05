using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// The res:// paths validate attaches to a .cs target from a scene or resource file: the <c>[ext_resource</c> line of a .tscn,
/// .tres, .xscn or .xres, and in a godot-xaml .xscn or .xres every whole attribute value that is a res:// path; no Godot runs here.
/// </summary>
public sealed class HeadlessAttachingTests
{
    /// <summary>The lines of career-driver's PausePrompt.xscn, which attaches two scripts and a theme.</summary>
    private static readonly string[] CareerDriver =
    [
        "<Control x:Name=\"Prompt\" Script=\"res://Scripts/Input/PausePrompt.cs\">",
        "    <x:Uid Path=\"res://Scripts/Input/PausePrompt.cs\" Value=\"uid://cugf44aecs1sx\" />",
        "      <Control x:Name=\"PauseMenu\" Script=\"res://Scripts/Settings/PauseMenu.cs\"",
        "      Theme=\"res://Scenes/Settings/Settings.xres\"",
    ];

    [Fact]
    public void ACareerDriverXscnYieldsTheThreePathsItsAttributesName() =>
        Assert.Equal(
            ["res://Scripts/Input/PausePrompt.cs", "res://Scripts/Settings/PauseMenu.cs", "res://Scenes/Settings/Settings.xres"],
            HeadlessTools.AttachedPaths(".xscn", CareerDriver)
        );

    [Theory]
    [InlineData(".xscn", "Script='res://A.cs'", "res://A.cs")]
    [InlineData(".xscn", "Script = \"res://A.cs\"", "res://A.cs")]
    [InlineData(".xres", "Path = 'res://Data.xres'", "res://Data.xres")]
    public void AWholeAttributeValueIsFoundWhateverItsQuotesAndSpacing(string extension, string line, string path) =>
        Assert.Equal([path], HeadlessTools.AttachedPaths(extension, [line]));

    [Theory]
    [InlineData(".xscn", "Text=\"see res://A.cs\"")]
    [InlineData(".xres", "Text='see res://A.cs'")]
    [InlineData(".tscn", "Script=\"res://A.cs\"")]
    public void APathInALongerValueOrOutsideAnExtResourceIsNotAnAttachment(string extension, string line) =>
        Assert.Empty(HeadlessTools.AttachedPaths(extension, [line]));

    [Theory]
    [InlineData(".tscn")]
    [InlineData(".xscn")]
    public void AnExtResourceLineIsFoundInBothFormats(string extension) =>
        Assert.Equal(
            ["res://CsProbeNode.cs"],
            HeadlessTools.AttachedPaths(
                extension,
                ["[gd_scene load_steps=2 format=3]", "", "[ext_resource type=\"Script\" path=\"res://CsProbeNode.cs\" id=\"1\"]"]
            )
        );
}
