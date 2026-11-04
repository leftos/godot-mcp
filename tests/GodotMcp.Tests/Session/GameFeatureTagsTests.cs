using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

public sealed class GameFeatureTagsTests
{
    private static readonly string PlatformTag = OperatingSystem.IsWindows() ? "windows" : "linux";

    [Fact]
    public void AGameRunOfTheEditorBuildHasItsTagsAndNoOthers()
    {
        string[] present = ["editor", "editor_runtime", "debug", "pc", PlatformTag];
        string[] absent = ["editor_hint", "template", "release", "embedded_in_editor", "movie"];

        Assert.All(present, tag => Assert.True(GameFeatureTags.Has(tag, recording: false), tag));
        Assert.All(absent, tag => Assert.False(GameFeatureTags.Has(tag, recording: false), tag));
    }

    [Fact]
    public void TheMovieTagIsPresentOnlyWhenTheRunRecords() => Assert.True(GameFeatureTags.Has("movie", recording: true));
}
