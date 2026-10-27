using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The one line that says Godot could not create a window: the ANGLE OpenGL failure, and not the warning that precedes a
/// working ANGLE fallback nor an unrelated line.
/// </summary>
public sealed class NoGlLineTests
{
    [Fact]
    public void TheAngleWindowErrorLineMatches() => Assert.True(NoGlLine.Matches("ERROR: Failed to create ANGLE OpenGL window."));

    [Fact]
    public void TheOpenGlVersionWarningAloneDoesNotMatch() =>
        Assert.False(NoGlLine.Matches("WARNING: Your video card drivers seem not to support the required OpenGL 3.3 version, switching to ANGLE."));

    [Fact]
    public void AnUnrelatedLineDoesNotMatch() => Assert.False(NoGlLine.Matches("Godot Engine v4.7.2.stable.official"));

    [Fact]
    public void TheLineInLowerCaseDoesNotMatch() => Assert.False(NoGlLine.Matches("ERROR: failed to create angle opengl window."));
}
